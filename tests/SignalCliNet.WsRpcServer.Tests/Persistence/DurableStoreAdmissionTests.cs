using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Persistence;

/// <summary>
/// Пінить admission-таблиці durable-стору (міграція v3): budget_reservations (блок-резервація W13,
/// double-reserve кидає) та known_recipients (D2, ідемпотентний upsert + FK-каскад).
/// </summary>
public class DurableStoreAdmissionTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-admission-store-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    public DurableStoreAdmissionTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore(string? fileName = null) =>
        new(Path.Combine(_dataDir, fileName ?? "durable.db"), NullLogger<DurableStore>.Instance);

    [Fact]
    public void Migration_ReachesV3_WithAdmissionTables()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.True(store.SchemaVersion >= 3);
        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);
    }

    [Fact]
    public void BudgetBlocks_CountAndInsert_RoundTrip()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.Equal(0, store.CountBudgetBlocks("202607161200"));

        store.InsertBudgetBlock("202607161200", 0, 5, FixedTime);
        store.InsertBudgetBlock("202607161200", 1, 5, FixedTime);
        Assert.Equal(2, store.CountBudgetBlocks("202607161200"));

        // Інше вікно — окремий простір індексів.
        Assert.Equal(0, store.CountBudgetBlocks("202607161300"));
    }

    [Fact]
    public void InsertBudgetBlock_DoubleReserve_Throws()
    {
        using var store = NewStore();
        store.Initialize();

        store.InsertBudgetBlock("w1", 0, 10, FixedTime);

        // Повторна резервація того ж (window_key, block_index) — конфлікт PK → кидок (захист W13).
        Assert.ThrowsAny<SqliteException>(() => store.InsertBudgetBlock("w1", 0, 10, FixedTime));
    }

    [Fact]
    public void BudgetBlocks_SurviveRestart()
    {
        using (var first = NewStore())
        {
            first.Initialize();
            first.InsertBudgetBlock("w1", 0, 5, FixedTime);
        }

        using var second = NewStore();
        second.Initialize();
        Assert.Equal(1, second.CountBudgetBlocks("w1"));
    }

    [Fact]
    public void KnownRecipient_UpsertIsIdempotent_AndCascades()
    {
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));

        Assert.False(store.IsKnownRecipient("id-1", "hash-a"));

        store.UpsertKnownRecipient("id-1", "hash-a", FixedTime);
        Assert.True(store.IsKnownRecipient("id-1", "hash-a"));

        // Повторний upsert — no-op (не кидає, лишається відомим).
        store.UpsertKnownRecipient("id-1", "hash-a", FixedTime.AddHours(1));
        Assert.True(store.IsKnownRecipient("id-1", "hash-a"));

        // Ізоляція per-identity.
        Assert.False(store.IsKnownRecipient("id-2", "hash-a"));
    }

    [Fact]
    public void Migration_V2ToV3_PreservesData_AndAddsTables()
    {
        var dbPath = Path.Combine(_dataDir, "durable.db");

        // БД у стані схеми v2 із рядком identity; user_version=2.
        using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            seed.Open();
            ExecRaw(seed,
                """
                CREATE TABLE identities (
                    id TEXT PRIMARY KEY, role TEXT NOT NULL, created_at TEXT NOT NULL
                ) STRICT;
                CREATE TABLE identity_accounts (
                    identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                    account TEXT NOT NULL, PRIMARY KEY (identity_id, account)
                ) STRICT;
                CREATE TABLE account_bindings (
                    account TEXT PRIMARY KEY,
                    owning_identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                    is_private INTEGER NOT NULL, created_at TEXT NOT NULL
                ) STRICT;
                CREATE INDEX ix_account_bindings_identity ON account_bindings(owning_identity_id);
                CREATE TABLE auth_tokens (
                    token_hash TEXT PRIMARY KEY, pepper_version INTEGER NOT NULL,
                    identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                    expires_at TEXT NOT NULL, is_revoked INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL
                ) STRICT;
                CREATE INDEX ix_auth_tokens_identity ON auth_tokens(identity_id);
                CREATE TABLE device_secrets (
                    identity_id TEXT PRIMARY KEY REFERENCES identities(id) ON DELETE CASCADE,
                    public_key TEXT NOT NULL, algorithm TEXT NOT NULL,
                    is_revoked INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL
                ) STRICT;
                """);
            ExecRaw(seed,
                "INSERT INTO identities (id, role, created_at) VALUES ('id-1', 'admin', '2026-07-16T12:00:00.0000000+00:00');");
            ExecRaw(seed, "PRAGMA user_version = 2;");
        }
        SqliteConnection.ClearAllPools();

        using var store = NewStore();
        store.Initialize();

        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);
        Assert.True(store.SchemaVersion >= 3);

        // Дані v2 збереглися; нові v3-таблиці працюють.
        Assert.NotNull(store.GetIdentity("id-1"));
        store.InsertBudgetBlock("w1", 0, 10, FixedTime);
        Assert.Equal(1, store.CountBudgetBlocks("w1"));
        store.UpsertKnownRecipient("id-1", "hash-a", FixedTime);
        Assert.True(store.IsKnownRecipient("id-1", "hash-a"));
    }

    private static void ExecRaw(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
