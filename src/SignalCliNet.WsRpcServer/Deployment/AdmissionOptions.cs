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

    // ── Транспортні ліміти (task 3.2/3.3) — діють лише при Enabled=true ──────────────────────────
    // (auth-timeout НЕ тут: він у AuthOptions.AuthTimeoutSeconds — не дублюємо; див. docs cross-ref.)

    /// <summary>
    /// Maximum concurrent authenticated WebSocket sessions per identity (task 3.2, 1..256). Default 4.
    /// Enforced by <c>IdentityConnectionLimiter</c>; an over-cap session is closed <c>4429</c>
    /// (<c>too-many-connections</c>). Loopback (auth-disabled) sessions carry no identity and are exempt.
    /// </summary>
    [Range(1, 256)]
    public int MaxConnectionsPerIdentity { get; set; } = 4;

    /// <summary>
    /// Per-connection message-rate ceiling: accepted data frames per minute (token-bucket capacity =
    /// burst; task 3.2, 1..10000). Default 100. Exceeding it closes the socket <c>4429</c>
    /// (<c>message-rate:&lt;sec&gt;</c>). WS ping/pong frames are NOT counted (they never reach the
    /// data-frame path).
    /// </summary>
    [Range(1, 10000)]
    public int MessagesPerMinutePerConnection { get; set; } = 100;

    /// <summary>
    /// Idle timeout in minutes: a session silent (no accepted data frame) for this long is closed with a
    /// normal <c>1000</c> (<c>idle-timeout</c>; task 3.2, 1..1440). Default 30. This is NOT an auth event.
    /// </summary>
    [Range(1, 1440)]
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Maximum concurrent in-flight RPC dispatches per connection (D12, task 3.3, 1..256). Default 8.
    /// Exceeding it refuses the request immediately with <c>-32005</c> (<c>retry_after=1</c>) — no queue.
    /// </summary>
    [Range(1, 256)]
    public int MaxInFlightPerConnection { get; set; } = 8;

    /// <summary>
    /// Server-wide concurrency limit for signal-cli dispatch (G6, task 3.3, 1..64). Default 4. Acquired
    /// non-blocking by <c>SignalCliGate</c>; when full, the request is refused with <c>-32005</c>
    /// (<c>retry_after=1</c>) so the upstream pending queue never grows unbounded.
    /// </summary>
    [Range(1, 64)]
    public int SignalCliConcurrencyLimit { get; set; } = 4;
}
