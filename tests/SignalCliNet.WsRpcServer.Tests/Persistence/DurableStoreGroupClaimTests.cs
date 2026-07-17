using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Persistence;

/// <summary>
/// Пінить group-claim/binding-частину durable-стору (add-group-claim-receive, секція 2): round-trip
/// claim/binding, атомарний consume (W20, паралельний), revoke-каскад на identity й міграцію v5→v6.
/// </summary>
public sealed class DurableStoreGroupClaimTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-groupclaim-store-tests-" + Guid.NewGuid().ToString("N"));

    public DurableStoreGroupClaimTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore(string? fileName = null) =>
        new(Path.Combine(_dataDir, fileName ?? "durable.db"), NullLogger<DurableStore>.Instance);

    private static GroupClaimRecord Claim(
        string hash, string createdBy = "id-u", bool consumed = false, DateTimeOffset? expiresAt = null) =>
        new(hash, createdBy, consumed, expiresAt ?? FixedTime.AddMinutes(10), FixedTime);

    private static GroupBindingRecord Binding(
        string id, string identity = "id-u", string group = "grp-1", string anchor = "aci-1",
        bool revoked = false, DateTimeOffset? expiresAt = null) =>
        new(id, identity, group, anchor, expiresAt ?? FixedTime.AddHours(720), revoked, FixedTime);

    [Fact]
    public void GroupClaim_RoundTrips()
    {
        using var store = NewStore();
        store.Initialize();

        store.InsertGroupClaim(Claim("hash-1"));

        var loaded = store.GetGroupClaim("hash-1");
        Assert.NotNull(loaded);
        Assert.Equal("id-u", loaded!.CreatedBy);
        Assert.False(loaded.Consumed);
        Assert.Equal(FixedTime.AddMinutes(10), loaded.ExpiresAt);
    }

    [Fact]
    public void InsertGroupClaim_DuplicateHash_Throws()
    {
        using var store = NewStore();
        store.Initialize();
        store.InsertGroupClaim(Claim("dup"));

        Assert.Throws<SqliteException>(() => store.InsertGroupClaim(Claim("dup")));
    }

    [Fact]
    public void TryConsumeGroupClaim_HappyPath_ConsumesOnce()
    {
        using var store = NewStore();
        store.Initialize();
        store.InsertGroupClaim(Claim("hash-1"));

        Assert.True(store.TryConsumeGroupClaim("hash-1", FixedTime));
        Assert.True(store.GetGroupClaim("hash-1")!.Consumed);

        // Повторний consume → false (one-time).
        Assert.False(store.TryConsumeGroupClaim("hash-1", FixedTime));
    }

    [Fact]
    public void TryConsumeGroupClaim_UnknownOrExpired_ReturnsFalse()
    {
        using var store = NewStore();
        store.Initialize();
        store.InsertGroupClaim(Claim("hash-exp", expiresAt: FixedTime));

        Assert.False(store.TryConsumeGroupClaim("nope", FixedTime));       // невідомий
        Assert.False(store.TryConsumeGroupClaim("hash-exp", FixedTime));   // expires <= now
        Assert.False(store.GetGroupClaim("hash-exp")!.Consumed);
    }

    [Fact]
    public void TryConsumeGroupClaim_ParallelGuesses_ExactlyOneWinner()
    {
        // W20: паралельні consume одного коду → рівно один виграє (умовний UPDATE WHERE consumed=0).
        const int attempts = 32;
        using var store = NewStore();
        store.Initialize();
        store.InsertGroupClaim(Claim("hash-1"));

        var winners = 0;
        Parallel.For(0, attempts, _ =>
        {
            if (store.TryConsumeGroupClaim("hash-1", FixedTime))
                Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
        Assert.True(store.GetGroupClaim("hash-1")!.Consumed);
    }

    [Fact]
    public void GroupBinding_RoundTrips_AndGetsLatestPerGroup()
    {
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-u", "user", [], FixedTime));

        store.InsertGroupBinding(Binding("b-1", anchor: "aci-old"));
        var loaded = store.GetGroupBinding("id-u", "grp-1");
        Assert.NotNull(loaded);
        Assert.Equal("aci-old", loaded!.AnchorAci);
        Assert.Equal("b-1", loaded.BindingId);

        // Re-claim: новіший binding для (identity, group) → GetGroupBinding повертає останній.
        store.InsertGroupBinding(new GroupBindingRecord(
            "b-2", "id-u", "grp-1", "aci-new", FixedTime.AddHours(720), false, FixedTime.AddMinutes(1)));
        Assert.Equal("aci-new", store.GetGroupBinding("id-u", "grp-1")!.AnchorAci);

        // Списки/фільтрація власних.
        Assert.Equal(2, store.GetGroupBindingsForIdentity("id-u").Count);
        Assert.Empty(store.GetGroupBindingsForIdentity("other"));
    }

    [Fact]
    public void RevokeGroupBinding_And_ForIdentity()
    {
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-u", "user", [], FixedTime));
        store.InsertGroupBinding(Binding("b-1", group: "grp-1"));
        store.InsertGroupBinding(Binding("b-2", group: "grp-2"));

        store.RevokeGroupBinding("b-1");
        Assert.True(store.GetGroupBinding("id-u", "grp-1")!.Revoked);
        Assert.False(store.GetGroupBinding("id-u", "grp-2")!.Revoked);

        store.RevokeGroupBindingsForIdentity("id-u");
        Assert.True(store.GetGroupBinding("id-u", "grp-2")!.Revoked);
    }

    [Fact]
    public void RevokeIdentityCredentials_CascadesToGroupBindings()
    {
        // task 2.4: revoke identity атомарно гасить і її group-bindings.
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-u", "user", [], FixedTime));
        store.InsertGroupBinding(Binding("b-1"));

        store.RevokeIdentityCredentials("id-u");

        Assert.True(store.GetGroupBinding("id-u", "grp-1")!.Revoked);
    }

    [Fact]
    public void Migration_V5ToV6_CreatesGroupTables_PreservingData()
    {
        var dbPath = Path.Combine(_dataDir, "durable.db");

        using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            seed.Open();
            // Мінімальна v5-схема (identities + identity_accounts, user_version=5) — достатньо для
            // перевірки міграції v6 (group_bindings.identity_id FK → identities; GetIdentity читає accounts).
            ExecRaw(seed,
                """
                CREATE TABLE identities (
                    id TEXT PRIMARY KEY, role TEXT NOT NULL, created_at TEXT NOT NULL
                ) STRICT;
                CREATE TABLE identity_accounts (
                    identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                    account TEXT NOT NULL, PRIMARY KEY (identity_id, account)
                ) STRICT;
                INSERT INTO identities (id, role, created_at)
                VALUES ('id-u', 'user', '2026-07-17T12:00:00.0000000+00:00');
                PRAGMA user_version = 5;
                """);
        }
        SqliteConnection.ClearAllPools();

        using var store = NewStore();
        store.Initialize();

        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);
        Assert.True(store.SchemaVersion >= 6);
        Assert.Equal("user", store.GetIdentity("id-u")!.Role); // v5-дані збереглися

        // Нові v6-таблиці існують (claim/binding round-trip проходить).
        store.InsertGroupClaim(Claim("hash-1"));
        Assert.NotNull(store.GetGroupClaim("hash-1"));
        store.InsertGroupBinding(Binding("b-1"));
        Assert.NotNull(store.GetGroupBinding("id-u", "grp-1"));
    }

    private static void ExecRaw(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
}
