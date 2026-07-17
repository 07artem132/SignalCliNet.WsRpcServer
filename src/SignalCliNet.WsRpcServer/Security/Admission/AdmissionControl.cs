using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>Why a send was denied by <see cref="AdmissionControl"/> (<see cref="AdmissionDenyReason.None"/> = admitted).</summary>
public enum AdmissionDenyReason
{
    /// <summary>Not denied.</summary>
    None,

    /// <summary>The global pause gate is engaged (W16 first check).</summary>
    GlobalPause,

    /// <summary>The identity is over its per-user fair-share under the aggregate (G2).</summary>
    UserQuota,

    /// <summary>The identity exceeded its new-recipient window (D2).</summary>
    NewRecipientWindow,

    /// <summary>The aggregate send budget for the window is exhausted (W13).</summary>
    AggregateBudget,
}

/// <summary>
/// The outcome of an admission decision. <paramref name="RetryAfterSeconds"/> carries the S9 in-band
/// retry hint (seconds until the relevant window frees up); it is <c>0</c> when admitted.
/// </summary>
public sealed record AdmissionDecision(bool Admitted, AdmissionDenyReason Reason, int RetryAfterSeconds)
{
    /// <summary>The canonical "admitted, no wait" decision.</summary>
    public static AdmissionDecision Allow { get; } = new(true, AdmissionDenyReason.None, 0);
}

/// <summary>
/// The single ordered admission function on the send chokepoint (W16). Runs the four limiters in a fixed
/// order with short-circuit on the first deny:
/// <c>global_pause → per-user quota → new-recipient window → aggregate budget</c>. The limiters do NOT
/// decide independently — this facade is the sole caller.
/// </summary>
/// <remarks>
/// <para>
/// With <see cref="AdmissionOptions.Enabled"/> = <c>false</c> every send is admitted with NO side effects
/// (no counter increments, no durable writes) — the staged-rollout no-op.
/// </para>
/// <para>
/// <b>Side-effect ordering (W16).</b> The global-pause check runs before any limiter mutates state, so a
/// paused server denies with zero side effects. Beyond that, each gate applies its own side effect only
/// when it admits, and the chain stops at the first deny — a later deny does not roll back an earlier
/// gate's increment (a known, conservative over-count that only ever counts against the sender).
/// </para>
/// </remarks>
public sealed class AdmissionControl
{
    private readonly IGlobalPauseGate _pauseGate;
    private readonly PerUserQuota _quota;
    private readonly NewRecipientTracker _recipients;
    private readonly ReserveThenSendBudget _budget;
    private readonly bool _enabled;
    private readonly ILogger<AdmissionControl> _logger;

    /// <summary>Creates the admission facade from its four limiters and validated options.</summary>
    public AdmissionControl(
        IGlobalPauseGate pauseGate,
        PerUserQuota quota,
        NewRecipientTracker recipients,
        ReserveThenSendBudget budget,
        IOptions<AdmissionOptions> options,
        ILogger<AdmissionControl> logger)
    {
        _pauseGate = pauseGate ?? throw new ArgumentNullException(nameof(pauseGate));
        _quota = quota ?? throw new ArgumentNullException(nameof(quota));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        ArgumentNullException.ThrowIfNull(options);
        _enabled = options.Value.Enabled;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Decides whether a send from <paramref name="identityId"/> to <paramref name="recipients"/> is
    /// admitted, running the W16 chain with short-circuit on the first deny.
    /// </summary>
    /// <param name="identityId">The sending identity.</param>
    /// <param name="recipients">The send's recipients (raw identifiers — never logged/stored here).</param>
    /// <returns>The admission decision (with an in-band retry hint on deny).</returns>
    public AdmissionDecision Admit(string identityId, IReadOnlyList<string> recipients)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentNullException.ThrowIfNull(recipients);

        // Вимкнено — admit без побічних ефектів (поетапний rollout).
        if (!_enabled)
            return AdmissionDecision.Allow;

        // 1. Global-pause — ДО будь-яких side-effect (W16): паузнутий сервер деніїть без декрементів.
        if (_pauseGate.IsPaused)
            return Deny(AdmissionDenyReason.GlobalPause, _budget.RetryAfterSeconds());

        // 2. Per-user floor / fair-share (G2).
        if (!_quota.TryAdmit(identityId))
            return Deny(AdmissionDenyReason.UserQuota, _quota.RetryAfterSeconds(identityId));

        // 3. New-recipient window (D2).
        if (!_recipients.TryAdmitRecipients(identityId, recipients))
            return Deny(AdmissionDenyReason.NewRecipientWindow, _recipients.RetryAfterSeconds(identityId));

        // 4. Аггрегатний reserve-then-send бюджет (W13).
        if (!_budget.TryReserveUnit())
            return Deny(AdmissionDenyReason.AggregateBudget, _budget.RetryAfterSeconds());

        return AdmissionDecision.Allow;
    }

    private AdmissionDecision Deny(AdmissionDenyReason reason, int retryAfterSeconds)
    {
        // Логуємо лише причину/identity/retry — БЕЗ отримувачів і тіла (privacy-контракт).
        _logger.LogInformation(
            "Admission deny: {Reason}; retry_after={RetryAfter}s.", reason, retryAfterSeconds);
        return new AdmissionDecision(false, reason, retryAfterSeconds);
    }
}
