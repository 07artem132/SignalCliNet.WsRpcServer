using System.Net;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Pure, side-effect-free helpers for the D4 fail-closed startup assertion. Extracted so the
/// binding rule is unit-testable in isolation (mirrors the <c>Program.ApplySignalCliConfig</c>
/// testable-config-binding pattern) and reused by both the options validator and the hosted service.
/// </summary>
public static class StartupSecurityValidator
{
    /// <summary>
    /// Determines whether <paramref name="host"/> is a loopback bind. A non-parsable host or an
    /// "any" address (<c>0.0.0.0</c> / <c>::</c>) is treated as NON-loopback (fail-closed).
    /// </summary>
    /// <param name="host">The configured listen address.</param>
    /// <returns><c>true</c> only when <paramref name="host"/> is a valid loopback IP address.</returns>
    public static bool IsLoopbackBind(string? host)
    {
        // "localhost" не є IP, але семантично loopback; NetCoreServer усе одно вимагає IP-літерал,
        // тож тут приймаємо і його як зручність для тестів/докерів.
        if (string.Equals(host?.Trim(), "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Applies the D4 rule: a non-loopback bind is allowed ONLY when the operator has consciously
    /// opted in (<paramref name="allowNonLoopback"/>) AND asserted TLS is configured in front of
    /// the server (<paramref name="tlsConfigured"/>). Auth does not exist in Phase 1, so these two
    /// flags stand in for the "auth enabled" half of the D4 predicate.
    /// </summary>
    /// <param name="host">The configured listen address.</param>
    /// <param name="allowNonLoopback">Whether the operator opted in to a non-loopback bind.</param>
    /// <param name="tlsConfigured">Whether TLS is asserted to be configured (reverse-proxy termination).</param>
    /// <returns>A tuple: <c>Ok</c> is <c>true</c> when the bind is permitted; otherwise <c>Error</c> explains why not.</returns>
    public static (bool Ok, string? Error) ValidateBinding(string? host, bool allowNonLoopback, bool tlsConfigured)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (false, "Server:Host не задано.");

        if (IsLoopbackBind(host))
            return (true, null);

        // Не-loopback: обидві передумови обов'язкові (D4, fail-closed).
        if (!allowNonLoopback)
        {
            return (false,
                $"Не-loopback bind '{host}' заборонений у Фазі 1 без автентифікації. " +
                "Виставте Server:AllowNonLoopback=true ТА Server:Tls:Enabled=true лише за наявності " +
                "TLS-термінації і зовнішнього L4-фаєрволу (nftables/cloud SG) перед сервером.");
        }

        if (!tlsConfigured)
        {
            return (false,
                $"Не-loopback bind '{host}' вимагає сконфігурованого TLS (Server:Tls:Enabled=true) — " +
                "інакше це відкрите Signal-реле без шифрування транспорту (D4).");
        }

        return (true, null);
    }
}
