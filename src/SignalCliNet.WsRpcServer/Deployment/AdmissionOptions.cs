using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Options for the send-admission / anti-abuse limiters (config section <c>Server:Admission</c>). Like
/// <see cref="AuthOptions"/> the whole feature is opt-in: with <see cref="Enabled"/> = <c>false</c> (the
/// default) the admission facade short-circuits to "admit" and none of these knobs take effect — a
/// staged rollout that keeps current behaviour verbatim.
/// </summary>
/// <remarks>
/// <para>
/// Одне admission-вікно (<see cref="BudgetWindowMinutes"/>) спільне для всіх лімітерів: агрегатний
/// бюджет, per-user floor і new-recipient counter скидаються на цій самій монотонній каденції (D15).
/// </para>
/// <para>
/// Крос-правило (валідатор): <see cref="PerUserFloorPerWindow"/> ≤ <see cref="AggregateBudgetPerWindow"/>
/// (калібровка 1.1). Σ-floor УСІХ активних identity статично перевірити неможливо, тож floor гарантований
/// лише поки Σ(активних identity) × <see cref="PerUserFloorPerWindow"/> ≤ <see cref="AggregateBudgetPerWindow"/>;
/// понад це floor вироджується у fair-share (best-effort) — див. <c>PerUserQuota</c>.
/// </para>
/// </remarks>
public sealed class AdmissionOptions
{
    /// <summary>Configuration section name (<c>Server:Admission</c>).</summary>
    public const string SectionName = "Server:Admission";

    /// <summary>
    /// Whether send-admission is enforced. Default <c>false</c> — the facade admits every send with no
    /// side effects (staged rollout, like <see cref="AuthOptions.Enabled"/>).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Σ-ceiling of sends per window across all identities (aggregate budget, 1..100000). Default 300.</summary>
    [Range(1, 100000)]
    public int AggregateBudgetPerWindow { get; set; } = 300;

    /// <summary>The admission window length in minutes (1..1440). Default 60.</summary>
    [Range(1, 1440)]
    public int BudgetWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Guaranteed per-identity send floor within a window under the aggregate budget (G2, 0..10000).
    /// Default 10. MUST be ≤ <see cref="AggregateBudgetPerWindow"/> (cross-rule).
    /// </summary>
    [Range(0, 10000)]
    public int PerUserFloorPerWindow { get; set; } = 10;

    /// <summary>Maximum NEW (first-ever-contact) recipients per identity per window (D2, 1..1000). Default 5.</summary>
    [Range(1, 1000)]
    public int NewRecipientsPerWindow { get; set; } = 5;

    /// <summary>
    /// Durable block-reservation granularity K (W13): the aggregate budget is drawn down in blocks of
    /// this many units at a time (1..1000). Default 10. The effective ceiling rounds the aggregate up to
    /// a whole block, so keeping <see cref="AggregateBudgetPerWindow"/> a multiple of this value avoids
    /// granting up to K-1 units above the nominal aggregate.
    /// </summary>
    [Range(1, 1000)]
    public int ReserveBlockSize { get; set; } = 10;
}
