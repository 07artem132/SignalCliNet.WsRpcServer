using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Options for the token/PoP authentication handshake (config section <c>Server:Auth</c>). The whole
/// feature is opt-in: with <see cref="Enabled"/> = <c>false</c> (the default) the server keeps the
/// unchanged Phase-1 loopback full-access behaviour and none of these knobs take effect.
/// </summary>
/// <remarks>
/// Поетапний rollout (див. tasks.md): дефолт <see cref="Enabled"/>=<c>false</c> МУСИТЬ лишити всю
/// поточну поведінку незмінною. D4-посилення «non-loopback ⇒ Auth.Enabled» відкладено до
/// add-invites-admin — тут ми лише даємо opt-in-механізм.
/// </remarks>
public sealed class AuthOptions
{
    /// <summary>Configuration section name (<c>Server:Auth</c>).</summary>
    public const string SectionName = "Server:Auth";

    /// <summary>
    /// Whether token/PoP authentication is enforced. Default <c>false</c> — Phase-1 loopback
    /// full-access is preserved verbatim and the handshake seam is a no-op.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Access-token time-to-live in hours (1..168). Default 12.</summary>
    [Range(1, 168)]
    public int TokenTtlHours { get; set; } = 12;

    /// <summary>
    /// Seconds a browser session may stay unauthenticated (awaiting the fallback <c>authenticate</c>)
    /// before it is closed with <c>4408</c> (3..120). Default 10.
    /// </summary>
    [Range(3, 120)]
    public int AuthTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum concurrent unauthenticated sockets per remote IP (admission cap, 1..1024). Default 8.
    /// </summary>
    [Range(1, 1024)]
    public int MaxUnauthenticatedPerIp { get; set; } = 8;

    /// <summary>
    /// Exact-match Origin allowlist (defense-in-depth, NOT authentication; C3). Each entry MUST be an
    /// absolute origin <c>scheme://host[:port]</c> without a trailing '/'. An empty allowlist rejects
    /// every browser (Origin-bearing) request — fail-closed. Requests without an <c>Origin</c> header
    /// (non-browser clients) are not subject to this check.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Validates a single allowlist entry: a non-empty absolute origin <c>scheme://host[:port]</c>
    /// with no trailing '/', no path/query/fragment and no whitespace.
    /// </summary>
    /// <param name="origin">The candidate origin string.</param>
    /// <returns><c>true</c> if <paramref name="origin"/> is a well-formed origin; otherwise <c>false</c>.</returns>
    public static bool IsValidOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.EndsWith('/'))
            return false;

        // Позиційний парс scheme://authority — навмисно НЕ через System.Uri, бо кастомні схеми
        // (напр. chrome-extension://) нормалізуються ним непередбачувано. Origin = scheme "://" host[:port],
        // без шляху/пробілів.
        var separator = origin.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        var scheme = origin.AsSpan(0, separator);
        var authority = origin.AsSpan(separator + 3);

        if (authority.IsEmpty || authority.Contains('/') || authority.ContainsAny(' ', '\t'))
            return false;

        // Схема: [a-zA-Z][a-zA-Z0-9+.-]* (RFC 3986).
        if (!char.IsAsciiLetter(scheme[0]))
            return false;
        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '.' or '-'))
                return false;
        }

        return true;
    }
}
