using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Adapter for the onboarding RPC surface (add-invites-admin, task 1.3). <c>redeemInvite</c> consumes a
/// one-time invite (atomically, W20/G12), provisions a brand-new <c>role=user</c> identity, TOFU-enrolls
/// the caller's device key and returns that identity's first access token.
/// </summary>
/// <remarks>
/// Політика <c>IdentityOnboarding</c> (без токена, rate-limited) — редемпшн виконується ДО існування
/// будь-якого акаунта. Порядок: soft global rate-cap → атомарний consume інвайту → нова identity →
/// enroll (перший токен) → abuse-log. Anti-oracle: невідомий / прострочений / погашений / понад-cap код
/// повертає ІДЕНТИЧНУ помилку <c>-32006</c> — причина не розкривається.
///
/// Privacy: плейнтекст-код і плейнтекст-токен НІКОЛИ не логуються (лише факт редемпшну й нова identity).
/// Малформ device-ключа після успішного consume «спалює» інвайт (self-harm: щоб дійти сюди, треба вже
/// мати валідний код) — цим ми не розкриваємо стан інвайту й тримаємо consume атомарним.
/// </remarks>
public sealed class SignalOnboardingRpcAdapter(
    InviteService inviteService,
    InviteRedemptionRateLimiter rateLimiter,
    DeviceEnrollmentService enrollmentService,
    AbuseLogService abuseLog,
    AuditLogService auditLog,
    IDurableStore store,
    TimeProvider timeProvider,
    ILogger<SignalOnboardingRpcAdapter> logger)
    : ISignalOnboardingRpc
{
    /// <summary>
    /// JSON-RPC error code for an invalid <c>redeemInvite</c> code (unknown / expired / consumed /
    /// over-cap). Одна помилка на всі випадки — anti-oracle. <c>-32006</c> — вільний слот у
    /// implementation-defined band (сусіди: <c>-32004</c> link-session-invalid, <c>-32005</c>
    /// rate-limited); окремий від <c>-32004</c>, щоб клієнт відрізняв редемпшн від лінк-сесій.
    /// Задокументовано у <c>docs/self-host.md</c>.
    /// </summary>
    public const int InviteInvalidCode = -32006;

    // Generic, PII-free повідомлення на невалідний код (однакове для всіх причин).
    private const string InviteInvalidMessage = "Invite is invalid or has expired.";

    private readonly InviteService _inviteService =
        inviteService ?? throw new ArgumentNullException(nameof(inviteService));

    private readonly InviteRedemptionRateLimiter _rateLimiter =
        rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

    private readonly DeviceEnrollmentService _enrollmentService =
        enrollmentService ?? throw new ArgumentNullException(nameof(enrollmentService));

    private readonly AbuseLogService _abuseLog = abuseLog ?? throw new ArgumentNullException(nameof(abuseLog));

    private readonly AuditLogService _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));

    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<SignalOnboardingRpcAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<RedeemInviteResponse> RedeemInvite(
        string code, string devicePublicKey, CancellationToken cancellationToken = default)
    {
        // Валідація на межі RPC: порожній ключ → InvalidParams (як finishLink deviceName), НЕ InvocationError.
        if (string.IsNullOrWhiteSpace(devicePublicKey))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "devicePublicKey cannot be empty");

        // М'який глобальний rate-cap СПРОБ редемпшну (task 1.2): перевищення → детермінований -32005 з
        // in-band retry_after (S9), БЕЗ бану. Гейт до будь-якої роботи з інвайтом.
        if (!_rateLimiter.TryAcquire())
        {
            _logger.LogInformation("redeemInvite відхилено soft rate-cap'ом (глобальне вікно).");
            throw new RpcErrorException(
                (JsonRpcErrorCode)AdmissionErrorCodes.RateLimited,
                AdmissionErrorCodes.RateLimitedMessage,
                new RateLimitErrorData(_rateLimiter.RetryAfterSeconds()));
        }

        var now = _timeProvider.GetUtcNow();
        var newIdentityId = NewIdentityId();

        // Атомарний consume+attempt-cap (W20/G12) у сторі. Невдача (будь-яка причина) → однаковий -32006.
        var redemption = _inviteService.TryRedeem(code, newIdentityId, now);
        if (redemption is null)
            throw new RpcErrorException((JsonRpcErrorCode)InviteInvalidCode, InviteInvalidMessage);

        // Нова identity (role=user) — FK-передумова для device-секрету/токена.
        _store.UpsertIdentity(new IdentityRecord(newIdentityId, "user", [], now));

        string token;
        try
        {
            // TOFU-enroll device-ключа + перший токен. Малформ ключа → InvalidParams (інвайт уже consumed).
            (token, _) = _enrollmentService.Enroll(newIdentityId, devicePublicKey);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("redeemInvite: невалідний device-ключ (identity {Identity}).", newIdentityId);
            throw new RpcErrorException(
                JsonRpcErrorCode.InvalidParams, "devicePublicKey is not a valid P-256 SPKI key", ex);
        }

        // Audit shared-bot: подія invite_redeemed (subject = code_hash, caller = нова identity). W24-хешування
        // й приховування сирого субʼєкта — у AbuseLogService.
        _abuseLog.Record("invite_redeemed", newIdentityId, redemption.CodeHash);

        // Tamper-evident audit-trail (task 3.2): та сама подія у hash-chain лозі (лінк бот-девайсу через
        // onboarding = нова identity + enrolled device-ключ). Деталь — code_hash (ідентифікатор, не секрет).
        _auditLog.Append("invite_redeemed", newIdentityId, redemption.CodeHash);

        // НЕ логуємо ані код, ані токен — лише факт редемпшну й нову identity.
        _logger.LogInformation("redeemInvite: створено identity {Identity} (емітент {CreatedBy}).",
            newIdentityId, redemption.CreatedBy);

        return Task.FromResult(new RedeemInviteResponse(newIdentityId, token));
    }

    // Нова identity id — 128-bit CSPRNG у hex (opaque; ніколи не номер телефону).
    private static string NewIdentityId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
