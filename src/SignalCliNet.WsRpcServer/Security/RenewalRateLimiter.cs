using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// In-memory sliding-window rate limiter for token renewal (T5): at most
/// <see cref="AuthOptions.RenewalPerIdentityPerHour"/> successful renewal challenges per identity per
/// rolling hour. Bounds how often an expired-token holder can drive the renewal path.
/// </summary>
/// <remarks>
/// Singleton, лише в пам'яті процесу (рестарт скидає вікна — прийнятно: це anti-abuse throttle, а не
/// безпекова гарантія; revocation лишається головним важелем, D7). Вікно — ковзне: при кожній спробі
/// відкидаємо позначки часу, старші за годину. Ізоляція per-identity: у кожної identity власна черга,
/// тож бюджет однієї не з'їдає бюджет іншої. Перевищення cap → <c>TryAcquire</c> повертає <c>false</c>,
/// і сесія закриває сокет <c>4401 expired</c> (renewal тимчасово недоступний).
/// </remarks>
public sealed class RenewalRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _events =
        new(StringComparer.Ordinal);

    private readonly int _maxPerHour;
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Creates the limiter with the cap from <see cref="AuthOptions.RenewalPerIdentityPerHour"/>.</summary>
    /// <param name="options">The auth options (renewal cap).</param>
    /// <param name="timeProvider">System clock (tests substitute a fixed clock).</param>
    public RenewalRateLimiter(IOptions<AuthOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxPerHour = options.Value.RenewalPerIdentityPerHour;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>The configured per-identity hourly cap.</summary>
    public int MaxPerHour => _maxPerHour;

    /// <summary>
    /// Tries to record a renewal for <paramref name="identityId"/> within its rolling-hour budget.
    /// </summary>
    /// <param name="identityId">The identity attempting a renewal.</param>
    /// <returns>
    /// <c>true</c> if a slot was available (and now consumed); <c>false</c> if the identity already used
    /// its full hourly budget.
    /// </returns>
    public bool TryAcquire(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        var now = _timeProvider.GetUtcNow();
        var window = _events.GetOrAdd(identityId, static _ => new Queue<DateTimeOffset>());

        // Черга однієї identity серіалізується власним локом — паралельні identity не блокуються.
        lock (window)
        {
            // Відкидаємо позначки, що вийшли за межі ковзного вікна (година).
            while (window.Count > 0 && now - window.Peek() >= Window)
                window.Dequeue();

            if (window.Count >= _maxPerHour)
                return false;

            window.Enqueue(now);
            return true;
        }
    }
}
