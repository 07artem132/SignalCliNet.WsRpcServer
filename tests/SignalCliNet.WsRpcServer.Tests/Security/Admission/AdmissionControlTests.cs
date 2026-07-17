using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Пінить фасад <see cref="AdmissionControl"/> (W16): опт-ін (Enabled=false → усе admitted без side-effect),
/// строгий порядок short-circuit (global-pause деніїть ДО декрементів), кожну причину відмови окремо і
/// in-band retry_after > 0 на deny.
/// </summary>
public class AdmissionControlTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-admission-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeAbusePepperProvider _pepper = new();

    public AdmissionControlTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore()
    {
        var store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));
        return store;
    }

    private AdmissionControl NewControl(
        IDurableStore store,
        AdmissionOptions options,
        IGlobalPauseGate? gate = null,
        TimeProvider? time = null)
    {
        time ??= new TestTimeProvider(FixedTime);
        var opts = Options.Create(options);
        var quota = new PerUserQuota(opts, time);
        var recipients = new NewRecipientTracker(store, _pepper, opts, time);
        var budget = new ReserveThenSendBudget(store, opts, time, NullLogger<ReserveThenSendBudget>.Instance);
        return new AdmissionControl(
            gate ?? new NoopGlobalPauseGate(),
            quota,
            recipients,
            budget,
            opts,
            NullLogger<AdmissionControl>.Instance);
    }

    [Fact]
    public void Disabled_AdmitsEverything_WithNoSideEffects()
    {
        using var store = NewStore();
        var control = NewControl(store, new AdmissionOptions { Enabled = false });

        var decision = control.Admit("id-1", ["+15551230001"]);

        Assert.True(decision.Admitted);
        Assert.Equal(AdmissionDenyReason.None, decision.Reason);
        Assert.Equal(0, decision.RetryAfterSeconds);

        // Жодних побічних ефектів у сторі.
        Assert.Equal(0, store.CountBudgetBlocks("202607161200"));
        Assert.False(store.IsKnownRecipient("id-1", _pepper.Hash("+15551230001")));
    }

    [Fact]
    public void GlobalPause_DeniesFirst_WithNoSideEffects()
    {
        using var store = NewStore();
        var control = NewControl(
            store,
            new AdmissionOptions { Enabled = true, AggregateBudgetPerWindow = 100, PerUserFloorPerWindow = 100, NewRecipientsPerWindow = 100 },
            gate: new PausedGate());

        var decision = control.Admit("id-1", ["+15551230001"]);

        Assert.False(decision.Admitted);
        Assert.Equal(AdmissionDenyReason.GlobalPause, decision.Reason);
        Assert.True(decision.RetryAfterSeconds > 0);

        // W16: deniться ДО будь-яких декрементів/записів — стор незайманий.
        Assert.Equal(0, store.CountBudgetBlocks("202607161200"));
        Assert.False(store.IsKnownRecipient("id-1", _pepper.Hash("+15551230001")));
    }

    [Fact]
    public void HappyPath_Admits()
    {
        using var store = NewStore();
        var control = NewControl(
            store,
            new AdmissionOptions { Enabled = true, AggregateBudgetPerWindow = 100, ReserveBlockSize = 10, PerUserFloorPerWindow = 100, NewRecipientsPerWindow = 100 });

        var decision = control.Admit("id-1", ["+15551230001"]);

        Assert.True(decision.Admitted);
        Assert.Equal(AdmissionDenyReason.None, decision.Reason);
    }

    [Fact]
    public void UserQuotaExceeded_DeniesWithReason_AndRetryAfter()
    {
        using var store = NewStore();
        // floor=1, aggregate=1 → друга спроба тієї ж identity понад floor і без fair-share.
        var control = NewControl(
            store,
            new AdmissionOptions { Enabled = true, AggregateBudgetPerWindow = 1, PerUserFloorPerWindow = 1, NewRecipientsPerWindow = 100, ReserveBlockSize = 1 });

        Assert.True(control.Admit("id-1", ["+r1"]).Admitted);

        var decision = control.Admit("id-1", ["+r2"]);
        Assert.False(decision.Admitted);
        Assert.Equal(AdmissionDenyReason.UserQuota, decision.Reason);
        Assert.True(decision.RetryAfterSeconds > 0);
    }

    [Fact]
    public void NewRecipientWindowExceeded_DeniesWithReason_AndRetryAfter()
    {
        using var store = NewStore();
        // Квота/бюджет великі, ліміт лише на нові контакти = 1.
        var control = NewControl(
            store,
            new AdmissionOptions { Enabled = true, AggregateBudgetPerWindow = 100, PerUserFloorPerWindow = 100, NewRecipientsPerWindow = 1, ReserveBlockSize = 10 });

        Assert.True(control.Admit("id-1", ["+r1"]).Admitted);

        var decision = control.Admit("id-1", ["+r2"]);
        Assert.False(decision.Admitted);
        Assert.Equal(AdmissionDenyReason.NewRecipientWindow, decision.Reason);
        Assert.True(decision.RetryAfterSeconds > 0);
    }

    [Fact]
    public void AggregateBudgetExhausted_DeniesWithReason_AndRetryAfter()
    {
        using var store = NewStore();
        // Квота/нові контакти великі; бюджет = 1 юніт → друга відправка деніїться на бюджеті.
        var control = NewControl(
            store,
            new AdmissionOptions { Enabled = true, AggregateBudgetPerWindow = 1, ReserveBlockSize = 1, PerUserFloorPerWindow = 100, NewRecipientsPerWindow = 100 });

        Assert.True(control.Admit("id-1", ["+r1"]).Admitted);

        var decision = control.Admit("id-1", ["+r1"]);   // той самий (відомий) отримувач → лишається бюджет
        Assert.False(decision.Admitted);
        Assert.Equal(AdmissionDenyReason.AggregateBudget, decision.Reason);
        Assert.True(decision.RetryAfterSeconds > 0);
    }

    private sealed class PausedGate : IGlobalPauseGate
    {
        public bool IsPaused => true;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
