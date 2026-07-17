using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// A single one-time link session: the opaque <paramref name="SessionId"/>, the identity that created
/// it, an optional target-account hint, the (sensitive) device-link URI it stands for, and its expiry.
/// </summary>
/// <remarks>
/// <paramref name="DeviceLinkUri"/> — ЧУТЛИВЕ (сирий device-link URI / QR): НІКОЛИ не логувати (task 6).
/// Запис існує лише в пам'яті стору й не серіалізується на дріт (у serializer-контексті його немає).
/// </remarks>
/// <param name="SessionId">Opaque 256-bit CSPRNG session id (base64url).</param>
/// <param name="CreatedByIdentityId">The identity that started the link (owner of the session).</param>
/// <param name="TargetAccount">Optional target-account hint supplied to <c>startLink</c> (may be null).</param>
/// <param name="DeviceLinkUri">The signal-cli device-link URI this session stands for (sensitive).</param>
/// <param name="ExpiresAt">Absolute expiry (created + TTL).</param>
public sealed record LinkSession(
    string SessionId,
    string CreatedByIdentityId,
    string? TargetAccount,
    string DeviceLinkUri,
    DateTimeOffset ExpiresAt);

/// <summary>
/// In-memory, one-time link-session store (add-link-sessions). Maps a 256-bit CSPRNG
/// <c>SessionId → (creator identity, target-account hint, device-link URI, expiry)</c> and gates the
/// device-linking flow: <c>startLink</c> mints a session, <c>finishLink</c> conditionally consumes it.
/// </summary>
/// <remarks>
/// Анти-griefing: <see cref="TryConsume"/> видаляє сесію ЛИШЕ якщо identity того, хто завершує лінк,
/// збігається з identity ініціатора; інакше сесія лишається живою до TTL, а спроба логується (без повного
/// sessionId — лише префікс). One-time: успішне гашення видаляє запис, тож повторний <c>finishLink</c> тим
/// самим id відмовляє.
///
/// TTL 120с (W9): свідомо НЕ звужуємо під глобальний 30с RPC-таймаут send-шляху — ручний QR-скан не
/// встигає за 30с. Прибирання прострочених — ліниве (при кожному <see cref="Create"/>/<see cref="TryConsume"/>),
/// без фонового таймера.
///
/// Rate-limit 3/хв/identity (монотонне ковзне вікно, патерн <see cref="RenewalRateLimiter"/>): захищає від
/// флуду стартів лінкування однією identity. Перевищення → <see cref="InvalidOperationException"/>.
///
/// Singleton, лише в пам'яті процесу (рестарт скидає сесії — прийнятно: сесії ефемерні, TTL 120с).
/// <see cref="TimeProvider"/> інжектимо для тестабельності (тести рухають час явно).
/// </remarks>
public sealed class LinkSessionStore
{
    /// <summary>TTL сесії лінкування — 120с (W9: НЕ звужувати під глобальний RPC-таймаут send-шляху).</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromSeconds(120);

    /// <summary>Максимум стартів лінкування на одну identity у ковзному вікні (rate-limit).</summary>
    public const int MaxStartsPerWindow = 3;

    /// <summary>Ковзне вікно rate-limit (1 хвилина).</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    // Ентропія SessionId — 256 біт (32 CSPRNG-байти); base64url без padding → 43 символи.
    private const int SessionIdBytes = 32;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LinkSessionStore> _logger;
    private readonly object _sync = new();

    // SessionId → сесія. Захищено _sync (усі операції серіалізуються — стор ефемерний і невеликий).
    private readonly Dictionary<string, LinkSession> _sessions = new(StringComparer.Ordinal);

    // identity → часи стартів у поточному вікні (rate-limit). Захищено тим самим _sync.
    private readonly Dictionary<string, Queue<DateTimeOffset>> _starts = new(StringComparer.Ordinal);

    /// <summary>Creates the store.</summary>
    /// <param name="timeProvider">System clock (tests substitute a fixed clock).</param>
    /// <param name="logger">Logger for anti-griefing warnings (never logs a full sessionId or a URI).</param>
    public LinkSessionStore(TimeProvider timeProvider, ILogger<LinkSessionStore> logger)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Mints a new one-time session for <paramref name="identityId"/> over <paramref name="deviceLinkUri"/>.
    /// </summary>
    /// <param name="identityId">The identity starting the link (session owner).</param>
    /// <param name="targetAccount">Optional target-account hint (may be null for a new own number).</param>
    /// <param name="deviceLinkUri">The upstream device-link URI (sensitive — never logged).</param>
    /// <returns>The created session (its <see cref="LinkSession.SessionId"/> is returned to the client).</returns>
    /// <exception cref="InvalidOperationException">
    /// The identity exceeded its start budget (<see cref="MaxStartsPerWindow"/> per <see cref="RateWindow"/>).
    /// </exception>
    public LinkSession Create(string identityId, string? targetAccount, string deviceLinkUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceLinkUri);

        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            PurgeExpired(now);

            if (!TryReserveStart(identityId, now))
            {
                // privacy: без деталей сесії/URI; клієнту адаптер віддасть generic rate-limit.
                throw new InvalidOperationException(
                    "Перевищено ліміт стартів лінкування (3/хв на identity) — спробуйте за хвилину.");
            }

            var sessionId = GenerateSessionId();
            var session = new LinkSession(sessionId, identityId, targetAccount, deviceLinkUri, now + SessionTtl);
            _sessions[sessionId] = session;
            return session;
        }
    }

    /// <summary>
    /// Atomically, one-time consumes the session <paramref name="sessionId"/> for
    /// <paramref name="callerIdentityId"/>.
    /// </summary>
    /// <remarks>
    /// Під одним локом: сесія існує + не прострочена + <see cref="LinkSession.CreatedByIdentityId"/> ==
    /// <paramref name="callerIdentityId"/> → видаляємо (one-time) і повертаємо. Чужа identity → НЕ
    /// видаляємо (анти-griefing) + warning, повертаємо <c>null</c>. Прострочена/невідома → <c>null</c>.
    /// Усі негативні гілки повертають <c>null</c> нерозрізнено (anti-oracle: адаптер віддає одну помилку).
    /// </remarks>
    /// <param name="sessionId">The client-supplied session id.</param>
    /// <param name="callerIdentityId">The identity completing the link.</param>
    /// <returns>The consumed session on success; otherwise <c>null</c>.</returns>
    public LinkSession? TryConsume(string sessionId, string callerIdentityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerIdentityId);
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            PurgeExpired(now);

            if (!_sessions.TryGetValue(sessionId, out var session))
                return null; // невідома (або вже прострочена/видалена)

            if (now >= session.ExpiresAt)
            {
                _sessions.Remove(sessionId); // прострочена → видалити
                return null;
            }

            if (!string.Equals(session.CreatedByIdentityId, callerIdentityId, StringComparison.Ordinal))
            {
                // Анти-griefing: чужа identity НЕ гасить сесію. Логуємо лише префікс sessionId (не повний).
                _logger.LogWarning(
                    "Анти-griefing: finishLink чужою identity на link-сесію {SessionPrefix}… — відмова; сесію збережено.",
                    Preview(sessionId));
                return null;
            }

            // Успіх: one-time — видаляємо і повертаємо власникові.
            _sessions.Remove(sessionId);
            return session;
        }
    }

    // Ліниве прибирання прострочених (без фонового таймера): викликається лише вже під _sync.
    private void PurgeExpired(DateTimeOffset now)
    {
        if (_sessions.Count == 0)
            return;

        List<string>? expired = null;
        foreach (var (id, session) in _sessions)
        {
            if (now >= session.ExpiresAt)
                (expired ??= []).Add(id);
        }

        if (expired is null)
            return;

        foreach (var id in expired)
            _sessions.Remove(id);
    }

    // Монотонне ковзне вікно на identity (патерн RenewalRateLimiter): відкидаємо мітки, старші за вікно,
    // і резервуємо слот, якщо бюджет не вичерпано.
    private bool TryReserveStart(string identityId, DateTimeOffset now)
    {
        if (!_starts.TryGetValue(identityId, out var window))
        {
            window = new Queue<DateTimeOffset>();
            _starts[identityId] = window;
        }

        while (window.Count > 0 && now - window.Peek() >= RateWindow)
            window.Dequeue();

        if (window.Count >= MaxStartsPerWindow)
            return false;

        window.Enqueue(now);
        return true;
    }

    // 256-bit CSPRNG → base64url без padding (URL-safe, 43 символи). Enough ентропії проти вгадування.
    private static string GenerateSessionId()
    {
        Span<byte> bytes = stackalloc byte[SessionIdBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    // Перші 8 символів sessionId для діагностики (повний id — секрет, ніколи в лог повністю).
    private static string Preview(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];
}
