using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SignalCliNet.WsRpcServer.Tests.TestSupport;

/// <summary>
/// Потокобезпечний <see cref="ILogger{TCategoryName}"/>, що збирає відформатовані лог-рядки у список —
/// для redact-перевірок (жоден рядок не містить чутливого URI / повного sessionId).
/// </summary>
/// <typeparam name="T">Категорія логера.</typeparam>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Усі зібрані рядки (у порядку логування).</summary>
    public IReadOnlyCollection<string> Lines => _lines;

    /// <summary>Чи містить БУДЬ-ЯКИЙ зібраний рядок підстроку <paramref name="value"/>.</summary>
    public bool AnyContains(string value) =>
        _lines.Any(l => l.Contains(value, StringComparison.Ordinal));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : exception.ToString()));
    }
}
