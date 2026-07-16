using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Enrolls a device public key for an identity (TOFU under a one-time invite, D1) and issues that
/// identity's first access token. This is the enrollment <b>service</b>; the wire-level
/// <c>redeemInvite</c> RPC (plus the invites table and its atomic single-use consume, W20/G12) lives in
/// the <c>add-invites-admin</c> change, which calls into this service.
/// </summary>
/// <remarks>
/// TOFU/D1: якщо у identity вже є НЕзреволкнутий device-секрет — реєстрація відхиляється
/// (<see cref="InvalidOperationException"/>). Зміна ключа = новий інвайт, а не мовчазна заміна;
/// повторна реєстрація можлива лише коли попередній секрет revoked (re-enroll after loss). Плейнтекст
/// першого токена повертається викликачу рівно один раз (як у <see cref="TokenService.IssueToken"/>).
/// </remarks>
public sealed class DeviceEnrollmentService
{
    // OID кривої NIST P-256 (prime256v1 / secp256r1) — єдина дозволена крива device-ключа (ES256).
    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    private readonly IDurableStore _store;
    private readonly TokenService _tokenService;
    private readonly TimeProvider _timeProvider;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<DeviceEnrollmentService> _logger;

    /// <summary>Creates the enrollment service.</summary>
    /// <param name="store">The durable store (identities + device secrets).</param>
    /// <param name="tokenService">Issues the first access token on successful enrollment.</param>
    /// <param name="timeProvider">System clock (tests substitute a fixed clock).</param>
    /// <param name="authOptions">Auth options (token TTL).</param>
    /// <param name="logger">Logger (never receives the enrolled key material or plaintext token).</param>
    public DeviceEnrollmentService(
        IDurableStore store,
        TokenService tokenService,
        TimeProvider timeProvider,
        IOptions<AuthOptions> authOptions,
        ILogger<DeviceEnrollmentService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _authOptions = (authOptions ?? throw new ArgumentNullException(nameof(authOptions))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Enrolls <paramref name="devicePublicKeySpkiBase64"/> for <paramref name="identityId"/> and issues
    /// the identity's first access token.
    /// </summary>
    /// <param name="identityId">The identity being enrolled (MUST already exist).</param>
    /// <param name="devicePublicKeySpkiBase64">The device public key as base64 SPKI (ECDSA P-256).</param>
    /// <returns>The freshly issued plaintext token and its persisted record.</returns>
    /// <exception cref="ArgumentException">The public key is not valid base64 SPKI or not a P-256 key.</exception>
    /// <exception cref="InvalidOperationException">
    /// The identity does not exist, or it already has a non-revoked device secret (TOFU — re-enroll needs
    /// the old secret revoked first, i.e. a fresh invite).
    /// </exception>
    public (string Token, TokenRecord Record) Enroll(string identityId, string devicePublicKeySpkiBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePublicKeySpkiBase64);

        // Identity мусить існувати (enrollment прив'язується до наявної identity).
        var identity = _store.GetIdentity(identityId)
            ?? throw new InvalidOperationException(
                $"Identity '{identityId}' не існує — enrollment device-ключа неможливий.");

        // TOFU (D1): активний (НЕзреволкнутий) секрет блокує тиху заміну ключа. Re-enroll — лише після revoke.
        var existing = _store.GetDeviceSecret(identityId);
        if (existing is not null && !existing.IsRevoked)
        {
            throw new InvalidOperationException(
                $"Identity '{identityId}' уже має активний device-ключ — повторна реєстрація заборонена " +
                "(зміна ключа = новий інвайт, D1).");
        }

        // Валідація: ключ має бути валідним P-256 SPKI (невалідний/не-P256 → ArgumentException).
        ValidateP256PublicKey(devicePublicKeySpkiBase64);

        // Реєструємо/оновлюємо секрет (UpsertDeviceSecret покриває і re-enroll після revoke) + видаємо токен.
        _store.UpsertDeviceSecret(new DeviceSecretRecord(
            identity.Id, devicePublicKeySpkiBase64, "ES256", IsRevoked: false, _timeProvider.GetUtcNow()));

        var issued = _tokenService.IssueToken(identity.Id, TimeSpan.FromHours(_authOptions.TokenTtlHours));

        // НЕ логуємо ані ключ, ані plaintext-токен — лише факт enrollment.
        _logger.LogInformation("Enrolled device-ключ для identity {IdentityId}; видано перший токен.", identity.Id);
        return issued;
    }

    // Перевіряє, що вхід — валідний base64 SPKI ECDSA-ключа на кривій P-256. Інакше кидає ArgumentException.
    private static void ValidateP256PublicKey(string devicePublicKeySpkiBase64)
    {
        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(devicePublicKeySpkiBase64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                "Device-pubkey не є валідним base64.", nameof(devicePublicKeySpkiBase64), ex);
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);

            var curve = ecdsa.ExportParameters(includePrivateParameters: false).Curve;
            var oid = curve.IsNamed ? curve.Oid?.Value : null;

            // Приймаємо лише P-256: або збіг OID кривої, або (як запасний варіант на платформах, де
            // named-OID не заповнюється) розмір ключа 256 біт — серед кривих WebCrypto це однозначно P-256.
            var isP256 = string.Equals(oid, NistP256Oid, StringComparison.Ordinal) || ecdsa.KeySize == 256;
            if (!isP256)
            {
                throw new ArgumentException(
                    "Device-pubkey має бути ECDSA-ключем на кривій P-256 (ES256).",
                    nameof(devicePublicKeySpkiBase64));
            }
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException(
                "Device-pubkey не є валідним SPKI-ключем ECDSA.", nameof(devicePublicKeySpkiBase64), ex);
        }
    }
}
