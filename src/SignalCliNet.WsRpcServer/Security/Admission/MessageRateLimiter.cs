namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// Per-connection message-rate limiter — a token bucket refilled on the MONOTONIC clock (D15, task 3.2).
/// The bucket holds up to <c>capacity</c> tokens (= <c>MessagesPerMinutePerConnection</c>); each accepted
/// data frame consumes one. A full bucket permits a burst of <c>capacity</c>, then the rate settles to
/// <c>capacity / window</c>. Exceeding it closes the socket with <c>4429</c> (<c>message-rate:&lt;sec&gt;</c>).
/// </summary>
/// <remarks>
/// Один екземпляр на з'єднання (стан живе в сесії), тож contention мінімальний — але доступ усе одно
/// серіалізуємо локом (NetCoreServer може доставляти кадри з різних потоків IO-пулу). Рефіл рахується
/// монотонним годинником через <see cref="TimeProvider.GetElapsedTime(long, long)"/>, тож стрибки
/// wall-clock не впливають (D15).
/// </remarks>
public sealed class MessageRateLimiter
{
    private readonly double _capacity;
    private readonly double _tokensPerSecond;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    private double _tokens;
    private long _lastTimestamp;

    /// <summary>Creates the bucket.</summary>
    /// <param name="capacity">Bucket capacity (messages) — also the maximum burst. MUST be ≥ 1.</param>
    /// <param name="refillWindow">The window over which <paramref name="capacity"/> tokens refill (e.g. 1 minute).</param>
    /// <param name="timeProvider">System clock (monotonic timestamp for refill).</param>
    public MessageRateLimiter(int capacity, TimeSpan refillWindow, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(refillWindow, TimeSpan.Zero);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _capacity = capacity;
        _tokensPerSecond = capacity / refillWindow.TotalSeconds;
        _tokens = capacity;   // burst = ємність: свіже з'єднання починає з повним бакетом
        _lastTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Tries to consume one token for an accepted data frame. Returns <c>true</c> if within rate,
    /// <c>false</c> if the bucket is empty (caller closes <c>4429</c>).
    /// </summary>
    public bool TryConsume()
    {
        lock (_sync)
        {
            Refill();
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Seconds until the bucket refills at least one token (for the in-band <c>message-rate:&lt;sec&gt;</c>
    /// hint), ≥ 1. Meaningful right after a <see cref="TryConsume"/> that returned <c>false</c>.
    /// </summary>
    public int RetryAfterSeconds()
    {
        lock (_sync)
        {
            Refill();
            if (_tokens >= 1.0)
                return 1;

            var seconds = (1.0 - _tokens) / _tokensPerSecond;
            return Math.Max(1, (int)Math.Ceiling(seconds));
        }
    }

    // Доливаємо токени пропорційно монотонно пройденому часу, cap на ємності.
    private void Refill()
    {
        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;

        if (elapsed <= TimeSpan.Zero)
            return;

        _tokens = Math.Min(_capacity, _tokens + elapsed.TotalSeconds * _tokensPerSecond);
    }
}
