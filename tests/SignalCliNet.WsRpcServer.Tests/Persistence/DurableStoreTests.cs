using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Persistence;

/// <summary>
/// Пінить durable-стор (G4/R3.5): міграція схеми, round-trip identity/binding, durability через
/// «рестарт» (новий інстанс на тому ж файлі), backup/restore і fail-start на пошкодженій БД.
/// </summary>
public class DurableStoreTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-store-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    public DurableStoreTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore(string? fileName = null) =>
        new(Path.Combine(_dataDir, fileName ?? "durable.db"), NullLogger<DurableStore>.Instance);

    [Fact]
    public void Initialize_MigratesToLatestSchemaVersion()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);
        Assert.True(store.SchemaVersion >= 1);
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        using var store = NewStore();
        store.Initialize();
        store.Initialize(); // не має кидати чи повторно мігрувати

        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);
    }

    [Fact]
    public void Identity_RoundTrips_WithLinkedAccounts()
    {
        using var store = NewStore();
        store.Initialize();

        var identity = new IdentityRecord("id-1", "admin", ["+15551230001", "+15551230002"], FixedTime);
        store.UpsertIdentity(identity);

        var loaded = store.GetIdentity("id-1");
        Assert.NotNull(loaded);
        Assert.Equal("admin", loaded!.Role);
        Assert.Equal(FixedTime, loaded.CreatedAt);
        Assert.Equal(["+15551230001", "+15551230002"], loaded.LinkedAccounts.OrderBy(a => a).ToArray());
    }

    [Fact]
    public void UpsertIdentity_ReplacesLinkedAccountsAndRole()
    {
        using var store = NewStore();
        store.Initialize();

        store.UpsertIdentity(new IdentityRecord("id-1", "user", ["+15551230001"], FixedTime));
        store.UpsertIdentity(new IdentityRecord("id-1", "admin", ["+15551239999"], FixedTime.AddDays(1)));

        var loaded = store.GetIdentity("id-1");
        Assert.NotNull(loaded);
        Assert.Equal("admin", loaded!.Role);
        Assert.Equal(["+15551239999"], loaded.LinkedAccounts.ToArray());
        // created_at не оновлюється при повторному upsert (ставиться лише на першій вставці).
        Assert.Equal(FixedTime, loaded.CreatedAt);
    }

    [Fact]
    public void GetIdentity_WhenAbsent_ReturnsNull()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.Null(store.GetIdentity("missing"));
    }

    [Fact]
    public void AccountBinding_RoundTrips_WithPrivateFlag()
    {
        using var store = NewStore();
        store.Initialize();

        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));
        store.UpsertAccountBinding(new AccountBinding("+15551230001", "id-1", IsPrivate: true, FixedTime));

        var binding = store.GetAccountBinding("+15551230001");
        Assert.NotNull(binding);
        Assert.Equal("id-1", binding!.OwningIdentityId);
        Assert.True(binding.IsPrivate);
        Assert.Equal(FixedTime, binding.CreatedAt);

        var owned = store.GetBindingsForIdentity("id-1");
        Assert.Single(owned);
        Assert.Equal("+15551230001", owned[0].Account);
    }

    [Fact]
    public void State_SurvivesRestart()
    {
        // Пишемо одним інстансом, читаємо іншим на тому ж файлі — durability через рестарт.
        using (var first = NewStore())
        {
            first.Initialize();
            first.UpsertIdentity(new IdentityRecord("id-1", "admin", ["+15551230001"], FixedTime));
            first.UpsertAccountBinding(new AccountBinding("+15551230001", "id-1", IsPrivate: false, FixedTime));
        }

        using var second = NewStore();
        second.Initialize();

        var identity = second.GetIdentity("id-1");
        Assert.NotNull(identity);
        Assert.Equal(["+15551230001"], identity!.LinkedAccounts.ToArray());

        var binding = second.GetAccountBinding("+15551230001");
        Assert.NotNull(binding);
        Assert.False(binding!.IsPrivate);
    }

    [Fact]
    public void Backup_ProducesRestorableCopy()
    {
        var backupPath = Path.Combine(_dataDir, "backup", "durable.bak.db");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

        using (var store = NewStore())
        {
            store.Initialize();
            store.UpsertIdentity(new IdentityRecord("id-1", "admin", ["+15551230001"], FixedTime));
            store.Backup(backupPath);
        }

        // Відкриваємо backup як окремий стор — дані на місці, integrity/міграції проходять (restore).
        using var restored = new DurableStore(backupPath, NullLogger<DurableStore>.Instance);
        restored.Initialize();

        var identity = restored.GetIdentity("id-1");
        Assert.NotNull(identity);
        Assert.Equal("admin", identity!.Role);
    }

    [Fact]
    public void Initialize_OnCorruptDatabase_FailsStart()
    {
        var dbPath = Path.Combine(_dataDir, "durable.db");

        // Будуємо валідну БД у DELETE-журналі (усе в основному файлі), з даними на кілька сторінок.
        using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            seed.Open();
            ExecRaw(seed, "PRAGMA journal_mode=DELETE;");
            ExecRaw(seed, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);");
            for (var i = 0; i < 500; i++)
                ExecRaw(seed, $"INSERT INTO t (v) VALUES ('row-{i}-padding-padding-padding-padding');");
        }
        SqliteConnection.ClearAllPools();

        // Псуємо середину файлу (b-tree сторінки даних), лишаючи SQLite-магію в заголовку.
        var bytes = File.ReadAllBytes(dbPath);
        for (var i = 4096; i < Math.Min(bytes.Length, 4096 + 4096); i++)
            bytes[i] = 0xEE;
        File.WriteAllBytes(dbPath, bytes);

        // integrity-check на старті має заблокувати запуск (G4).
        using var store = new DurableStore(dbPath, NullLogger<DurableStore>.Instance);
        Assert.ThrowsAny<Exception>(store.Initialize);
    }

    [Fact]
    public void Token_RoundTrips()
    {
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));

        var token = new TokenRecord("hash-1", 1, "id-1", FixedTime.AddHours(12), IsRevoked: false, FixedTime);
        store.InsertToken(token);

        var loaded = store.GetToken("hash-1");
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.PepperVersion);
        Assert.Equal("id-1", loaded.IdentityId);
        Assert.Equal(FixedTime.AddHours(12), loaded.ExpiresAt);
        Assert.False(loaded.IsRevoked);
        Assert.Equal(FixedTime, loaded.CreatedAt);
    }

    [Fact]
    public void GetToken_WhenAbsent_ReturnsNull()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.Null(store.GetToken("missing"));
    }

    [Fact]
    public void RotateToken_RevokesOld_AndInsertsNew_InOneStore()
    {
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));
        store.InsertToken(new TokenRecord("old", 1, "id-1", FixedTime.AddHours(12), false, FixedTime));

        store.RotateToken("old", new TokenRecord("new", 1, "id-1", FixedTime.AddHours(24), false, FixedTime));

        // Zero-overlap (W21): старий revoked, новий живий — обидва присутні.
        Assert.True(store.GetToken("old")!.IsRevoked);
        var fresh = store.GetToken("new");
        Assert.NotNull(fresh);
        Assert.False(fresh!.IsRevoked);
    }

    [Fact]
    public void RotateToken_WithUnknownPredecessor_Throws_AndDoesNotInsert()
    {
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));

        Assert.Throws<InvalidOperationException>(
            () => store.RotateToken("nope", new TokenRecord("new", 1, "id-1", FixedTime.AddHours(24), false, FixedTime)));

        // Транзакція відкотилась — новий токен не вставлено.
        Assert.Null(store.GetToken("new"));
    }

    [Fact]
    public void RevokeIdentityCredentials_Cascades_ToTokensAndDeviceSecret()
    {
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));
        store.UpsertIdentity(new IdentityRecord("id-2", "user", [], FixedTime));

        store.InsertToken(new TokenRecord("t1", 1, "id-1", FixedTime.AddHours(12), false, FixedTime));
        store.InsertToken(new TokenRecord("t2", 1, "id-1", FixedTime.AddHours(12), false, FixedTime));
        store.InsertToken(new TokenRecord("other", 1, "id-2", FixedTime.AddHours(12), false, FixedTime));
        store.UpsertDeviceSecret(new DeviceSecretRecord("id-1", "pub-1", "ed25519", false, FixedTime));

        store.RevokeIdentityCredentials("id-1");

        // Каскад: усі токени id-1 revoked, device-секрет revoked; чужа identity незаймана (task 1.4).
        Assert.True(store.GetToken("t1")!.IsRevoked);
        Assert.True(store.GetToken("t2")!.IsRevoked);
        Assert.True(store.GetDeviceSecret("id-1")!.IsRevoked);
        Assert.False(store.GetToken("other")!.IsRevoked);
    }

    [Fact]
    public void DeviceSecret_RoundTrips_AndUpsertReplacesKey()
    {
        using var store = NewStore();
        store.Initialize();
        store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));

        store.UpsertDeviceSecret(new DeviceSecretRecord("id-1", "pub-a", "ed25519", false, FixedTime));
        var first = store.GetDeviceSecret("id-1");
        Assert.NotNull(first);
        Assert.Equal("pub-a", first!.PublicKey);
        Assert.Equal("ed25519", first.Algorithm);
        Assert.False(first.IsRevoked);

        // Re-enrollment ключа (task 3.3) — оновлює public_key/algorithm; created_at лишається.
        store.UpsertDeviceSecret(new DeviceSecretRecord("id-1", "pub-b", "p256", false, FixedTime.AddDays(1)));
        var second = store.GetDeviceSecret("id-1");
        Assert.Equal("pub-b", second!.PublicKey);
        Assert.Equal("p256", second.Algorithm);
        Assert.Equal(FixedTime, second.CreatedAt);
    }

    [Fact]
    public void GetDeviceSecret_WhenAbsent_ReturnsNull()
    {
        using var store = NewStore();
        store.Initialize();

        Assert.Null(store.GetDeviceSecret("missing"));
    }

    [Fact]
    public void Migration_V1ToV2_PreservesExistingData()
    {
        var dbPath = Path.Combine(_dataDir, "durable.db");

        // Будуємо БД у стані схеми v1 (замороженого) з рядком даних і user_version=1.
        using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            seed.Open();
            ExecRaw(seed,
                """
                CREATE TABLE identities (
                    id          TEXT PRIMARY KEY,
                    role        TEXT NOT NULL,
                    created_at  TEXT NOT NULL
                ) STRICT;

                CREATE TABLE identity_accounts (
                    identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                    account     TEXT NOT NULL,
                    PRIMARY KEY (identity_id, account)
                ) STRICT;

                CREATE TABLE account_bindings (
                    account            TEXT PRIMARY KEY,
                    owning_identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                    is_private         INTEGER NOT NULL,
                    created_at         TEXT NOT NULL
                ) STRICT;

                CREATE INDEX ix_account_bindings_identity ON account_bindings(owning_identity_id);
                """);
            ExecRaw(seed,
                "INSERT INTO identities (id, role, created_at) VALUES ('id-1', 'admin', '2026-07-16T12:00:00.0000000+00:00');");
            ExecRaw(seed, "PRAGMA user_version = 1;");
        }
        SqliteConnection.ClearAllPools();

        using var store = NewStore();
        store.Initialize();

        // Мігровано до v2, дані v1 збереглися.
        Assert.Equal(SchemaMigrator.LatestVersion, store.SchemaVersion);
        Assert.True(store.SchemaVersion >= 2);

        var identity = store.GetIdentity("id-1");
        Assert.NotNull(identity);
        Assert.Equal("admin", identity!.Role);

        // Нові v2-таблиці існують (запис токена/device-секрета проходить).
        store.InsertToken(new TokenRecord("hash-1", 1, "id-1", FixedTime.AddHours(12), false, FixedTime));
        Assert.NotNull(store.GetToken("hash-1"));
        store.UpsertDeviceSecret(new DeviceSecretRecord("id-1", "pub", "ed25519", false, FixedTime));
        Assert.NotNull(store.GetDeviceSecret("id-1"));
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
