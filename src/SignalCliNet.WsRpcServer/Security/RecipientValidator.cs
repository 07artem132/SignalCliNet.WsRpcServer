using System.Text.RegularExpressions;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Validates and normalizes message recipients at the chokepoint, before dispatch (D11/S6): a
/// malformed recipient is rejected with <c>-32602</c> and never reaches signal-cli.
/// </summary>
/// <remarks>
/// Поточна RPC-поверхня (<c>sendTextMessage</c>) адресує лише user-recipient'ів (номер/UUID/username);
/// group-send ще не підключено, тож <see cref="IsValidGroupId"/> надано наперед для майбутнього
/// group-recipient шляху. Порожні/whitespace-елементи пропускаємо — їх і так відсіює адаптер; ми
/// відхиляємо саме НЕпорожні малформовані значення (щоб не зламати наявну поведінку фільтрації).
/// </remarks>
public static partial class RecipientValidator
{
    /// <summary>Upper bound on a single recipient identifier length (sanity bound).</summary>
    public const int MaxRecipientLength = 256;

    /// <summary>The classified shape of a user recipient.</summary>
    public enum RecipientKind
    {
        /// <summary>Not a recognizable recipient.</summary>
        Invalid,

        /// <summary>An E.164 phone number (<c>+</c> then country code and digits).</summary>
        Phone,

        /// <summary>A Signal account UUID (ACI).</summary>
        Uuid,

        /// <summary>A Signal username (letters/digits/underscore/dot).</summary>
        Username,
    }

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164Regex();

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex UuidRegex();

    // Username: 3..64 із [a-zA-Z0-9_.], мусить містити хоча б одну літеру (щоб суто-цифрове не пройшло).
    [GeneratedRegex(@"^(?=.*[a-zA-Z])[a-zA-Z0-9_.]{3,64}$")]
    private static partial Regex UsernameRegex();

    // Внутрішній group-ID Signal — base64 (мін. довжина, опційний '=' паддінг).
    [GeneratedRegex("^[A-Za-z0-9+/]{16,}={0,2}$")]
    private static partial Regex GroupIdRegex();

    /// <summary>
    /// Attempts to normalize a single user recipient. Returns <c>false</c> for a malformed value.
    /// </summary>
    /// <param name="raw">The raw recipient string.</param>
    /// <param name="normalized">The trimmed/canonical form on success; empty otherwise.</param>
    /// <param name="kind">The classified kind on success; <see cref="RecipientKind.Invalid"/> otherwise.</param>
    /// <returns><c>true</c> if the recipient is a recognizable user identifier.</returns>
    public static bool TryNormalizeUserRecipient(string? raw, out string normalized, out RecipientKind kind)
    {
        normalized = string.Empty;
        kind = RecipientKind.Invalid;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw.Trim();
        if (s.Length is 0 or > MaxRecipientLength)
            return false;

        // Пробіли/контрольні символи всередині ідентифікатора — завжди малформовано.
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
                return false;
        }

        if (s[0] == '+')
        {
            if (!E164Regex().IsMatch(s))
                return false; // «не-E.164»
            normalized = s;
            kind = RecipientKind.Phone;
            return true;
        }

        // Суто-цифрове без '+' — це схожий на номер, але малформований recipient.
        if (s.All(char.IsAsciiDigit))
            return false;

        if (UuidRegex().IsMatch(s))
        {
            normalized = s.ToLowerInvariant();
            kind = RecipientKind.Uuid;
            return true;
        }

        if (UsernameRegex().IsMatch(s))
        {
            normalized = s;
            kind = RecipientKind.Username;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Asserts that every non-empty recipient in <paramref name="recipients"/> is well-formed; throws
    /// <see cref="RpcErrorException"/> (<c>-32602</c>) at the first malformed one — before dispatch.
    /// </summary>
    /// <param name="recipients">The recipients supplied by the caller.</param>
    /// <exception cref="RpcErrorException">If any non-empty recipient is malformed.</exception>
    public static void AssertValidUserRecipients(IReadOnlyList<string> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        foreach (var r in recipients)
        {
            if (string.IsNullOrWhiteSpace(r))
                continue; // порожні відсіює адаптер — не валимо весь виклик через них

            if (!TryNormalizeUserRecipient(r, out _, out _))
                throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Malformed recipient.");
        }
    }

    /// <summary>
    /// Whether <paramref name="raw"/> is a well-formed Signal internal group id (base64). Provided for
    /// the future group-recipient path; the current send surface uses only user recipients.
    /// </summary>
    /// <param name="raw">The candidate group id.</param>
    /// <returns><c>true</c> if it looks like a valid group id.</returns>
    public static bool IsValidGroupId(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && GroupIdRegex().IsMatch(raw.Trim());
}
