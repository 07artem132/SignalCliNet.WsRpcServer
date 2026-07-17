using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Persistence;

/// <summary>
/// Пінить invite/abuse-частину durable-стору (add-invites-admin): round-trip інвайта, атомарний
/// consume+attempt-cap (W20/G12), abuse-лог append і міграцію v3→v4.
/// </summary>
public class DurableStoreInvitesTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-invite-store-tests-" + Guid.NewGuid().ToString("N"));

    public DurableStoreInvitesTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore(string? fileName = null) =>
        new(Path.Combine(_dataDir, fileName ?? "durable.db"), NullLogger<DurableStore>.Instance);

    private static InviteRecord Invite(
        string hash, int maxAttempts = 3, int attemptCount = 0, bool consumed = false,
        DateTimeOffset? expiresAt = null) =>
        new(hash, "admin-1", consumed, attemptCount, maxAttempts,
            expiresAt ?? FixedTime.AddHours(1), consumed ? "someone" : null, FixedTime);

    [Fact]
    public void Invite_RoundTrips()
    {
        using var store = NewStore();
        store.Initialize();

        store.InsertInvite(Invite("hash-1", maxAttempts: 5));

        var loaded = store.GetInvite("hash-1");
        Assert.NotNull(loaded);
        Assert.Equal("admin-1", loaded!.CreatedBy);
        Assert.False(loaded.Consumed);
        Assert.Equal(0, loaded.AttemptCount);
        Assert.Equal(5, loaded.MaxAttempts);
        Assert.Equal(FixedTime.AddHours(1), loaded.ExpiresAt);
        Assert.Null(loaded.ConsumedBy);
        Assert.Equal(FixedTime, loaded.CreatedAt);
    }

    [Fact]
    public void GetInvite_WhenAbsent_ReturnsNull()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.Null(store.GetInvite("missing"));
    }

    [Fact]
    public void InsertInvite_DuplicateHash_Throws()
    {
        using var store = NewStore();
        store.Initialize();
        store.InsertInvite(Invite("dup"));

        Assert.Throws<SqliteException>(() => store.InsertInvite(Invite("dup")));
    }

    [Fact]
    public void TryConsumeInvite_HappyPath_ConsumesOnce()
    {
        using var store = NewStore();
        store.Initialize();
        store.InsertInvite(Invite("hash-1"));

        Assert.True(store.TryConsumeInvite("hash-1", "id-a", FixedTime));

        var after = store.GetInvite("hash-1");
        Assert.True(after!.Consumed);
        Assert.Equal("id-a", after.ConsumedBy);
        Assert.Equal(1, after.AttemptCount);

        // Повторний consume того ж інвайта → false (one-time).
        Assert.False(store.TryConsumeInvite("hash-1", "id-b", FixedTime));
        Assert.Equal("id-a", store.GetInvite("hash-1")!.ConsumedBy); // власник не переписався
    }

    [Fact]
    public void TryConsumeInvite_UnknownHash_ReturnsFalse()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.False(store.TryConsumeInvite("nope", "id-a", FixedTime));
    }

    [Fact]
    public void TryConsumeInvite_Expired_ReturnsFalse_ButCountsAttempt()
    {
        using var store = NewStore();
        store.Initialize();
        store.InsertInvite(Invite("hash-1", expiresAt: FixedTime));

        // now == expiresAt → прострочено (expires <= now).
        Assert.False(store.TryConsumeInvite("hash-1", "id-a", FixedTime));

        // G12: невдала спроба все одно інкрементнула attempt_count.
        Assert.Equal(1, store.GetInvite("hash-1")!.AttemptCount);
        Assert.False(store.GetInvite("hash-1")!.Consumed);
    }

    [Fact]
    public void TryConsumeInvite_OverCap_ReturnsFalse_WithoutConsuming()
    {
        using var store = NewStore();
        store.Initialize();
        // attempt_count вже == max; наступна спроба інкрементнеться до max+1 → понад cap, не consume.
        store.InsertInvite(Invite("hash-1", maxAttempts: 3, attemptCount: 3));

        Assert.False(store.TryConsumeInvite("hash-1", "id-a", FixedTime));

        var after = store.GetInvite("hash-1");
        Assert.False(after!.Consumed);
        Assert.Equal(4, after.AttemptCount);
    }

    [Fact]
    public void TryConsumeInvite_ParallelGuesses_ExactlyOneWinner_AllCounted()
    {
        // G12/W20: паралельні спроби одного коду під maxAttempts=3 → рівно один consume, cap не обходиться
        // гонкою; кожна спроба порахована (attempt_count == N).
        const int attempts = 24;
        using var store = NewStore();
        store.Initialize();
        store.InsertInvite(Invite("hash-1", maxAttempts: 3));

        var winners = 0;
        Parallel.For(0, attempts, i =>
        {
            if (store.TryConsumeInvite("hash-1", $"id-{i}", FixedTime))
                Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
        Assert.Equal(attempts, store.GetInvite("hash-1")!.AttemptCount);
        Assert.True(store.GetInvite("hash-1")!.Consumed);
    }

    [Fact]
    public void AppendAbuseLog_ReturnsIncreasingRowIds()
    {
        using var store = NewStore();
        store.Initialize();

        var id1 = store.AppendAbuseLog(new AbuseLogRecord("invite_redeemed", "hmac-1", "salt-1", FixedTime));
        var id2 = store.AppendAbuseLog(new AbuseLogRecord("invite_redeemed", "hmac-2", "salt-2", FixedTime));

        Assert.True(id1 >= 1);
        Assert.Equal(id1 + 1, id2);
    }

    [Fact]
    public void State_SurvivesRestart()
    {
        using (var first = NewStore())
        {
            first.Initialize();
            first.InsertInvite(Invite("hash-1"));
            first.AppendAbuseLog(new AbuseLogRecord("invite_redeemed", "hmac-1", "salt-1", FixedTime));
        }

        using var second = NewStore();
        second.Initialize();
        Assert.NotNull(second.GetInvite("hash-1"));
        // Новий append продовжує rowid-послідовність — таблиця пережила рестарт.
        Assert.True(second.AppendAbuseLog(new AbuseLogRecord("x", "h", "s", FixedTime)) >= 2);
    }

    [Fact]
    public void Migration_V3ToV4_CreatesInviteAndAbuseTables_PreservingData()
    {
        var dbPath = Path.Combine(_dataDir, "durable.db");

        // Будуємо БД у стані схеми v3 (v1+v2+v3 DDL) з рядком даних і user_version=3.
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
                """);
            ExecRaw(seed,
                "INSERT INTO identities (id, role, created_at) VALUES ('id-1', 'admin', '2026-07-16T12:00:00.0000000+00:00');");
            ExecRaw(seed, "PRAGMA user_version = 3;");
        }
        SqliteConnection.ClearAllPools();

        using var store = NewStore();
        store.Initialize();

        // Мігровано до v4, дані v3 збереглися.
        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);
        Assert.True(store.SchemaVersion >= 4);
        Assert.Equal("admin", store.GetIdentity("id-1")!.Role);

        // Нові v4-таблиці існують (invite/abuse round-trip проходить).
        store.InsertInvite(Invite("hash-1"));
        Assert.NotNull(store.GetInvite("hash-1"));
        Assert.True(store.AppendAbuseLog(new AbuseLogRecord("invite_redeemed", "hmac", "salt", FixedTime)) >= 1);
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
