using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Persistence;

/// <summary>
/// Пінить v5-частину durable-стору (add-invites-admin, секція 2): admin-credential mapping (SPKI→identity,
/// idempotent upsert, revoke), audit-log append/last/entries (hash-chain-поля) і міграцію v4→v5.
/// </summary>
public class DurableStoreAdminTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-admin-store-tests-" + Guid.NewGuid().ToString("N"));

    public DurableStoreAdminTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore(string? fileName = null) =>
        new(Path.Combine(_dataDir, fileName ?? "durable.db"), NullLogger<DurableStore>.Instance);

    private static void SeedAdminIdentity(DurableStore store, string id = "admin") =>
        store.UpsertIdentity(new IdentityRecord(id, "admin", [], FixedTime));

    [Fact]
    public void AdminCredential_RoundTrips()
    {
        using var store = NewStore();
        store.Initialize();
        SeedAdminIdentity(store);

        store.UpsertAdminCredential(new AdminCredentialRecord("SPKI-ABC", "admin", Revoked: false, FixedTime));

        var loaded = store.GetAdminCredential("SPKI-ABC");
        Assert.NotNull(loaded);
        Assert.Equal("admin", loaded!.IdentityId);
        Assert.False(loaded.Revoked);
        Assert.Equal(FixedTime, loaded.CreatedAt);
    }

    [Fact]
    public void GetAdminCredential_WhenAbsent_ReturnsNull()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.Null(store.GetAdminCredential("missing"));
    }

    [Fact]
    public void UpsertAdminCredential_Idempotent_PreservesRevokedFlag()
    {
        using var store = NewStore();
        store.Initialize();
        SeedAdminIdentity(store);

        store.UpsertAdminCredential(new AdminCredentialRecord("SPKI-ABC", "admin", Revoked: false, FixedTime));
        store.RevokeAdminCredential("SPKI-ABC");

        // Повторний upsert того самого SPKI НЕ знімає revoked (INSERT OR IGNORE — no-op на конфлікті).
        store.UpsertAdminCredential(new AdminCredentialRecord("SPKI-ABC", "admin", Revoked: false, FixedTime.AddDays(1)));

        var loaded = store.GetAdminCredential("SPKI-ABC");
        Assert.True(loaded!.Revoked);
        Assert.Equal(FixedTime, loaded.CreatedAt); // created_at не перезаписано
    }

    [Fact]
    public void RevokeAdminCredential_UnknownSpki_IsNoOp()
    {
        using var store = NewStore();
        store.Initialize();

        store.RevokeAdminCredential("nope"); // не кидає
        Assert.Null(store.GetAdminCredential("nope"));
    }

    [Fact]
    public void AuditLog_AppendAndReadBack_InSeqOrder()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.Null(store.GetLastAuditEntry());

        store.AppendAuditEntry(new AuditLogRecord(1, "e1", "actor", "d1", new string('0', 64), "h1", FixedTime));
        store.AppendAuditEntry(new AuditLogRecord(2, "e2", "actor", "d2", "h1", "h2", FixedTime));

        var last = store.GetLastAuditEntry();
        Assert.Equal(2, last!.Seq);
        Assert.Equal("h2", last.EntryHash);

        var all = store.GetAuditEntries();
        Assert.Equal([1L, 2L], all.Select(e => e.Seq));
        Assert.Equal("h1", all[1].PrevHash);
    }

    [Fact]
    public void AppendAuditEntry_DuplicateSeq_Throws()
    {
        using var store = NewStore();
        store.Initialize();
        store.AppendAuditEntry(new AuditLogRecord(1, "e1", "actor", "d1", new string('0', 64), "h1", FixedTime));

        Assert.Throws<SqliteException>(
            () => store.AppendAuditEntry(new AuditLogRecord(1, "e1", "actor", "d1", new string('0', 64), "h1b", FixedTime)));
    }

    [Fact]
    public void AdminState_SurvivesRestart()
    {
        using (var first = NewStore())
        {
            first.Initialize();
            SeedAdminIdentity(first);
            first.UpsertAdminCredential(new AdminCredentialRecord("SPKI-ABC", "admin", Revoked: false, FixedTime));
            first.AppendAuditEntry(new AuditLogRecord(1, "e1", "admin", "d1", new string('0', 64), "h1", FixedTime));
        }

        using var second = NewStore();
        second.Initialize();
        Assert.NotNull(second.GetAdminCredential("SPKI-ABC"));
        Assert.Equal(1, second.GetLastAuditEntry()!.Seq);
    }

    [Fact]
    public void Migration_V4ToV5_CreatesAdminTables_PreservingData()
    {
        var dbPath = Path.Combine(_dataDir, "durable.db");

        // Будуємо БД у стані схеми v4 (v1..v4 DDL) з рядком даних і user_version=4.
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
                CREATE TABLE budget_reservations (
                    window_key TEXT NOT NULL, block_index INTEGER NOT NULL, block_size INTEGER NOT NULL,
                    reserved_at TEXT NOT NULL, PRIMARY KEY (window_key, block_index)
                ) STRICT;
                CREATE TABLE known_recipients (
                    identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                    recipient_hash TEXT NOT NULL, first_seen TEXT NOT NULL,
                    PRIMARY KEY (identity_id, recipient_hash)
                ) STRICT;
                CREATE TABLE invites (
                    code_hash TEXT PRIMARY KEY, created_by TEXT NOT NULL, consumed INTEGER NOT NULL DEFAULT 0,
                    attempt_count INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL,
                    expires_at TEXT NOT NULL, consumed_by TEXT, created_at TEXT NOT NULL
                ) STRICT;
                CREATE TABLE abuse_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, event_type TEXT NOT NULL,
                    subject_hmac TEXT NOT NULL, salt TEXT NOT NULL, logged_at TEXT NOT NULL
                ) STRICT;
                """);
            ExecRaw(seed,
                "INSERT INTO identities (id, role, created_at) VALUES ('admin', 'admin', '2026-07-17T12:00:00.0000000+00:00');");
            ExecRaw(seed, "PRAGMA user_version = 4;");
        }
        SqliteConnection.ClearAllPools();

        using var store = NewStore();
        store.Initialize();

        // Мігровано до v5, дані v4 збереглися.
        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);
        Assert.True(store.SchemaVersion >= 5);
        Assert.Equal("admin", store.GetIdentity("admin")!.Role);

        // Нові v5-таблиці існують (admin-credential + audit round-trip проходить).
        store.UpsertAdminCredential(new AdminCredentialRecord("SPKI-ABC", "admin", Revoked: false, FixedTime));
        Assert.NotNull(store.GetAdminCredential("SPKI-ABC"));
        store.AppendAuditEntry(new AuditLogRecord(1, "e1", "admin", "d1", new string('0', 64), "h1", FixedTime));
        Assert.Equal(1, store.GetLastAuditEntry()!.Seq);
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
