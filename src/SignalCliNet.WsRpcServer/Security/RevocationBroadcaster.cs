using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// In-process fan-out that tears down live connections when an identity is revoked (task 1.4). Sessions
/// authenticated by a token subscribe with their identity id; <see cref="Publish"/> (called from
/// <c>TokenService.RevokeIdentity</c>) invokes every subscriber so an ongoing socket is closed
/// immediately rather than surviving until its next dispatch.
/// </summary>
/// <remarks>
/// Singleton. Підписки — по identityId; кожна повертає <see cref="IDisposable"/> для відписки у
/// teardown. Виклик підписників обгорнутий у broad-catch (це межа фан-ауту, конвенція дозволяє): збій
/// одного підписника не має зривати розсилку іншим.
/// </remarks>
public sealed class RevocationBroadcaster
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action>> _subscribers =
        new(StringComparer.Ordinal);

    private readonly ILogger<RevocationBroadcaster> _logger;

    /// <summary>Creates the broadcaster.</summary>
    public RevocationBroadcaster(ILogger<RevocationBroadcaster> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Subscribes <paramref name="onRevoked"/> to revocations of <paramref name="identityId"/>.
    /// </summary>
    /// <param name="identityId">The identity to watch.</param>
    /// <param name="onRevoked">Callback invoked (once) when the identity is revoked.</param>
    /// <returns>A handle whose disposal unsubscribes (idempotent).</returns>
    public IDisposable Subscribe(string identityId, Action onRevoked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentNullException.ThrowIfNull(onRevoked);

        var map = _subscribers.GetOrAdd(identityId, static _ => new ConcurrentDictionary<Guid, Action>());
        var key = Guid.NewGuid();
        map[key] = onRevoked;
        return new Subscription(this, identityId, key);
    }

    /// <summary>
    /// Notifies every subscriber of <paramref name="identityId"/> that it was revoked.
    /// </summary>
    /// <param name="identityId">The revoked identity.</param>
    public void Publish(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        if (!_subscribers.TryGetValue(identityId, out var map))
            return;

        foreach (var callback in map.Values)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                // Межа фан-ауту: логуємо й продовжуємо, щоб один збійний підписник не блокував решту.
                _logger.LogError(ex, "Помилка в revoke-підписнику identity {IdentityId}.", identityId);
            }
        }
    }

    private void Unsubscribe(string identityId, Guid key)
    {
        if (_subscribers.TryGetValue(identityId, out var map))
            map.TryRemove(key, out _);
    }

    private sealed class Subscription(RevocationBroadcaster owner, string identityId, Guid key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(identityId, key);
        }
    }
}
