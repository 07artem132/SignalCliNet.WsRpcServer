using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SignalCliNet.WsRpcServer.Extensions;
using SignalCliNet.WsRpcServer.Persistence;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Deployment;

/// <summary>
/// DoD-тести секції 5 (add-secure-deploy-persistence) через РЕАЛЬНИЙ шлях виконання: повний
/// <see cref="IHost"/> з композиційним коренем <c>AddSecureDeployment</c> (ValidateOnStart → D4 →
/// G1-lock → провіжн секретів → init durable-стору) — не прямі виклики компонентів.
/// 5.1: другий екземпляр на тому ж data-dir → refuse-start (G1).
/// 5.3: durable-стан переживає повний цикл рестарту хоста; CA переюзається (client-trust цілий).
/// 5.4: corrupt DB → fail-start; restore з backup повертає роботу; міграція проходить (G4).
/// </summary>
public sealed class SecureDeploymentHostStartupTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-host-dod-tests-" + Guid.NewGuid().ToString("N"));

    private static IHost BuildHost(string dataDirectory) =>
        new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:DataDirectory"] = dataDirectory,
                    ["Server:Host"] = "127.0.0.1",
                }))
            .ConfigureServices((context, services) => services.AddSecureDeployment(context.Configuration))
            .Build();

    [Fact]
    public async Task SecondHost_OnSameDataDirectory_RefusesStart()
    {
        // 5.1 (G1): перший хост тримає exclusive lock — другий на тому ж каталозі мусить
        // детерміновано відмовитись стартувати ще ДО відкриття БД.
        using var first = BuildHost(_dataDir);
        await first.StartAsync(CancellationToken.None);

        using var second = BuildHost(_dataDir);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.StartAsync(CancellationToken.None));
        Assert.Contains("G1", ex.Message);

        await first.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DataDirectory_AfterCleanShutdown_IsReusable()
    {
        // 5.1-доповнення: чистий stop звільняє lock — наступний запуск на тому ж каталозі працює
        // (deploy = downtime-window W15: стоп старого → старт нового).
        using (var first = BuildHost(_dataDir))
        {
            await first.StartAsync(CancellationToken.None);
            await first.StopAsync(CancellationToken.None);
        }

        using var second = BuildHost(_dataDir);
        await second.StartAsync(CancellationToken.None);
        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DurableState_SurvivesFullHostRestart()
    {
        // 5.3: identity + прив'язки, записані через IDurableStore працюючого хоста, валідні після
        // повного циклу stop/dispose → новий хост. (Звуження за T1: таблиці token/бюджету додають
        // їхні changes міграціями v2+ — тут пінимо identity/bindings + сам механізм durability.)
        using (var first = BuildHost(_dataDir))
        {
            await first.StartAsync(CancellationToken.None);

            var store = first.Services.GetRequiredService<IDurableStore>();
            store.UpsertIdentity(new IdentityRecord("id-dod", "admin", ["+15551230001"], FixedTime));
            store.UpsertAccountBinding(
                new AccountBinding("+15551230001", "id-dod", IsPrivate: true, FixedTime));

            await first.StopAsync(CancellationToken.None);
        }

        var caBeforeRestart = ReadCaCertificate();

        using var second = BuildHost(_dataDir);
        await second.StartAsync(CancellationToken.None);

        var restarted = second.Services.GetRequiredService<IDurableStore>();
        var identity = restarted.GetIdentity("id-dod");
        Assert.NotNull(identity);
        Assert.Equal("admin", identity!.Role);
        Assert.Equal(["+15551230001"], identity.LinkedAccounts.ToArray());

        var binding = restarted.GetAccountBinding("+15551230001");
        Assert.NotNull(binding);
        Assert.Equal("id-dod", binding!.OwningIdentityId);
        Assert.True(binding.IsPrivate);

        // Рестарт переюзає той самий CA — інакше client-trust зламано (R3.4).
        Assert.Equal(caBeforeRestart, ReadCaCertificate());

        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CorruptDatabase_FailsStart_AndRestoreFromBackupRecovers()
    {
        // 5.4 (G4): пошкоджена БД блокує старт хоста (integrity-check у стартовому сервісі);
        // restore з online-backup повертає роботу, міграція проходить, дані на місці.
        var backupPath = Path.Combine(_dataDir, "backup", "durable.bak.db");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

        using (var first = BuildHost(_dataDir))
        {
            await first.StartAsync(CancellationToken.None);

            var store = first.Services.GetRequiredService<IDurableStore>();
            store.UpsertIdentity(new IdentityRecord("id-dod", "admin", ["+15551230001"], FixedTime));
            store.Backup(backupPath);

            await first.StopAsync(CancellationToken.None);
        }
        SqliteConnection.ClearAllPools();

        // Псуємо все після SQLite-заголовка (сторінка схеми включно) — integrity-check мусить впасти.
        var databasePath = Path.Combine(_dataDir, "durable.db");
        var bytes = File.ReadAllBytes(databasePath);
        for (var i = 100; i < bytes.Length; i++)
            bytes[i] = 0xEE;
        File.WriteAllBytes(databasePath, bytes);

        using (var corrupted = BuildHost(_dataDir))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => corrupted.StartAsync(CancellationToken.None));
        }
        SqliteConnection.ClearAllPools();

        // Restore за документованою процедурою (DEPLOYMENT.md): підмінити файл БД копією backup
        // (WAL/SHM-залишки прибрати) і стартувати заново.
        File.Copy(backupPath, databasePath, overwrite: true);
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");

        using var restored = BuildHost(_dataDir);
        await restored.StartAsync(CancellationToken.None);

        var restoredStore = restored.Services.GetRequiredService<IDurableStore>();
        Assert.Equal(SchemaMigrator.LatestVersion, restoredStore.SchemaVersion);
        var identity = restoredStore.GetIdentity("id-dod");
        Assert.NotNull(identity);
        Assert.Equal("admin", identity!.Role);

        await restored.StopAsync(CancellationToken.None);
    }

    private byte[] ReadCaCertificate() =>
        File.ReadAllBytes(Path.Combine(_dataDir, "secrets", "ca.crt"));

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
