using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить tamper-evident audit-trail (add-invites-admin, task 3.2 / M8): append будує коректний
/// hash-chain (<see cref="AuditLogService.VerifyChain"/> = true); підробка/видалення запису рве ланцюг
/// (VerifyChain = false); сирі деталі НЕ зберігаються (лише detail_hash — privacy).
/// </summary>
public sealed class AuditLogServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-audit-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath;

    public AuditLogServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _dbPath = Path.Combine(_dataDir, "durable.db");
    }

    private (AuditLogService Audit, DurableStore Store) NewService()
    {
        var store = new DurableStore(_dbPath, NullLogger<DurableStore>.Instance);
        store.Initialize();
        var audit = new AuditLogService(store, new TestTimeProvider(FixedTime), NullLogger<AuditLogService>.Instance);
        return (audit, store);
    }

    [Fact]
    public void Append_BuildsChain_VerifyChainTrue()
    {
        var (audit, store) = NewService();
        using var _ = store;

        var s1 = audit.Append("invite_created", "admin", "code-hash-1");
        var s2 = audit.Append("identity_revoked", "admin", "id-victim");
        var s3 = audit.Append("admin_list_accounts", "admin", "accounts=2;legacyBound=1");

        Assert.Equal(1, s1);
        Assert.Equal(2, s2);
        Assert.Equal(3, s3);
        Assert.True(audit.VerifyChain());

        // Genesis: перший запис має prev = 64 нулі; кожен наступний prev = попередній entry_hash.
        var entries = store.GetAuditEntries();
        Assert.Equal(new string('0', 64), entries[0].PrevHash);
        Assert.Equal(entries[0].EntryHash, entries[1].PrevHash);
        Assert.Equal(entries[1].EntryHash, entries[2].PrevHash);
    }

    [Fact]
    public void VerifyChain_EmptyLog_IsTrue()
    {
        var (audit, store) = NewService();
        using var _ = store;

        Assert.True(audit.VerifyChain());
    }

    [Fact]
    public void TamperedEntry_BreaksChain_VerifyChainFalse()
    {
        var (audit, store) = NewService();
        using var _ = store;
        audit.Append("invite_created", "admin", "code-hash-1");
        audit.Append("identity_revoked", "admin", "id-victim");

        // Підробка: змінюємо actor запису seq=1 напряму у БД (обходячи hash-chain).
        ExecRaw($"UPDATE audit_log SET actor_identity = 'attacker' WHERE seq = 1;");

        Assert.False(audit.VerifyChain());
    }

    [Fact]
    public void DeletedEntry_BreaksChain_VerifyChainFalse()
    {
        var (audit, store) = NewService();
        using var _ = store;
        audit.Append("e1", "admin", "d1");
        audit.Append("e2", "admin", "d2");
        audit.Append("e3", "admin", "d3");

        // Видалення середнього запису → розрив послідовності seq і prev-звʼязку.
        ExecRaw("DELETE FROM audit_log WHERE seq = 2;");

        Assert.False(audit.VerifyChain());
    }

    [Fact]
    public void Append_DoesNotStoreRawDetail_OnlyItsHash()
    {
        var (audit, store) = NewService();
        using var _ = store;
        const string sensitiveDetail = "code-hash-super-secret-subject";

        audit.Append("invite_created", "admin", sensitiveDetail);

        var entry = store.GetLastAuditEntry()!;
        // Сирий subject НЕ у detail_hash (це SHA-256 hex, 64 символи), і його ніде немає у рядку.
        Assert.Equal(64, entry.DetailHash.Length);
        Assert.DoesNotContain(sensitiveDetail, entry.DetailHash, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetail, entry.EntryHash, StringComparison.Ordinal);
    }

    private void ExecRaw(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
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
