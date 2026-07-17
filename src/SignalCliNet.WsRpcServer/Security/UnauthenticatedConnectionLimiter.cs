using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Per-IP admission cap on concurrent <b>unauthenticated</b> WebSocket sockets (tasks 2.4/2.6). A socket
/// acquires a slot the moment it upgrades without a proven identity — whether it is waiting for the
/// fallback <c>authenticate</c> or is doomed to a <c>4401</c> — and releases it on activation or close.
/// This bounds how many half-open sockets a single source can pin.
/// </summary>
/// <remarks>
/// Singleton на весь процес; лічильники — по remote-IP. Cap читається з <see cref="AuthOptions"/> один
/// раз (незмінний на час життя процесу). Порожні записи прибираються при звільненні, щоб словник не ріс
/// на кожен IP назавжди.
/// </remarks>
public sealed class UnauthenticatedConnectionLimiter
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly int _max;

    /// <summary>Creates the limiter with the cap from <see cref="AuthOptions.MaxUnauthenticatedPerIp"/>.</summary>
    public UnauthenticatedConnectionLimiter(IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _max = options.Value.MaxUnauthenticatedPerIp;
    }

    /// <summary>The configured per-IP cap.</summary>
    public int Max => _max;

    /// <summary>
    /// Tries to reserve an unauthenticated slot for <paramref name="ip"/>.
    /// </summary>
    /// <param name="ip">The remote IP (string form).</param>
    /// <returns><c>true</c> if a slot was reserved; <c>false</c> if the per-IP cap is already reached.</returns>
    public bool TryAcquire(string ip)
    {
        ArgumentException.ThrowIfNullOrEmpty(ip);

        // Lock-free CAS-цикл: або ключ уже є (інкремент через TryUpdate), або його нема (TryAdd 1).
        // Cap перевіряється до інкременту; при contention — ретрай.
        while (true)
        {
            if (_counts.TryGetValue(ip, out var current))
            {
                if (current >= _max)
                    return false;
                if (_counts.TryUpdate(ip, current + 1, current))
                    return true;
            }
            else
            {
                if (_max < 1)
                    return false;
                if (_counts.TryAdd(ip, 1))
                    return true;
            }
        }
    }

    /// <summary>
    /// Releases a previously acquired slot for <paramref name="ip"/>. Safe to call for an unknown IP
    /// (no-op); callers must ensure exactly one release per successful acquire.
    /// </summary>
    /// <param name="ip">The remote IP (string form).</param>
    public void Release(string ip)
    {
        ArgumentException.ThrowIfNullOrEmpty(ip);

        while (true)
        {
            if (!_counts.TryGetValue(ip, out var current))
                return;

            if (current <= 1)
            {
                // Атомарне видалення лише якщо значення все ще == current (KVP-варіант Remove).
                if (((ICollection<KeyValuePair<string, int>>)_counts).Remove(
                        new KeyValuePair<string, int>(ip, current)))
                    return;
            }
            else if (_counts.TryUpdate(ip, current - 1, current))
            {
                return;
            }
        }
    }
}
