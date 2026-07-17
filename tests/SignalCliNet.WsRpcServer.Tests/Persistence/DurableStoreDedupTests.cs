using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Persistence;

/// <summary>
/// Пінить send-dedup таблицю durable-стору (міграція v7, task 3.3): атомарний claim (<c>TryBeginSend</c>),
/// перехід pending→done (<c>CompleteSend</c>), lookup (<c>GetSendDedup</c>), durability через рестарт,
/// per-tenant ізоляцію ключа (identity_id, message_id).
/// </summary>
public sealed class DurableStoreDedupTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-dedup-store-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    public DurableStoreDedupTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore(string? fileName = null)
    {
        var store = new DurableStore(Path.Combine(_dataDir, fileName ?? "durable.db"), NullLogger<DurableStore>.Instance);
        store.Initialize();
        return store;
    }

    [Fact]
    public void Migration_ReachesV7_WithSendDedupTable()
    {
        using var store = NewStore();
        Assert.True(store.SchemaVersion >= 7);
        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);

        // Свіжий ключ → null (таблиця існує, порожня).
        Assert.Null(store.GetSendDedup("id-1", "msg-1"));
    }

    [Fact]
    public void TryBeginSend_ClaimsOnce_DuplicateReturnsFalse()
    {
        using var store = NewStore();

        Assert.True(store.TryBeginSend("id-1", "msg-1", FixedTime));   // виграв claim
        Assert.False(store.TryBeginSend("id-1", "msg-1", FixedTime));  // дубль → false

        var row = store.GetSendDedup("id-1", "msg-1");
        Assert.NotNull(row);
        Assert.Equal(SendDedupRecord.StatusPending, row!.Status);
        Assert.True(row.BudgetReserved);
        Assert.Null(row.ResultJson);
    }

    [Fact]
    public void CompleteSend_TransitionsPendingToDone_StoresResult()
    {
        using var store = NewStore();
        store.TryBeginSend("id-1", "msg-1", FixedTime);

        store.CompleteSend("id-1", "msg-1", "{\"timestamp\":123}");

        var row = store.GetSendDedup("id-1", "msg-1");
        Assert.NotNull(row);
        Assert.Equal(SendDedupRecord.StatusDone, row!.Status);
        Assert.Equal("{\"timestamp\":123}", row.ResultJson);
    }

    [Fact]
    public void CompleteSend_OnlyWhilePending_DoesNotOverwriteDone()
    {
        using var store = NewStore();
        store.TryBeginSend("id-1", "msg-1", FixedTime);
        store.CompleteSend("id-1", "msg-1", "first");

        // Друга спроба complete — no-op (status уже done, умовний WHERE status='pending').
        store.CompleteSend("id-1", "msg-1", "second");

        Assert.Equal("first", store.GetSendDedup("id-1", "msg-1")!.ResultJson);
    }

    [Fact]
    public void Dedup_SurvivesRestart()
    {
        using (var first = NewStore())
        {
            first.TryBeginSend("id-1", "msg-1", FixedTime);
            first.CompleteSend("id-1", "msg-1", "done-result");
        }

        SqliteConnection.ClearAllPools();

        using var second = NewStore();
        var row = second.GetSendDedup("id-1", "msg-1");
        Assert.NotNull(row);
        Assert.Equal(SendDedupRecord.StatusDone, row!.Status);
        Assert.Equal("done-result", row.ResultJson);
    }

    [Fact]
    public void Key_IsPerTenant_NoCrossIdentityCollision()
    {
        using var store = NewStore();

        Assert.True(store.TryBeginSend("id-1", "msg-shared", FixedTime));
        // Той самий messageId, інша identity → окремий ключ, окремий claim.
        Assert.True(store.TryBeginSend("id-2", "msg-shared", FixedTime));

        Assert.NotNull(store.GetSendDedup("id-1", "msg-shared"));
        Assert.NotNull(store.GetSendDedup("id-2", "msg-shared"));
    }

    [Fact]
    public void Migration_V6ToV7_PreservesSchema_AndAddsDedupTable()
    {
        var dbPath = Path.Combine(_dataDir, "v6.db");

        // Ініціалізуємо повну схему, потім відкатуємо user_version до 6 (симуляція старої БД без v7).
        using (var seed = NewStore("v6.db")) { }
        SqliteConnection.ClearAllPools();
        using (var raw = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            raw.Open();
            using var drop = raw.CreateCommand();
            drop.CommandText = "DROP TABLE send_dedup; PRAGMA user_version = 6;";
            drop.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = NewStore("v6.db");
        Assert.Equal(SchemaMigrator.LatestVersion, migrated.SchemaVersion);
        Assert.True(migrated.TryBeginSend("id-1", "msg-1", FixedTime));
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
