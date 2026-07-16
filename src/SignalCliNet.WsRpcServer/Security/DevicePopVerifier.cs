using System.Security.Cryptography;
using System.Text;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Verifies a proof-of-possession signature produced by an enrolled device key. The client signs the
/// UTF-8 bytes of the concatenation <c>nonce + connId</c> with its ECDSA P-256 private key; the server
/// verifies with the enrolled SubjectPublicKeyInfo (SPKI) public key.
/// </summary>
/// <remarks>
/// <para>
/// Формат підпису — <b>IEEE P1363</b> (raw <c>r‖s</c>, 64 байти для P-256): це формат, який віддає
/// WebCrypto <c>ECDSA</c> (клієнт тримає приватний ключ в IndexedDB, C1). Тому верифікація явно вимагає
/// <see cref="DSASignatureFormat.IeeeP1363FixedFieldConcatenation"/>, а НЕ DER/ASN.1.
/// </para>
/// <para>
/// Pre-image = <c>nonce</c> (той самий base64url-рядок, як його надіслано в <c>pop.challenge</c>) ‖
/// <c>connId</c> (session Id). Прив'язка до connId робить підпис bound до з'єднання (G8): релей того ж
/// підпису на інший сокет ламається, бо в іншого сокета інший connId. Будь-який малформлений вхід
/// (не-base64 ключ/підпис, не-ECDSA ключ, крива не P-256 тощо) повертає <c>false</c> — метод НЕ кидає.
/// </para>
/// </remarks>
public static class DevicePopVerifier
{
    /// <summary>
    /// Verifies <paramref name="signatureBase64"/> over <c>UTF8(nonce + connId)</c> against the enrolled
    /// device public key. Never throws — any malformed input resolves to <c>false</c>.
    /// </summary>
    /// <param name="publicKeySpkiBase64">The enrolled device public key (base64 SPKI, P-256).</param>
    /// <param name="nonce">The challenge nonce, exactly as sent (base64url string).</param>
    /// <param name="connId">The connection id the challenge was bound to (session Id).</param>
    /// <param name="signatureBase64">The client signature (base64, IEEE P1363 raw r‖s).</param>
    /// <returns><c>true</c> if the signature is valid for this key and pre-image; otherwise <c>false</c>.</returns>
    public static bool Verify(string publicKeySpkiBase64, string nonce, string connId, string signatureBase64)
    {
        if (string.IsNullOrEmpty(publicKeySpkiBase64) || string.IsNullOrEmpty(signatureBase64) ||
            nonce is null || connId is null)
        {
            return false;
        }

        byte[] spki;
        byte[] signature;
        try
        {
            spki = Convert.FromBase64String(publicKeySpkiBase64);
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            // Малформлений base64 (ключ або підпис) → відмова без винятку.
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);

            var preImage = Encoding.UTF8.GetBytes(nonce + connId);
            return ecdsa.VerifyData(
                preImage,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            // Не-ECDSA/пошкоджений SPKI або невідповідна довжина підпису → відмова без винятку.
            return false;
        }
    }
}
