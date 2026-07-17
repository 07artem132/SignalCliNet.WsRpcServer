using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// In-memory sliding-window rate limiter for <c>requestGroupClaim</c> (add-group-claim-receive, task 2.1):
/// at most <see cref="GroupClaimOptions.ClaimRequestsPerHourPerIdentity"/> claim requests per identity per
/// rolling hour. Bounds how fast an identity can mint claim codes (griefing-burn re-request budget).
/// </summary>
/// <remarks>
/// Singleton, лише в пам'яті процесу (рестарт скидає вікна — прийнятно: throttle, не безпекова гарантія).
/// Per-identity ізоляція під власним локом черги. Дзеркало <see cref="RenewalRateLimiter"/>.
/// </remarks>
public sealed class GroupClaimRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _events = new(StringComparer.Ordinal);
    private readonly int _maxPerHour;
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Creates the limiter with the cap from <see cref="GroupClaimOptions.ClaimRequestsPerHourPerIdentity"/>.</summary>
    /// <param name="options">The group-claim options (request cap).</param>
    /// <param name="timeProvider">System clock (tests substitute a fixed clock).</param>
    public GroupClaimRateLimiter(IOptions<GroupClaimOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxPerHour = options.Value.ClaimRequestsPerHourPerIdentity;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>The configured per-identity hourly cap.</summary>
    public int MaxPerHour => _maxPerHour;

    /// <summary>Tries to record a claim-request for <paramref name="identityId"/> within its rolling-hour budget.</summary>
    /// <param name="identityId">The identity requesting a claim.</param>
    /// <returns><c>true</c> if a slot was available (and now consumed); <c>false</c> if the budget is spent.</returns>
    public bool TryAcquire(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        var now = _timeProvider.GetUtcNow();
        var window = _events.GetOrAdd(identityId, static _ => new Queue<DateTimeOffset>());

        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() >= Window)
                window.Dequeue();

            if (window.Count >= _maxPerHour)
                return false;

            window.Enqueue(now);
            return true;
        }
    }
}
