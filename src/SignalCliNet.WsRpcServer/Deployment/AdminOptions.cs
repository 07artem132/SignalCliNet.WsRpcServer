using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Options for the mTLS admin channel (config section <c>Server:Admin</c>) and admin-invite defaults
/// (add-invites-admin, tasks 2.3/3.1). The admin port is a SEPARATE listener from the plain token port;
/// it authenticates by client certificate (the auto-provisioned admin bundle), never by token/PoP.
/// </summary>
/// <remarks>
/// Additive/opt-in, як auth/admission: за замовчуванням <see cref="Enabled"/> = <c>true</c>, але порт
/// стартує ЛИШЕ якщо на томі провіжнено секрети (server-cert + CA). Дефолтний bind — loopback (D4-логіка:
/// не-loopback лише зі свідомим <see cref="AllowNonLoopback"/>; TLS тут завжди є, бо mTLS).
/// </remarks>
public sealed class AdminOptions
{
    /// <summary>Configuration section name (<c>Server:Admin</c>).</summary>
    public const string SectionName = "Server:Admin";

    /// <summary>
    /// Whether the mTLS admin port is served. Default <c>true</c> — but the port still starts only when the
    /// deployment's secret material (server cert + CA) has been provisioned on the volume.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Admin listen address. Loopback by default (D4 — non-loopback needs the opt-in below).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Admin listen port (separate from the plain RPC port). Default <c>9443</c>.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 9443;

    /// <summary>
    /// Conscious opt-in to bind the admin port to a non-loopback address. Default <c>false</c>. The admin
    /// port is always mTLS (client cert required), so binding it publicly is defensible — but stays opt-in.
    /// </summary>
    public bool AllowNonLoopback { get; set; }

    /// <summary>Default invite TTL in seconds when <c>createInvite</c> omits it (60..2592000). Default 86400 (1 day).</summary>
    [Range(60, 2_592_000)]
    public int DefaultInviteTtlSeconds { get; set; } = 86_400;

    /// <summary>Default per-code attempt cap when <c>createInvite</c> omits it (1..1000). Default 5.</summary>
    [Range(1, 1000)]
    public int DefaultInviteMaxAttempts { get; set; } = 5;
}
