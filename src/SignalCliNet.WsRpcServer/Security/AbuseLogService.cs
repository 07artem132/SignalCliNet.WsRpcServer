using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Records shared-bot abuse events to the durable abuse-log (add-invites-admin, task 3.3). Only a
/// domain-separated HMAC of the subject is stored — never the raw subject/number (privacy, CLAUDE rule #4).
/// </summary>
/// <remarks>
/// <b>W24 (equality-oracle).</b> Pre-image містить per-record CSPRNG-<c>salt</c> І
/// <c>callerIdentityId</c>, тож той самий subject, залогований різними записами (або різними
/// caller'ами), дає РІЗНІ hmac — спостерігач лог-таблиці не може звірити рівність суб'єктів між
/// записами. HMAC-ключ — домен-розділений <c>pepper_abuse</c> (той самий домен «abuse», що й
/// new-recipient tracker; окремий від token-пеппера).
///
/// <b>Residual (не e2e-tamper-proof).</b> Цей лог фіксує подію, але сам по собі НЕ tamper-evident:
/// без hash-chain адмін-БД може бути відредагована без сліду. Tamper-evident audit-trail із ланцюгом
/// хешів — окрема робота (audit-trail, M8; секція 2). Див. <c>docs/shared-bot.md</c>.
///
/// Singleton; <see cref="TimeProvider"/> інжектимо для тестабельності.
/// </remarks>
public sealed class AbuseLogService
{
    // Per-record сіль — 16 CSPRNG-байтів (128-bit): унікалізує pre-image кожного запису (W24).
    private const int SaltBytes = 16;

    // Роздільник компонентів pre-image: NUL канонічно розмежовує salt/caller/subject (жоден компонент
    // його не містить — base64-сіль, opaque-identity, base64-хеш), тож (a‖b) не сплутати з (a'‖b').
    private const byte FieldSeparator = 0x00;

    private readonly IDurableStore _store;
    private readonly IAbusePepperProvider _pepperProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AbuseLogService> _logger;

    /// <summary>Creates the abuse-log service.</summary>
    /// <param name="store">The durable store (abuse_log table).</param>
    /// <param name="pepperProvider">Supplies the domain-separated <c>pepper_abuse</c> HMAC key.</param>
    /// <param name="timeProvider">System clock (tests substitute a fixed clock).</param>
    /// <param name="logger">Logger (never receives the raw subject).</param>
    public AbuseLogService(
        IDurableStore store,
        IAbusePepperProvider pepperProvider,
        TimeProvider timeProvider,
        ILogger<AbuseLogService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pepperProvider = pepperProvider ?? throw new ArgumentNullException(nameof(pepperProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records an abuse event: hashes <paramref name="subject"/> under a fresh per-record salt bound to
    /// <paramref name="callerIdentityId"/> (W24) and appends it to the durable abuse-log.
    /// </summary>
    /// <param name="eventType">Machine-readable event tag (e.g. <c>invite_redeemed</c>) — no PII.</param>
    /// <param name="callerIdentityId">The acting identity id (part of the HMAC pre-image; never a number).</param>
    /// <param name="subject">The sensitive subject (e.g. code hash) — hashed, NEVER stored/logged raw.</param>
    public void Record(string eventType, string callerIdentityId, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var saltBase64 = Convert.ToBase64String(salt);

        // pre-image = salt ‖ callerIdentityId ‖ subject (NUL-розділені). HMAC ключ — pepper_abuse.
        var preImage = BuildPreImage(saltBase64, callerIdentityId, subject);
        var hmac = HMACSHA256.HashData(_pepperProvider.GetPepper(), preImage);
        var subjectHmac = Convert.ToBase64String(hmac);

        _store.AppendAbuseLog(new AbuseLogRecord(eventType, subjectHmac, saltBase64, _timeProvider.GetUtcNow()));

        // НЕ логуємо сирий subject — лише тип події й актора (id, не номер).
        _logger.LogInformation(
            "Abuse-log: подія {EventType} (актор {Caller}).", eventType, callerIdentityId);
    }

    // Складає NUL-розділений pre-image (salt‖caller‖subject) у UTF-8.
    private static byte[] BuildPreImage(string saltBase64, string callerIdentityId, string subject)
    {
        var salt = Encoding.UTF8.GetBytes(saltBase64);
        var caller = Encoding.UTF8.GetBytes(callerIdentityId);
        var subj = Encoding.UTF8.GetBytes(subject);

        var buffer = new byte[salt.Length + 1 + caller.Length + 1 + subj.Length];
        var offset = 0;
        salt.CopyTo(buffer, offset);
        offset += salt.Length;
        buffer[offset++] = FieldSeparator;
        caller.CopyTo(buffer, offset);
        offset += caller.Length;
        buffer[offset++] = FieldSeparator;
        subj.CopyTo(buffer, offset);
        return buffer;
    }
}
