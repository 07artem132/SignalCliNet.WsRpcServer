using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Security.Idempotency;
using SignalCliNet.WsRpcServer.Tests.Security;
using SignalCliNet.WsRpcServer.Tests.Security.Admission;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Idempotency;

/// <summary>
/// Пінить <see cref="IdempotentSendCoordinator"/> (task 3.3, T6/C2): dedup-lookup ПЕРЕД admission;
/// miss → admission + атомарний pending-claim; hit done → bypass admission; hit pending → outcome-unknown;
/// краш-симуляція → ретрай отримує outcome-unknown, send НЕ повторюється, бюджет НЕ декрементується вдруге.
/// </summary>
public sealed class IdempotentSendCoordinatorTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-idem-coord-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeAbusePepperProvider _pepper = new();

    public IdempotentSendCoordinatorTests() => Directory.CreateDirectory(_dataDir);

    private sealed record Harness(
        IdempotentSendCoordinator Coordinator, DurableStore Store, ReserveThenSendBudget Budget);

    private Harness NewHarness(AdmissionOptions? options = null)
    {
        var store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));

        options ??= new AdmissionOptions
        {
            Enabled = true, AggregateBudgetPerWindow = 100, ReserveBlockSize = 1,
            PerUserFloorPerWindow = 100, NewRecipientsPerWindow = 100,
        };
        var opts = Options.Create(options);
        var time = new TestTimeProvider(FixedTime);
        var budget = new ReserveThenSendBudget(store, opts, time, NullLogger<ReserveThenSendBudget>.Instance);
        var admission = new AdmissionControl(
            new NoopGlobalPauseGate(),
            new PerUserQuota(opts, time),
            new NewRecipientTracker(store, _pepper, opts, time),
            budget,
            opts,
            NullLogger<AdmissionControl>.Instance);
        var coordinator = new IdempotentSendCoordinator(admission, store, time);
        return new Harness(coordinator, store, budget);
    }

    [Fact]
    public void NoMessageId_DelegatesToAdmission_Fresh()
    {
        var h = NewHarness();
        var outcome = h.Coordinator.Begin("id-1", messageId: null, ["+15551230001"]);
        Assert.Equal(IdempotentSendKind.AdmittedFresh, outcome.Kind);
    }

    [Fact]
    public void Miss_AdmitsPending_ReservesBudget_ClaimsRow()
    {
        var h = NewHarness();
        var window = h.Budget.CurrentWindowKey;

        var outcome = h.Coordinator.Begin("id-1", "msg-1", ["+15551230001"]);

        Assert.Equal(IdempotentSendKind.AdmittedPending, outcome.Kind);
        Assert.Equal(SendDedupRecord.StatusPending, h.Store.GetSendDedup("id-1", "msg-1")!.Status);
        Assert.Equal(1, h.Store.CountBudgetBlocks(window));   // рівно один блок зарезервовано
    }

    [Fact]
    public void HitDone_BypassesAdmission_ReturnsSavedResult()
    {
        var h = NewHarness();
        var window = h.Budget.CurrentWindowKey;

        // Перший виклик → pending → done з результатом.
        Assert.Equal(IdempotentSendKind.AdmittedPending, h.Coordinator.Begin("id-1", "msg-1", ["+r1"]).Kind);
        h.Coordinator.Complete("id-1", "msg-1", "{\"saved\":true}");
        var blocksAfterFirst = h.Store.CountBudgetBlocks(window);

        // Ретрай того самого key → hit done, БЕЗ admission і БЕЗ додаткового декременту бюджету.
        var retry = h.Coordinator.Begin("id-1", "msg-1", ["+r1"]);
        Assert.Equal(IdempotentSendKind.DedupDone, retry.Kind);
        Assert.Equal("{\"saved\":true}", retry.SavedResultJson);
        Assert.Equal(blocksAfterFirst, h.Store.CountBudgetBlocks(window));   // бюджет НЕ декрементовано вдруге
    }

    [Fact]
    public void HitPending_ReturnsOutcomeUnknown()
    {
        var h = NewHarness();

        Assert.Equal(IdempotentSendKind.AdmittedPending, h.Coordinator.Begin("id-1", "msg-1", ["+r1"]).Kind);

        // Ще не завершено (pending) → повтор → outcome unknown (at-most-once, БЕЗ авто-повтору).
        var retry = h.Coordinator.Begin("id-1", "msg-1", ["+r1"]);
        Assert.Equal(IdempotentSendKind.OutcomeUnknown, retry.Kind);
    }

    [Fact]
    public void CrashBetweenPendingAndSend_RetryGetsOutcomeUnknown_BudgetNotDecrementedTwice()
    {
        var h = NewHarness();
        var window = h.Budget.CurrentWindowKey;

        // Перший виклик: admission + pending-claim (бюджет зарезервовано). «Краш» = НЕ викликаємо Complete.
        Assert.Equal(IdempotentSendKind.AdmittedPending, h.Coordinator.Begin("id-1", "msg-1", ["+r1"]).Kind);
        var blocksAfterCrash = h.Store.CountBudgetBlocks(window);
        Assert.Equal(1, blocksAfterCrash);

        // Симуляція рестарту: новий стор поверх тієї ж БД (pending-рядок durable).
        var (coordinator2, store2, budget2) = RestartOverSameDb();
        try
        {
            var retry = coordinator2.Begin("id-1", "msg-1", ["+r1"]);

            Assert.Equal(IdempotentSendKind.OutcomeUnknown, retry.Kind);          // send НЕ повторюється
            Assert.Equal(SendDedupRecord.StatusPending, store2.GetSendDedup("id-1", "msg-1")!.Status);
            Assert.Equal(1, store2.CountBudgetBlocks(window));                     // бюджет НЕ декрементовано вдруге
        }
        finally
        {
            store2.Dispose();
        }
    }

    // Відкриває НОВИЙ координатор/стор поверх тієї ж durable БД (симуляція рестарту процесу).
    private (IdempotentSendCoordinator, DurableStore, ReserveThenSendBudget) RestartOverSameDb()
    {
        SqliteConnection.ClearAllPools();
        var store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        store.Initialize();
        var opts = Options.Create(new AdmissionOptions
        {
            Enabled = true, AggregateBudgetPerWindow = 100, ReserveBlockSize = 1,
            PerUserFloorPerWindow = 100, NewRecipientsPerWindow = 100,
        });
        var time = new TestTimeProvider(FixedTime);
        var budget = new ReserveThenSendBudget(store, opts, time, NullLogger<ReserveThenSendBudget>.Instance);
        var admission = new AdmissionControl(
            new NoopGlobalPauseGate(),
            new PerUserQuota(opts, time),
            new NewRecipientTracker(store, _pepper, opts, time),
            budget,
            opts,
            NullLogger<AdmissionControl>.Instance);
        return (new IdempotentSendCoordinator(admission, store, time), store, budget);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
}
