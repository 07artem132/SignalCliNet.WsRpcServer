using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Options for the group-claim receive gate (config section <c>Server:GroupClaim</c>,
/// add-group-claim-receive). Like <see cref="AuthOptions"/>/<see cref="AdmissionOptions"/> the WHOLE
/// feature is opt-in: with <see cref="Enabled"/> = <c>false</c> (the default) the receive-mode stays
/// send-only (manual), the per-account router is passthrough, the scanner is idle and the send-gate is
/// off — current behaviour is unchanged verbatim (R3.1 auto-receive activates only when enabled).
/// </summary>
/// <remarks>
/// Межові правила — через <see cref="Range"/> (валідатор <c>GroupClaimOptionsValidator</c>,
/// source-gen <c>[OptionsValidator]</c>, як решта секцій). Немає крос-польових правил.
/// </remarks>
public sealed class GroupClaimOptions
{
    /// <summary>Configuration section name (<c>Server:GroupClaim</c>).</summary>
    public const string SectionName = "Server:GroupClaim";

    /// <summary>
    /// Whether the group-claim feature is enforced. Default <c>false</c> — continuous auto-receive, the
    /// scanner, the per-account router and the send-gate are all inert (staged rollout, like auth/admission).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>One-time claim-code TTL in minutes (1..1440). Default 10.</summary>
    [Range(1, 1440)]
    public int ClaimTtlMinutes { get; set; } = 10;

    /// <summary>Backstop binding TTL in hours — forces periodic re-claim (1..8760). Default 720 (30 days).</summary>
    [Range(1, 8760)]
    public int BindingTtlHours { get; set; } = 720;

    /// <summary>Per-identity cap on active (non-revoked, non-expired) group-bindings (1..1000). Default 20 (D2 footprint).</summary>
    [Range(1, 1000)]
    public int MaxActiveBindingsPerIdentity { get; set; } = 20;

    /// <summary>Per-identity <c>requestGroupClaim</c> rate cap per rolling hour (1..1000). Default 10.</summary>
    [Range(1, 1000)]
    public int ClaimRequestsPerHourPerIdentity { get; set; } = 10;

    /// <summary>Group member-list cache TTL in seconds (~60s per T2; 5..3600). Default 60.</summary>
    [Range(5, 3600)]
    public int MemberCacheTtlSeconds { get; set; } = 60;
}
