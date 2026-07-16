using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nerdbank.Streams;
using SignalCli.Models.Signal.Accounts;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Security;
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

    // ---------- Harness ----------

    private static SignalPrincipal UserPrincipal(string account, params string[] groups) =>
        new($"user-{account[^4..]}", isAdmin: false, hasFullAccess: false, [account], groups);

    /// <summary>
    /// Піднімає пару клієнт ↔ <see cref="AuthorizingSignalJsonRpc"/> над in-memory duplex-стрімом.
    /// Формуттер дзеркалить <c>SignalRpcSession.CreateJsonFormatter</c> (camelCase + source-gen
    /// контекст із reflection-фолбеком), таргет реєструється з camelCase-трансформом імен —
    /// як у продакшн-registry.
    /// </summary>
    private JsonRpc CreatePair(SignalPrincipal principal, object target, string? principalName = null)
    {
        if (principalName is not null)
            principal = new SignalPrincipal(
                principalName, principal.IsAdmin, principal.HasFullAccess,
                principal.AllowedAccounts, principal.AllowedGroupIds);

        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new AuthorizingSignalJsonRpc(
            new HeaderDelimitedMessageHandler(serverStream, serverStream, CreateFormatter()),
            principal,
            new SignalRpcPolicyRegistry());
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
}
