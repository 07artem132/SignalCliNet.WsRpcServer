using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Wire format and at-rest hashing for opaque access tokens:
/// <c>ptzh_&lt;pepperVersion&gt;_&lt;payload&gt;_&lt;checksum&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The token is a server-issued bearer with no embedded claims — the durable store is the single
/// source of truth. Segments:
/// </para>
/// <list type="bullet">
/// <item><c>pepperVersion</c> — a decimal integer ≥ 1; a version-hint that yields a single lookup
/// after a pepper rotation (D10).</item>
/// <item><c>payload</c> — 32 CSPRNG bytes (256-bit entropy) encoded as base64url without padding
/// (43 chars).</item>
/// <item><c>checksum</c> — CRC32 of the decoded payload, big-endian, base64url without padding
/// (6 chars). Its ONLY role is a fast-reject of malformed tokens without touching the store or
/// computing an HMAC; it is NOT a security mechanism.</item>
/// </list>
/// <para>
/// At-rest hash = <c>HMAC-SHA256(decoded payload, pepper[version])</c>, base64. The canonical HMAC
/// pre-image is EXCLUSIVELY the decoded random payload (W18) — never the prefix, version or checksum.
/// The security model is the 256-bit entropy of the payload; there is deliberately NO constant-time
/// promise on the index lookup of the hash (W19 — honest model, not a timing-hardened compare).
/// The plaintext token value MUST NOT be logged.
/// </para>
/// </remarks>
public static class TokenFormat
{
    /// <summary>The fixed token prefix (protocol tag) — also the subprotocol marker on the handshake.</summary>
    public const string Prefix = "ptzh_";

    /// <summary>Decoded random payload length in bytes (256-bit entropy).</summary>
    private const int PayloadBytes = 32;

    /// <summary>Decoded checksum length in bytes (CRC32).</summary>
    private const int ChecksumBytes = 4;

    /// <summary>base64url-without-padding length of a 32-byte payload.</summary>
    private const int PayloadBase64Length = 43;

    /// <summary>base64url-without-padding length of a 4-byte checksum.</summary>
    private const int ChecksumBase64Length = 6;

    /// <summary>
    /// Creates a fresh opaque token for <paramref name="pepperVersion"/> and returns both the wire
    /// string and its decoded random payload (the sole HMAC pre-image).
    /// </summary>
    /// <param name="pepperVersion">The pepper version to embed as a version-hint (≥ 1).</param>
    /// <param name="payload">The 32-byte CSPRNG payload backing this token.</param>
    /// <returns>The wire-format token string.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pepperVersion"/> is below 1.</exception>
    public static string Create(int pepperVersion, out byte[] payload)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pepperVersion, 1);

        payload = RandomNumberGenerator.GetBytes(PayloadBytes);

        Span<byte> checksumBytes = stackalloc byte[ChecksumBytes];
        BinaryPrimitives.WriteUInt32BigEndian(checksumBytes, Crc32.HashToUInt32(payload));

        var payloadSegment = Base64Url.EncodeToString(payload);
        var checksumSegment = Base64Url.EncodeToString(checksumBytes);

        return string.Concat(
            Prefix,
            pepperVersion.ToString(CultureInfo.InvariantCulture),
            "_", payloadSegment,
            "_", checksumSegment);
    }

    /// <summary>
    /// Parses and validates a wire-format token: prefix, structure, version and checksum. Rejects
    /// malformed input without touching the store or computing an HMAC (fast-reject).
    /// </summary>
    /// <param name="token">The candidate token string.</param>
    /// <param name="pepperVersion">The parsed pepper version (0 on failure).</param>
    /// <param name="payload">The decoded 32-byte payload (empty on failure).</param>
    /// <returns><c>true</c> if the token is well-formed and its checksum matches; otherwise <c>false</c>.</returns>
    public static bool TryParse(string? token, out int pepperVersion, out byte[] payload)
    {
        pepperVersion = 0;
        payload = [];

        if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        // Версія пеппера — десяткові цифри до наступного '_'.
        var versionEnd = token.IndexOf('_', Prefix.Length);
        if (versionEnd <= Prefix.Length)
            return false;

        var versionSpan = token.AsSpan(Prefix.Length, versionEnd - Prefix.Length);
        if (!int.TryParse(versionSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            return false;

        // Решта — <payload(43)> '_' <checksum(6)>. base64url-алфавіт містить '_', тож парсимо
        // позиційно за фіксованими довжинами, а не split-ом по роздільнику.
        var rest = token.AsSpan(versionEnd + 1);
        if (rest.Length != PayloadBase64Length + 1 + ChecksumBase64Length || rest[PayloadBase64Length] != '_')
            return false;

        var payloadSpan = rest[..PayloadBase64Length];
        var checksumSpan = rest.Slice(PayloadBase64Length + 1, ChecksumBase64Length);

        Span<byte> payloadBytes = stackalloc byte[PayloadBytes];
        if (!Base64Url.TryDecodeFromChars(payloadSpan, payloadBytes, out var payloadWritten) || payloadWritten != PayloadBytes)
            return false;

        Span<byte> checksumBytes = stackalloc byte[ChecksumBytes];
        if (!Base64Url.TryDecodeFromChars(checksumSpan, checksumBytes, out var checksumWritten) || checksumWritten != ChecksumBytes)
            return false;

        // Checksum — лише fast-reject малформлених токенів (НЕ безпековий механізм).
        if (Crc32.HashToUInt32(payloadBytes) != BinaryPrimitives.ReadUInt32BigEndian(checksumBytes))
            return false;

        pepperVersion = version;
        payload = payloadBytes.ToArray();
        return true;
    }

    /// <summary>
    /// Computes the at-rest hash <c>HMAC-SHA256(payload, pepper)</c> as base64. The pre-image is
    /// EXCLUSIVELY the decoded random <paramref name="payload"/> (W18 canon).
    /// </summary>
    /// <param name="payload">The decoded 32-byte token payload (HMAC message).</param>
    /// <param name="pepper">The pepper for the token's version (HMAC key). Never logged.</param>
    /// <returns>The base64-encoded HMAC-SHA256.</returns>
    public static string ComputeHash(byte[] payload, byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(pepper);

        // Ключ = pepper, повідомлення = decoded payload (єдиний pre-image, W18).
        return Convert.ToBase64String(HMACSHA256.HashData(pepper, payload));
    }
}
