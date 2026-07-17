using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Пінить <see cref="NewRecipientTracker"/> (D2): вікно нових контактів (5 ок, 6-й deny), відомий
/// отримувач не рахується, батч-облік не персистить при перевищенні, монотонний скид вікна і
/// privacy-контракт (у сторі — хеш, не сирий номер).
/// </summary>
public class NewRecipientTrackerTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-recip-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeAbusePepperProvider _pepper = new();

    public NewRecipientTrackerTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore()
    {
        var store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));
        return store;
    }

    private NewRecipientTracker NewTracker(IDurableStore store, TimeProvider time, int max, int windowMinutes = 60) =>
        new(
            store,
            _pepper,
            Options.Create(new AdmissionOptions { NewRecipientsPerWindow = max, BudgetWindowMinutes = windowMinutes }),
            time);

    [Fact]
    public void FiveNewRecipients_Ok_SixthInWindow_Denied()
    {
        using var store = NewStore();
        var tracker = NewTracker(store, new TestTimeProvider(FixedTime), max: 5);

        for (var i = 1; i <= 5; i++)
            Assert.True(tracker.TryAdmitRecipients("id-1", [$"+1555000000{i}"]));

        Assert.False(tracker.TryAdmitRecipients("id-1", ["+15550000006"]));
    }

    [Fact]
    public void KnownRecipient_DoesNotCount()
    {
        using var store = NewStore();
        var tracker = NewTracker(store, new TestTimeProvider(FixedTime), max: 2);

        Assert.True(tracker.TryAdmitRecipients("id-1", ["+r1"]));   // новий, count=1
        Assert.True(tracker.TryAdmitRecipients("id-1", ["+r1"]));   // відомий → не рахується
        Assert.True(tracker.TryAdmitRecipients("id-1", ["+r2"]));   // новий, count=2
        Assert.False(tracker.TryAdmitRecipients("id-1", ["+r3"]));  // count+1=3 > 2 → deny
    }

    [Fact]
    public void StoredValue_IsHash_NotRawNumber()
    {
        using var store = NewStore();
        var tracker = NewTracker(store, new TestTimeProvider(FixedTime), max: 5);

        const string raw = "+15551230001";
        Assert.True(tracker.TryAdmitRecipients("id-1", [raw]));

        // У сторі — HMAC-хеш, а не сирий номер (privacy-контракт).
        Assert.True(store.IsKnownRecipient("id-1", _pepper.Hash(raw)));
        Assert.False(store.IsKnownRecipient("id-1", raw));
    }

    [Fact]
    public void BatchExceedingWindow_PersistsNothing()
    {
        using var store = NewStore();
        var tracker = NewTracker(store, new TestTimeProvider(FixedTime), max: 5);

        Assert.True(tracker.TryAdmitRecipients("id-1", ["+a", "+b", "+c"]));      // count=3
        Assert.False(tracker.TryAdmitRecipients("id-1", ["+d", "+e", "+f"]));     // 3+3=6 > 5 → deny

        // Нічого з відхиленого батча не персистовано.
        Assert.False(store.IsKnownRecipient("id-1", _pepper.Hash("+d")));
        Assert.False(store.IsKnownRecipient("id-1", _pepper.Hash("+e")));
        Assert.False(store.IsKnownRecipient("id-1", _pepper.Hash("+f")));
    }

    [Fact]
    public void WindowReset_AllowsNewRecipientsAgain()
    {
        using var store = NewStore();
        var time = new TestTimeProvider(FixedTime);
        var tracker = NewTracker(store, time, max: 1, windowMinutes: 60);

        Assert.True(tracker.TryAdmitRecipients("id-1", ["+a"]));    // count=1
        Assert.False(tracker.TryAdmitRecipients("id-1", ["+b"]));   // 1+1 > 1 → deny

        time.Advance(TimeSpan.FromMinutes(61));                     // монотонний скид вікна (D15)
        Assert.True(tracker.TryAdmitRecipients("id-1", ["+c"]));    // нове вікно → новий контакт ок
    }

    [Fact]
    public void EmptyRecipients_Admitted()
    {
        using var store = NewStore();
        var tracker = NewTracker(store, new TestTimeProvider(FixedTime), max: 1);

        Assert.True(tracker.TryAdmitRecipients("id-1", []));
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
