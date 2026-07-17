using System.Text;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// G1 single-instance invariant: an exclusive, process-lifetime lock on the data directory.
/// Two processes pointed at the same data directory cannot both start — the second refuses to
/// start without touching the database or budget state.
/// </summary>
/// <remarks>
/// Реалізація — <see cref="FileStream"/>, відкритий із <see cref="FileShare.None"/> і УТРИМУВАНИЙ
/// відкритим на весь час життя процесу. Це exclusive-lock на рівні ОС:
/// <list type="bullet">
///   <item>Windows: share-mode <c>None</c> блокує будь-яке інше відкриття файла.</item>
///   <item>Linux/macOS: .NET емулює share-mode через advisory <c>flock</c> (LOCK_EX) —
///   друге відкриття з <c>FileShare.None</c> кидає <see cref="IOException"/>.</item>
/// </list>
/// <b>Stale-lock самолікується:</b> ОС звільняє блокування, коли дескриптор закривається, зокрема
/// при аварійному завершенні процесу — тож «застряглий» lock від мертвого процесу НЕ блокує
/// наступний старт (на відміну від O_EXCL-по-імені-файла, що лишав би сирітський файл). Тому ми
/// НЕ видаляємо lock-файл на dispose і не робимо перевірок «свіжості» вручну.
/// </remarks>
public sealed class DataDirectoryLock : IDisposable
{
    private FileStream? _stream;

    private DataDirectoryLock(FileStream stream, string lockFilePath)
    {
        _stream = stream;
        LockFilePath = lockFilePath;
    }

    /// <summary>Absolute path of the held lock file (diagnostics only).</summary>
    public string LockFilePath { get; }

    /// <summary>
    /// Acquires the exclusive lock on <paramref name="dataDirectory"/>, creating the directory if
    /// needed. Throws <see cref="InvalidOperationException"/> if another process already holds it.
    /// </summary>
    /// <param name="dataDirectory">The data directory to guard.</param>
    /// <returns>A held lock; dispose it (process shutdown) to release.</returns>
    /// <exception cref="InvalidOperationException">The data directory is already locked by another instance.</exception>
    public static DataDirectoryLock Acquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        Directory.CreateDirectory(dataDirectory);
        var lockFilePath = Path.Combine(dataDirectory, ".instance.lock");

        FileStream stream;
        try
        {
            // FileShare.None + утримання відкритим = exclusive lock ОС (див. remarks).
            stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            // Другий процес на тому ж data-dir — refuse-start (G1). БД/бюджет не торкаємось.
            throw new InvalidOperationException(
                $"Каталог даних '{dataDirectory}' уже зайнятий іншим екземпляром сервера (G1: " +
                "single-instance інваріант, replicas=1). Зупиніть попередній процес перед стартом.",
                ex);
        }

        try
        {
            // Діагностика: pid + час старту (не секрет). Перезаписуємо вміст під час кожного захоплення.
            stream.SetLength(0);
            var info = $"pid={Environment.ProcessId} startedAtUtc={DateTimeOffset.UtcNow:O}\n";
            stream.Write(Encoding.UTF8.GetBytes(info));
            stream.Flush();
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return new DataDirectoryLock(stream, lockFilePath);
    }

    /// <summary>Releases the lock (closes the underlying handle). Idempotent.</summary>
    public void Dispose()
    {
        // Файл навмисно НЕ видаляємо — важливе саме закриття дескриптора (звільняє блокування ОС).
        _stream?.Dispose();
        _stream = null;
    }
}
