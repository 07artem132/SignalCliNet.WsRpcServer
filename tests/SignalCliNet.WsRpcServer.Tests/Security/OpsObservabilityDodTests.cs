using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCli.Exceptions;
using SignalCli.Models.Rpc;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Security.Fail;
using SignalCliNet.WsRpcServer.Security.Idempotency;
using StreamJsonRpc;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// DoD секції 5 (add-ops-observability) через РЕАЛЬНІ компоненти: GlobalPauseGate + AdmissionControl +
/// UpstreamSendFailureMapper + IdempotentSendCoordinator + DurableStore. 5.1 W10 restart-cancel типізовано
/// (окремо від client-cancel); 5.2 captcha/-5-6 → бот паузиться, наступні send'и Deny(GlobalPause);
/// 5.3 дубль-ретрай тим самим messageId → 1 send, 1 декремент бюджету (вкл. через «рестарт»);
/// 5.5 T6 краш між pending і send → ретрай = outcome-unknown, без повторного send/декременту; hit done →
/// без admission. (5.1 recovery signal-cli — upstream SignalCliHealthMonitor; тут — app-half W10.)
/// </summary>
public sealed class OpsObservabilityDodTests : IDisposable
{
    private const string Identity = "id-ops";
    private static readonly IReadOnlyList<string> Recipients = ["+15551230002"];

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-ops-dod-" + Guid.NewGuid().ToString("N"));

    public OpsObservabilityDodTests()
    {
        Directory.CreateDirectory(Path.Combine(_dataDir, "secrets"));
        File.WriteAllText(Path.Combine(_dataDir, "secrets", "pepper_abuse"),
            Convert.ToBase64String(new byte[32]));
    }

    // ---------------------------------------------------------------- 5.1 (W10 restart-cancel)

    [Fact]
    public void RestartCancel_TypedDistinctFromClientCancel()
    {
        var gate = NewPauseGate();
        var mapper = new UpstreamSendFailureMapper(
            gate, Options.Create(new GlobalPauseOptions()), NullLogger<UpstreamSendFailureMapper>.Instance);

        // Демон рестартнув посеред in-flight (clientToken НЕ скасований) → типізований ServiceRestarting,
        // а не тиха втрата нотіфа.
        var restart = mapper.Map(new OperationCanceledException(), CancellationToken.None, "sendTextMessage");
        var restartError = Assert.IsType<RpcErrorException>(restart);
        Assert.Equal(FailPathErrorCodes.ServiceRestarting, (int)restartError.ErrorCode);

        // Client-cancel (сам клієнт скасував токен) → НЕ ServiceRestarting (окрема кореляція).
        using var clientCts = new CancellationTokenSource();
        clientCts.Cancel();
        var clientCancel = mapper.Map(new OperationCanceledException(clientCts.Token), clientCts.Token, "sendTextMessage");
        Assert.True(clientCancel is not RpcErrorException r || (int)r.ErrorCode != FailPathErrorCodes.ServiceRestarting);
    }

    // ---------------------------------------------------------------- 5.2 (captcha/-5-6 → pause)

    [Fact]
    public void CaptchaAndRateLimit_PauseBot_SubsequentSendsDenied()
    {
        var gate = NewPauseGate();
        var admission = NewAdmission(gate, new TestTimeProvider(FixedTime), aggregateBudget: 100);
        var mapper = new UpstreamSendFailureMapper(
            gate, Options.Create(new GlobalPauseOptions { BasePauseSeconds = 60 }),
            NullLogger<UpstreamSendFailureMapper>.Instance);

        Assert.True(admission.Admit(Identity, Recipients).Admitted);   // до паузи — проходить
        Assert.False(gate.IsPaused);

        // Signal-сервер вимагає CAPTCHA (-6) → мапер паузить бота глобально.
        mapper.Map(new CaptchaRequiredException(new JsonRpcError { Code = -6, Message = "captcha required" }), CancellationToken.None, "sendTextMessage");
        Assert.True(gate.IsPaused);

        // Наступні send'и деняться GlobalPause — бот не «долбає» сервер (без per-user/budget side-effect).
        var denied = admission.Admit(Identity, Recipients);
        Assert.False(denied.Admitted);
        Assert.Equal(AdmissionDenyReason.GlobalPause, denied.Reason);

        // RateLimit (-5) — той самий шлях паузи.
        var gate2 = NewPauseGate();
        new UpstreamSendFailureMapper(gate2, Options.Create(new GlobalPauseOptions()),
            NullLogger<UpstreamSendFailureMapper>.Instance)
            .Map(new RateLimitException(new JsonRpcError { Code = -5, Message = "rate limited" }), CancellationToken.None, "sendTextMessage");
        Assert.True(gate2.IsPaused);
    }

    // ---------------------------------------------------------------- 5.3 (dedup: 1 send, 1 декремент)

    [Fact]
    public void DuplicateRetry_SameKey_OneSend_OneBudgetDecrement_AcrossRestart()
    {
        const string key = "msg-abc";
        using var store = NewStore();
        var gate = NewPauseGate();
        var admission = NewAdmission(gate, new TestTimeProvider(FixedTime), aggregateBudget: 100, store);

        var coordinator = new IdempotentSendCoordinator(admission, store, new TestTimeProvider(FixedTime));

        // Перший keyed-send: admission резервує бюджет + атомарний pending-claim.
        var first = coordinator.Begin(Identity, key, Recipients);
        Assert.Equal(IdempotentSendKind.AdmittedPending, first.Kind);
        coordinator.Complete(Identity, key, "{\"timestamp\":1}");   // успішний dispatch → done

        var blocksAfterFirst = store.CountBudgetBlocks(BudgetWindowKey(store));

        // Дубль-ретрай тим самим ключем → hit done → БЕЗ admission, БЕЗ другого декременту.
        var retry = coordinator.Begin(Identity, key, Recipients);
        Assert.Equal(IdempotentSendKind.DedupDone, retry.Kind);
        Assert.Equal("{\"timestamp\":1}", retry.SavedResultJson);
        Assert.Equal(blocksAfterFirst, store.CountBudgetBlocks(BudgetWindowKey(store)));   // бюджет не рухнувся

        // «Рестарт»: новий стор/координатор на тому ж durable.db → dedup durable, ретрай = done.
        store.Dispose();
        using var store2 = NewStore();
        var admission2 = NewAdmission(NewPauseGate(), new TestTimeProvider(FixedTime), 100, store2);
        var coord2 = new IdempotentSendCoordinator(admission2, store2, new TestTimeProvider(FixedTime));
        var afterRestart = coord2.Begin(Identity, key, Recipients);
        Assert.Equal(IdempotentSendKind.DedupDone, afterRestart.Kind);
    }

    // ---------------------------------------------------------------- 5.5 (T6 crash between pending and send)

    [Fact]
    public void Crash_BetweenPendingAndSend_Retry_OutcomeUnknown_NoResend_NoDoubleDecrement()
    {
        const string key = "msg-crash";
        var store = NewStore();
        var admission = NewAdmission(NewPauseGate(), new TestTimeProvider(FixedTime), 100, store);
        var coordinator = new IdempotentSendCoordinator(admission, store, new TestTimeProvider(FixedTime));

        // Begin резервує бюджет + pending-claim... і КРАШ (Complete НЕ викликано) — pending лишається durably.
        Assert.Equal(IdempotentSendKind.AdmittedPending, coordinator.Begin(Identity, key, Recipients).Kind);
        var blocksAtCrash = store.CountBudgetBlocks(BudgetWindowKey(store));
        store.Dispose();   // симуляція крашу процесу

        // Рестарт: ретрай того самого ключа бачить pending → outcome-unknown (at-most-once):
        // send НЕ повторюється, бюджет НЕ декрементується вдруге.
        using var store2 = NewStore();
        var admission2 = NewAdmission(NewPauseGate(), new TestTimeProvider(FixedTime), 100, store2);
        var coord2 = new IdempotentSendCoordinator(admission2, store2, new TestTimeProvider(FixedTime));

        var retry = coord2.Begin(Identity, key, Recipients);
        Assert.Equal(IdempotentSendKind.OutcomeUnknown, retry.Kind);
        Assert.Equal(blocksAtCrash, store2.CountBudgetBlocks(BudgetWindowKey(store2)));   // без другого декременту
    }

    // ---------------------------------------------------------------- 5.4 (heartbeat)

    [Fact]
    public async Task PersistentWs_ReceivesHeartbeat_WithinInterval()
    {
        var notifier = new CapturingNotifier();
        // Реальний годинник + мінімальний інтервал (1с, <25с C5): PeriodicTimer керується TimeProvider.System.
        var hosted = new SignalCliNet.WsRpcServer.Services.HeartbeatHostedService(
            notifier, NewPauseGate(), TimeProvider.System,
            Options.Create(new HeartbeatOptions { Enabled = true, IntervalSeconds = 1 }),
            NullLogger<SignalCliNet.WsRpcServer.Services.HeartbeatHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            // Персистентний WS без трафіку отримує heartbeat у межах інтервалу (<25с).
            await WaitUntilAsync(() => notifier.Methods.Contains("heartbeat"), TimeSpan.FromSeconds(10));
            Assert.Contains("heartbeat", notifier.Methods);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------- 4.1 (perf: budget-write амортизація)

    [Fact]
    public void BudgetPersistWrites_AreAmortizedByBlockReservation()
    {
        // N3/G11: durable budget-writes НЕ ростуть 1:1 із send'ами — блок-резервація гранулою K
        // амортизує їх (N юнітів → ceil(N/K) INSERT-ів у budget_reservations). Тримає writes/sec низьким
        // під навантаженням.
        using var store = NewStore();
        const int blockSize = 10;
        var budget = new ReserveThenSendBudget(
            store,
            Options.Create(new AdmissionOptions
            {
                Enabled = true, AggregateBudgetPerWindow = 1000, ReserveBlockSize = blockSize,
            }),
            new TestTimeProvider(FixedTime),
            NullLogger<ReserveThenSendBudget>.Instance);

        for (var i = 0; i < 100; i++)
            Assert.True(budget.TryReserveUnit());

        // 100 юнітів під блоком K=10 → рівно 10 durable-блоків (а не 100 записів).
        Assert.Equal(10, store.CountBudgetBlocks(budget.CurrentWindowKey));
    }

    // ================================================================ harness

    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private DurableStore NewStore()
    {
        var store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        store.Initialize();
        // FK: dedup/known_recipients посилаються на identities(id) — сідаємо identity (ідемпотентно).
        store.UpsertIdentity(new IdentityRecord(Identity, "user", [], FixedTime));
        return store;
    }

    private static GlobalPauseGate NewPauseGate() => new(
        Options.Create(new GlobalPauseOptions()), TimeProvider.System,
        NullLogger<GlobalPauseGate>.Instance);

    private AdmissionControl NewAdmission(
        GlobalPauseGate gate, TimeProvider time, int aggregateBudget, DurableStore? store = null)
    {
        store ??= NewStore();
        var options = Options.Create(new AdmissionOptions
        {
            Enabled = true,
            AggregateBudgetPerWindow = aggregateBudget,
            ReserveBlockSize = 1,
            PerUserFloorPerWindow = 10,
        });
        var pepper = new AbusePepperProvider(Options.Create(new PersistenceOptions { DataDirectory = _dataDir }));
        return new AdmissionControl(
            gate,
            new PerUserQuota(options, time),
            new NewRecipientTracker(store, pepper, options, time),
            new ReserveThenSendBudget(store, options, time, NullLogger<ReserveThenSendBudget>.Instance),
            options,
            NullLogger<AdmissionControl>.Instance);
    }

    private static string BudgetWindowKey(DurableStore store) =>
        new ReserveThenSendBudget(
            store,
            Options.Create(new AdmissionOptions { AggregateBudgetPerWindow = 100, ReserveBlockSize = 1 }),
            new TestTimeProvider(FixedTime),
            NullLogger<ReserveThenSendBudget>.Instance).CurrentWindowKey;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання темп-каталогу
        }
    }

    private sealed class CapturingNotifier : IAppNotifier
    {
        public ConcurrentBag<string> Methods { get; } = [];

        public void Broadcast(string method, object payload) => Methods.Add(method);
    }
}
