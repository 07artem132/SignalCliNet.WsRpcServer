using SignalCliNet.WsRpcServer.Deployment;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Deployment;

/// <summary>
/// Пінить G1 single-instance інваріант (<see cref="DataDirectoryLock"/>): другий захват того ж
/// каталогу → refuse; після dispose каталог знову вільний (stale-lock самолікується через ОС).
/// </summary>
public class DataDirectoryLockTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-lock-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Acquire_CreatesDirectoryAndLockFile()
    {
        using var held = DataDirectoryLock.Acquire(_dataDir);

        Assert.True(Directory.Exists(_dataDir));
        Assert.True(File.Exists(held.LockFilePath));
    }

    [Fact]
    public void Acquire_SecondInstanceOnSameDir_Throws()
    {
        using var first = DataDirectoryLock.Acquire(_dataDir);

        var ex = Assert.Throws<InvalidOperationException>(() => DataDirectoryLock.Acquire(_dataDir));
        Assert.Contains("G1", ex.Message);
    }

    [Fact]
    public void Acquire_AfterRelease_Succeeds()
    {
        var first = DataDirectoryLock.Acquire(_dataDir);
        first.Dispose();

        // Після звільнення каталог знову доступний для нового екземпляра.
        using var second = DataDirectoryLock.Acquire(_dataDir);
        Assert.NotNull(second.LockFilePath);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var held = DataDirectoryLock.Acquire(_dataDir);
        held.Dispose();
        held.Dispose(); // не має кидати
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
