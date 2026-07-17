using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Tests.Security.Admission;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="AbuseLogService"/> (task 3.3): per-record salt відрізняється, той самий
/// subject+caller дає РІЗНІ hmac (не equality-oracle, W24), і сирий subject не лежить у сторі як
/// plaintext.
/// </summary>
public class AbuseLogServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-abuse-svc-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath;
    private readonly DurableStore _store;
    private readonly AbuseLogService _service;

    public AbuseLogServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _dbPath = Path.Combine(_dataDir, "durable.db");
        _store = new DurableStore(_dbPath, NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _service = new AbuseLogService(
            _store, new FakeAbusePepperProvider(), new TestTimeProvider(FixedTime),
            NullLogger<AbuseLogService>.Instance);
    }

    [Fact]
    public void Record_SameSubjectAndCaller_ProducesDifferentSaltAndHmac()
    {
        _service.Record("invite_redeemed", "caller-1", "secret-subject");
        _service.Record("invite_redeemed", "caller-1", "secret-subject");

        var rows = ReadAbuseLog();
        Assert.Equal(2, rows.Count);

        // Per-record salt відрізняється → W24: той самий subject дає РІЗНІ hmac (не equality-oracle).
        Assert.NotEqual(rows[0].Salt, rows[1].Salt);
        Assert.NotEqual(rows[0].Hmac, rows[1].Hmac);
    }

    [Fact]
    public void Record_DoesNotStoreRawSubject()
    {
        const string subject = "sensitive-code-hash-plain";
        _service.Record("invite_redeemed", "caller-1", subject);

        var rows = ReadAbuseLog();
        var row = Assert.Single(rows);

        // Ані hmac, ані salt не дорівнюють сирому суб'єкту й не містять його як підрядок.
        Assert.NotEqual(subject, row.Hmac);
        Assert.NotEqual(subject, row.Salt);
        Assert.DoesNotContain(subject, row.Hmac);
        Assert.DoesNotContain(subject, row.Salt);
        Assert.DoesNotContain(subject, row.EventType);
    }

    [Fact]
    public void Record_DifferentSubjects_ProduceDifferentHmac()
    {
        _service.Record("invite_redeemed", "caller-1", "subject-a");
        _service.Record("invite_redeemed", "caller-1", "subject-b");

        var rows = ReadAbuseLog();
        Assert.NotEqual(rows[0].Hmac, rows[1].Hmac);
    }

    // Читаємо abuse_log напряму з файла БД (окреме з'єднання; WAL дозволяє паралельних читачів).
    private List<(string EventType, string Hmac, string Salt)> ReadAbuseLog()
    {
        var result = new List<(string, string, string)>();
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type, subject_hmac, salt FROM abuse_log ORDER BY id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _store.Dispose();
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
