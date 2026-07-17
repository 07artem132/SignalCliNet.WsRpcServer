using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// Per-identity admission cap on concurrent <b>authenticated</b> WebSocket sessions (task 3.2). A
/// token-authenticated session takes a slot at activation and releases it on teardown; a session over
/// its identity's cap is refused with close <c>4429</c> (<c>too-many-connections</c>). Loopback
/// (auth-disabled) sessions are OUTSIDE this cap — they carry no identity.
/// </summary>
/// <remarks>
/// Патерн дзеркалить <see cref="UnauthenticatedConnectionLimiter"/> (per-IP), лише ключ — identityId.
/// Singleton на весь процес; cap читається з <see cref="AdmissionOptions"/> один раз (незмінний на час
/// життя процесу). Порожні записи прибираються при звільненні, щоб словник не ріс назавжди. Cap діє лише
/// коли admission увімкнено (сесія викликає limiter тільки при <c>AdmissionOptions.Enabled</c>).
/// </remarks>
public sealed class IdentityConnectionLimiter
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly int _max;

    /// <summary>Creates the limiter with the cap from <see cref="AdmissionOptions.MaxConnectionsPerIdentity"/>.</summary>
    public IdentityConnectionLimiter(IOptions<AdmissionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _max = options.Value.MaxConnectionsPerIdentity;
    }

    /// <summary>The configured per-identity cap.</summary>
    public int Max => _max;

    /// <summary>
    /// Tries to reserve a connection slot for <paramref name="identityId"/>.
    /// </summary>
    /// <param name="identityId">The authenticated identity.</param>
    /// <returns><c>true</c> if a slot was reserved; <c>false</c> if the per-identity cap is already reached.</returns>
    public bool TryAcquire(string identityId)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityId);

        // Lock-free CAS-цикл (як UnauthenticatedConnectionLimiter): або ключ уже є (інкремент через
        // TryUpdate), або його нема (TryAdd 1). Cap перевіряється до інкременту; при contention — ретрай.
        while (true)
        {
            if (_counts.TryGetValue(identityId, out var current))
            {
                if (current >= _max)
                    return false;
                if (_counts.TryUpdate(identityId, current + 1, current))
                    return true;
            }
            else
            {
                if (_max < 1)
                    return false;
                if (_counts.TryAdd(identityId, 1))
                    return true;
            }
        }
    }

    /// <summary>
    /// Releases a previously acquired slot for <paramref name="identityId"/>. Safe to call for an unknown
    /// identity (no-op); callers must ensure exactly one release per successful acquire.
    /// </summary>
    /// <param name="identityId">The authenticated identity.</param>
    public void Release(string identityId)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityId);

        while (true)
        {
            if (!_counts.TryGetValue(identityId, out var current))
                return;

            if (current <= 1)
            {
                // Атомарне видалення лише якщо значення все ще == current (KVP-варіант Remove).
                if (((ICollection<KeyValuePair<string, int>>)_counts).Remove(
                        new KeyValuePair<string, int>(identityId, current)))
                    return;
            }
            else if (_counts.TryUpdate(identityId, current - 1, current))
            {
                return;
            }
        }
    }
}
