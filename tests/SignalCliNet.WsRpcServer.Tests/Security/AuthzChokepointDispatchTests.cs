using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Nerdbank.Streams;
using SignalCli.Models.Signal.Accounts;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Serialization;
using StreamJsonRpc;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// DoD-тести Фази 2 для <c>add-authz-chokepoint</c> (tasks 4.1–4.5): справжній диспетч
/// клієнт ↔ <see cref="AuthorizingSignalJsonRpc"/> через in-memory duplex-стрім — перевіряємо
/// поведінку chokepoint'а на реальному шляху StreamJsonRpc, а не на helper'ах.
/// </summary>
/// <remarks>
/// Мапінг помилок StreamJsonRpc на клієнті: <c>-32601</c> і <c>-32602</c> прилітають як
/// <see cref="RemoteMethodNotFoundException"/>/<see cref="RemoteRpcException"/> (без ErrorCode),
/// <c>-32001</c> — як <see cref="RemoteInvocationException"/> з <c>ErrorCode</c>. Тому «не виконано»
/// скрізь додатково пінується прапорцем на fake-таргеті — це і є суть DoD («без виконання»,
/// «у signal-cli не потрапляє»).
/// </remarks>
public sealed class AuthzChokepointDispatchTests : IDisposable
{
    private const string AccountA = "+380111111111";
    private const string AccountB = "+380222222222";

    private readonly List<IDisposable> _cleanup = [];

    public void Dispose()
    {
        foreach (IDisposable d in _cleanup)
            d.Dispose();
    }

    // ---------- 4.1 Default-deny ----------

    [Fact]
    public async Task Task41_RegisteredMethodWithoutPolicy_IsDeniedWithoutExecution()
    {
        var target = new UnclassifiedTarget();
        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target);

        // Метод зареєстрований у таргеті, але БЕЗ рядка політики → -32601 ще до виконання тіла.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(
            () => client.InvokeAsync<string>("danger"));

        Assert.False(target.Executed, "Тіло методу без політики НЕ сміє виконатись (default-deny).");
    }

    // ---------- 4.2 Inbound IDOR ----------

    [Fact]
    public async Task Task42_PrincipalA_NamingAccountB_IsDenied_TargetNotReached()
    {
        var target = new MessageTarget();
        var client = CreatePair(UserPrincipal(AccountA), target);

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.InvokeWithParameterObjectAsync<string>("sendTextMessage", new
            {
                account = AccountB,
                recipients = new[] { AccountA },
                message = "hi",
            }));

        Assert.Equal(AccountIsolationGuard.UnauthorizedErrorCode, ex.ErrorCode);
        // Anti-enumeration: generic-текст без натяку на існування акаунта B.
        Assert.DoesNotContain(AccountB, ex.Message, StringComparison.Ordinal);
        Assert.False(target.Invoked, "IDOR-виклик не сміє дійти до адаптера/signal-cli.");
    }

    [Fact]
    public async Task Task42_PrincipalA_OwnAccount_Passes()
    {
        var target = new MessageTarget();
        var client = CreatePair(UserPrincipal(AccountA), target);

        var result = await client.InvokeWithParameterObjectAsync<string>("sendTextMessage", new
        {
            account = AccountA,
            recipients = new[] { AccountB },
            message = "hi",
        });

        Assert.Equal("sent", result);
        Assert.True(target.Invoked);
        Assert.Equal(AccountA, target.LastAccount);
    }

    // ---------- 4.3 Outbound IDOR ----------

    [Fact]
    public async Task Task43_ListAccounts_UserSeesOnlyOwn()
    {
        var target = new AccountsTarget(AccountA, AccountB);
        var client = CreatePair(UserPrincipal(AccountA), target);

        var response = await client.InvokeAsync<ListAccountsResponse>("listAccounts");

        Assert.Equal([AccountA], response.Items.Select(a => a.Number));
    }

    [Fact]
    public async Task Task43_ListAccounts_AdminSeesAll_R36()
    {
        var target = new AccountsTarget(AccountA, AccountB);
        // R3.6: admin = read-only нагляд (IsAdmin), БЕЗ операційного full-access.
        var admin = new SignalPrincipal("admin", isAdmin: true, hasFullAccess: false, [], []);
        var client = CreatePair(admin, target);

        var response = await client.InvokeAsync<ListAccountsResponse>("listAccounts");

        Assert.Equal(2, response.Items.Count);
    }

    [Fact]
    public async Task Task43_ListGroups_UserSeesOnlyClaimedOwn_R32()
    {
        var target = new GroupsTarget("grp-1", "grp-2");
        var client = CreatePair(UserPrincipal(AccountA, "grp-1"), target);

        var response = await client.InvokeWithParameterObjectAsync<ListGroupsResponse>(
            "listGroups", new { account = AccountA });

        Assert.Equal(["grp-1"], response.Items.Select(g => g.Id));
    }

    [Fact]
    public async Task Task43_ListGroups_AdminGetsNoSpecialGroupVisibility_R32()
    {
        var target = new GroupsTarget("grp-1", "grp-2");
        // Admin з правом оперувати AccountA (щоб пройти inbound-guard), але без claim'ів груп:
        // R3.2 — admin НЕ має спец-видимості груп → порожній результат.
        var admin = new SignalPrincipal("admin", isAdmin: true, hasFullAccess: false, [AccountA], []);
        var client = CreatePair(admin, target);

        var response = await client.InvokeWithParameterObjectAsync<ListGroupsResponse>(
            "listGroups", new { account = AccountA });

        Assert.Empty(response.Items);
    }

    // ---------- 4.4 V8: ізоляція identity через спільний адаптер ----------

    [Fact]
    public async Task Task44_TwoConnections_SharedTarget_SeeOwnPrincipalOnly()
    {
        // ОДИН спільний (root-resolved) таргет — як спільні адаптери в DI (V8).
        var shared = new PrincipalEchoTarget();
        var clientA = CreatePair(UserPrincipal(AccountA), shared, principalName: "identity-A");
        var clientB = CreatePair(UserPrincipal(AccountB), shared, principalName: "identity-B");

        // Перемішані конкурентні виклики: кожен диспетч мусить бачити СВІЙ principal з call-context.
        var tasks = Enumerable.Range(0, 16).Select(i => i % 2 == 0
            ? clientA.InvokeAsync<string>("syncAccount")
            : clientB.InvokeAsync<string>("syncAccount"));
        string[] results = await Task.WhenAll(tasks);

        for (int i = 0; i < results.Length; i++)
            Assert.Equal(i % 2 == 0 ? "identity-A" : "identity-B", results[i]);

        // Поза диспетчем ambient principal не «протікає» у сторонній async-контекст.
        Assert.Null(SignalCallContext.Principal);
    }

    // ---------- 1.5 D9: per-dispatch credential re-check ----------

    [Fact]
    public async Task Task15_DeadCredential_RefusedBeforePolicy_TargetNotReached()
    {
        var target = new MessageTarget();
        // isCredentialLive=false ⇒ revoked/expired токен-сесія: та сама відмова, що й default-deny.
        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target, isCredentialLive: () => false);

        // sendTextMessage МАЄ політику й пройшов би IDOR (loopback), але мертвий креденшел рубає до неї.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(
            () => client.InvokeWithParameterObjectAsync<string>("sendTextMessage", new
            {
                account = AccountA,
                recipients = new[] { AccountB },
                message = "hi",
            }));

        Assert.False(target.Invoked, "Мертвий креденшел не сміє дійти до адаптера/signal-cli (D9).");
    }

    [Fact]
    public async Task Task15_LiveCredential_Passes()
    {
        var target = new MessageTarget();
        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target, isCredentialLive: () => true);

        var result = await client.InvokeWithParameterObjectAsync<string>("sendTextMessage", new
        {
            account = AccountA,
            recipients = new[] { AccountB },
            message = "hi",
        });

        Assert.Equal("sent", result);
        Assert.True(target.Invoked);
    }

    // ---------- 4.5 D11/S6: malformed recipient ----------

    [Fact]
    public async Task Task45_MalformedRecipient_RejectedAtChokepoint_NeverReachesTarget()
    {
        var target = new MessageTarget();
        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target);

        await Assert.ThrowsAnyAsync<RemoteRpcException>(
            () => client.InvokeWithParameterObjectAsync<string>("sendTextMessage", new
            {
                account = AccountA,
                recipients = new[] { "not-a-number" },
                message = "hi",
            }));

        Assert.False(target.Invoked, "Малформований recipient не сміє дійти до signal-cli (S6).");
    }

    [Fact]
    public void Task45_BrokenGroupId_IsRejectedByValidator()
    {
        // Group-send поверхні ще немає; валідатор group-ID існує наперед — пінуємо його контракт.
        Assert.False(RecipientValidator.IsValidGroupId("битий/ідентифікатор!"));
        Assert.False(RecipientValidator.IsValidGroupId(""));
    }

    // ---------- 3.2/4.1 Admission (W16): send-deny → -32005 + data.retry_after ----------

    [Fact]
    public async Task Admission_DeniesSend_Returns32005_WithInBandRetryAfter_TargetNotReached()
    {
        var target = new MessageTarget();
        var admissionCalled = false;
        // admitSends деніїть із retry_after=42 (як AggregateBudget-deny).
        Func<IReadOnlyList<string>, AdmissionDecision> deny = _ =>
        {
            admissionCalled = true;
            return new AdmissionDecision(false, AdmissionDenyReason.AggregateBudget, 42);
        };
        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target, admitSends: deny);

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.InvokeWithParameterObjectAsync<string>("sendTextMessage", new
            {
                account = AccountA,
                recipients = new[] { AccountB },
                message = "hi",
            }));

        Assert.True(admissionCalled, "Send-метод МУСИТЬ проходити admission.");
        Assert.Equal(AdmissionErrorCodes.RateLimited, ex.ErrorCode); // -32005
        // Повідомлення без PII (recipients/тіла).
        Assert.DoesNotContain(AccountB, ex.Message, StringComparison.Ordinal);
        Assert.False(target.Invoked, "Відхилений admission-ом send не сміє дійти до signal-cli.");
        // NB: сам drіт-контракт error.data={"retry_after":N} пінить окремий серіалізаційний тест нижче —
        // .NET-клієнт StreamJsonRpc за замовчуванням десеріалізує custom error.data у CommonErrorData
        // (втрачаючи retry_after), тож round-trip тут перевіряє КОД, а не форму data (її бачить raw-WS клієнт).
    }

    [Fact]
    public void RateLimitErrorData_WireShape_IsSnakeCaseRetryAfter()
    {
        // Форматтер дзеркалить продакшн (camelCase-політика + source-gen контекст). [JsonPropertyName]
        // фіксує snake_case retry_after на дроті — саме це читає браузерний WS-клієнт in-band (S9).
        var options = CreateFormatter().JsonSerializerOptions;

        var json = JsonSerializer.Serialize(new RateLimitErrorData(42), options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("retry_after").GetInt32());
    }

    [Fact]
    public async Task Admission_AdmitsSend_Passes_TargetReached()
    {
        var target = new MessageTarget();
        Func<IReadOnlyList<string>, AdmissionDecision> allow = _ => AdmissionDecision.Allow;
        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target, admitSends: allow);

        var result = await client.InvokeWithParameterObjectAsync<string>("sendTextMessage", new
        {
            account = AccountA,
            recipients = new[] { AccountB },
            message = "hi",
        });

        Assert.Equal("sent", result);
        Assert.True(target.Invoked);
    }

    [Fact]
    public async Task Admission_NotConsulted_ForNonSendMethod()
    {
        var admissionCalled = false;
        Func<IReadOnlyList<string>, AdmissionDecision> spy = _ =>
        {
            admissionCalled = true;
            return AdmissionDecision.Allow;
        };
        // listAccounts не має recipients-аргумента у політиці → admission НЕ викликається.
        var target = new AccountsTarget(AccountA);
        var client = CreatePair(UserPrincipal(AccountA), target, admitSends: spy);

        await client.InvokeAsync<ListAccountsResponse>("listAccounts");

        Assert.False(admissionCalled, "Non-send метод не сміє консультувати admission (немає recipients).");
    }

    // ---------- 3.3 In-flight cap (D12) + G6 signal-cli гейт → -32005 ----------

    [Fact]
    public async Task InFlightCap_ExcessConcurrentSend_Returns32005_AndCounterReturns()
    {
        var target = new BlockingMessageTarget();
        // Cap=2: два паралельні send зависають у таргеті, третій — понад cap → -32005.
        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target, maxInFlight: 2);

        var t1 = client.InvokeWithParameterObjectAsync<string>("sendTextMessage", SendArgs());
        var t2 = client.InvokeWithParameterObjectAsync<string>("sendTextMessage", SendArgs());
        var t3 = client.InvokeWithParameterObjectAsync<string>("sendTextMessage", SendArgs());

        // Чекаємо, поки рівно 2 виклики увійдуть у таргет (тримають in-flight слоти).
        await WaitUntilAsync(() => Volatile.Read(ref target.Entered) == 2);

        // Рівно один із трьох отримує -32005 (понад cap), не дійшовши до таргета.
        var rejected = await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
            await Task.WhenAny(t1, t2, t3).Unwrap());
        Assert.Equal(AdmissionErrorCodes.RateLimited, rejected.ErrorCode);
        Assert.Equal(2, Volatile.Read(ref target.Entered)); // третій таргета не торкнувся

        // Відпускаємо блоковані — вони завершуються успішно, лічильник повертається.
        target.Release();
        var results = await Task.WhenAll(
            new[] { t1, t2, t3 }.Select(async t =>
            {
                try { return await t; }
                catch (RemoteInvocationException) { return "rejected"; }
            }));
        Assert.Equal(2, results.Count(r => r == "sent"));
        Assert.Equal(1, results.Count(r => r == "rejected"));

        // Лічильник повернувся: after Release() таргет більше не блокує, а in-flight = 0 —
        // черговий send на тій самій сесії проходить попри той самий cap=2.
        var ok = await client.InvokeWithParameterObjectAsync<string>("sendTextMessage", SendArgs());
        Assert.Equal("sent", ok);
    }

    [Fact]
    public async Task SignalCliGate_WhenFull_Returns32005_TargetNotReached()
    {
        var target = new MessageTarget();
        // Гейт на 1 слот, який ми зайняли ЗЗОВНІ — будь-який не-Public dispatch застає його повним.
        using var gate = new SignalCliGate(Options.Create(new AdmissionOptions { SignalCliConcurrencyLimit = 1 }));
        Assert.True(gate.TryEnter()); // окупуємо єдиний слот

        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target, signalCliGate: gate);

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.InvokeWithParameterObjectAsync<string>("sendTextMessage", SendArgs()));

        Assert.Equal(AdmissionErrorCodes.RateLimited, ex.ErrorCode);
        Assert.False(target.Invoked, "Зайнятий G6-гейт не сміє пускати dispatch до signal-cli.");
    }

    [Fact]
    public async Task PublicMethod_BypassesGate_EvenWhenFull()
    {
        var target = new PingTarget();
        // Гейт повний, in-flight cap=1 — Public-метод (ping) МУСИТЬ пройти повз обидва.
        using var gate = new SignalCliGate(Options.Create(new AdmissionOptions { SignalCliConcurrencyLimit = 1 }));
        Assert.True(gate.TryEnter());

        var client = CreatePair(
            SignalPrincipal.LoopbackFullAccess, target, signalCliGate: gate, maxInFlight: 1);

        var result = await client.InvokeAsync<string>("ping");

        Assert.Equal("pong", result);
    }

    // ---------- 3.1 Admin-політика (add-invites-admin): не-admin → -32001, admin → проходить ----------

    [Fact]
    public async Task Admin_NonAdminPrincipal_OnAdminMethod_Denied32001_TargetNotReached()
    {
        var target = new AdminTarget();
        // Loopback (IsAdmin=false) — admin-метод має відмовити -32001 ще до виконання тіла.
        var client = CreatePair(SignalPrincipal.LoopbackFullAccess, target);

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.InvokeAsync<string>("createInvite"));

        Assert.Equal(AccountIsolationGuard.UnauthorizedErrorCode, ex.ErrorCode); // -32001
        Assert.False(target.Invoked, "Не-admin не сміє дійти до admin-адаптера.");
    }

    [Fact]
    public async Task Admin_AdminPrincipal_OnAdminMethod_Passes()
    {
        var target = new AdminTarget();
        var admin = new SignalPrincipal("admin", isAdmin: true, hasFullAccess: false, [], []);
        var client = CreatePair(admin, target);

        var result = await client.InvokeAsync<string>("createInvite");

        Assert.Equal("created", result);
        Assert.True(target.Invoked);
    }

    [Fact]
    public async Task Admin_NonAdmin_OnRevokeIdentity_Denied32001()
    {
        var target = new AdminTarget();
        var user = UserPrincipal(AccountA);
        var client = CreatePair(user, target);

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.InvokeWithParameterObjectAsync<string>("revokeIdentity", new { identityId = "victim" }));

        Assert.Equal(AccountIsolationGuard.UnauthorizedErrorCode, ex.ErrorCode);
        Assert.False(target.Invoked);
    }

    // ---------- Harness ----------

    private static object SendArgs() => new
    {
        account = AccountA,
        recipients = new[] { AccountB },
        message = "hi",
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Умова не настала у відведений час.");
            await Task.Delay(10);
        }
    }

    private static SignalPrincipal UserPrincipal(string account, params string[] groups) =>
        new($"user-{account[^4..]}", isAdmin: false, hasFullAccess: false, [account], groups);

    /// <summary>
    /// Піднімає пару клієнт ↔ <see cref="AuthorizingSignalJsonRpc"/> над in-memory duplex-стрімом.
    /// Формуттер дзеркалить <c>SignalRpcSession.CreateJsonFormatter</c> (camelCase + source-gen
    /// контекст із reflection-фолбеком), таргет реєструється з camelCase-трансформом імен —
    /// як у продакшн-registry.
    /// </summary>
    private JsonRpc CreatePair(
        SignalPrincipal principal, object target, string? principalName = null, Func<bool>? isCredentialLive = null,
        Func<IReadOnlyList<string>, AdmissionDecision>? admitSends = null, SignalCliGate? signalCliGate = null,
        int maxInFlight = 0)
    {
        if (principalName is not null)
            principal = new SignalPrincipal(
                principalName, principal.IsAdmin, principal.HasFullAccess,
                principal.AllowedAccounts, principal.AllowedGroupIds);

        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new AuthorizingSignalJsonRpc(
            new HeaderDelimitedMessageHandler(serverStream, serverStream, CreateFormatter()),
            principal,
            new SignalRpcPolicyRegistry(),
            logger: null,
            isCredentialLive: isCredentialLive,
            admitSends: admitSends,
            signalCliGate: signalCliGate,
            maxInFlightPerConnection: maxInFlight);
        server.AddLocalRpcTarget(target, new JsonRpcTargetOptions
        {
            MethodNameTransform = CommonMethodNameTransforms.CamelCase,
        });
        server.StartListening();

        var client = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientStream, clientStream, CreateFormatter()));
        client.StartListening();

        _cleanup.Add(server);
        _cleanup.Add(client);
        return client;
    }

    private static SystemTextJsonFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        formatter.JsonSerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
            SignalCliSerializerContext.Default,
            new DefaultJsonTypeInfoResolver());
        return formatter;
    }

    // ---------- Fake-таргети (роль адаптерів/signal-cli) ----------

    private sealed class UnclassifiedTarget
    {
        public bool Executed { get; private set; }

        public string Danger()
        {
            Executed = true;
            return "boom";
        }
    }

    private sealed class MessageTarget
    {
        public bool Invoked { get; private set; }

        public string? LastAccount { get; private set; }

        public Task<string> SendTextMessage(string account, List<string> recipients, string message)
        {
            Invoked = true;
            LastAccount = account;
            return Task.FromResult("sent");
        }
    }

    // Send-таргет, що зависає всередині dispatch до Release — для тесту in-flight cap (тримає слоти).
    private sealed class BlockingMessageTarget
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Entered;   // Interlocked — скільки викликів увійшло в тіло (тримають in-flight слот)

        public void Release() => _release.TrySetResult();

        public async Task<string> SendTextMessage(string account, List<string> recipients, string message)
        {
            Interlocked.Increment(ref Entered);
            await _release.Task.ConfigureAwait(false);
            return "sent";
        }
    }

    private sealed class PingTarget
    {
        public Task<string> Ping() => Task.FromResult("pong");
    }

    private sealed class AccountsTarget(params string[] numbers)
    {
        public Task<ListAccountsResponse> ListAccounts() =>
            Task.FromResult(new ListAccountsResponse(numbers.Select(n => new Account(n)).ToList()));
    }

    private sealed class GroupsTarget(params string[] groupIds)
    {
        public Task<ListGroupsResponse> ListGroups(string account) =>
            Task.FromResult(new ListGroupsResponse(groupIds.Select(MakeGroup).ToList()));

        private static Group MakeGroup(string id) => new(
            Id: id, Name: $"група {id}", Description: null, IsMember: true, IsBlocked: false,
            MessageExpirationTime: 0, Members: [], PendingMembers: [], RequestingMembers: [],
            Admins: [], Banned: [], PermissionAddMember: "EVERY_MEMBER",
            PermissionEditDetails: "EVERY_MEMBER", PermissionSendMessage: "EVERY_MEMBER",
            GroupInviteLink: null);
    }

    private sealed class PrincipalEchoTarget
    {
        // Дзеркалить V8-схему: спільний stateless-таргет читає principal per-invocation з call-context.
        public Task<string> SyncAccount() =>
            Task.FromResult(SignalCallContext.Principal?.Name ?? "(немає principal)");
    }

    // Admin-таргет (роль SignalAdminRpcAdapter): методи createInvite/revokeIdentity під Admin-політикою.
    private sealed class AdminTarget
    {
        public bool Invoked { get; private set; }

        public Task<string> CreateInvite()
        {
            Invoked = true;
            return Task.FromResult("created");
        }

        public Task<string> RevokeIdentity(string identityId)
        {
            Invoked = true;
            return Task.FromResult(identityId);
        }
    }
}
