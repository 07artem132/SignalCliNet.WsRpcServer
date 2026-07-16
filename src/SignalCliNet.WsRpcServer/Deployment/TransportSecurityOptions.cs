using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Startup security options for the listening socket — the inputs to the D4 fail-closed
/// assertion (a non-loopback bind is refused unless it is a conscious, TLS-fronted deployment).
/// </summary>
/// <remarks>
/// Фаза 1 не має автентифікації, тож не-loopback bind сам по собі — відкрите Signal-реле.
/// D4 (fail-closed): <c>Host != loopback</c> дозволений ЛИШЕ якщо оператор свідомо виставив
/// <see cref="AllowNonLoopback"/> = <c>true</c> ТА підтвердив, що TLS сконфігуровано перед
/// сервером (<see cref="TlsOptions.Enabled"/> = <c>true</c> — термінація у reverse-proxy, R3.7/D3).
/// Валідується на старті (<c>ValidateOnStart</c> + крос-польове правило).
/// </remarks>
public sealed class TransportSecurityOptions
{
    /// <summary>Configuration section name (<c>Server</c>).</summary>
    public const string SectionName = "Server";

    /// <summary>Listen address (must be a valid IP; loopback by default in Phase 1).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// Conscious opt-in to bind a non-loopback address. Default <c>false</c> — the server refuses
    /// to start on a non-loopback address without this flag (D4). Requires <see cref="TlsOptions.Enabled"/>.
    /// </summary>
    public bool AllowNonLoopback { get; set; }

    /// <summary>Transport-security posture asserted by the operator (TLS termination in front of the server).</summary>
    public TlsOptions Tls { get; set; } = new();
}

/// <summary>
/// TLS posture for the deployment. In Phase 1 TLS is terminated by a reverse proxy (R3.7/D3);
/// <see cref="Enabled"/> is the operator's assertion that such termination is in place, and is a
/// precondition for a non-loopback bind (D4).
/// </summary>
public sealed class TlsOptions
{
    /// <summary>Whether TLS is configured in front of the server (reverse-proxy termination).</summary>
    public bool Enabled { get; set; }
}
