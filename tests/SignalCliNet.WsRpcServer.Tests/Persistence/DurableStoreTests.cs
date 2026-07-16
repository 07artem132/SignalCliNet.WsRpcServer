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
