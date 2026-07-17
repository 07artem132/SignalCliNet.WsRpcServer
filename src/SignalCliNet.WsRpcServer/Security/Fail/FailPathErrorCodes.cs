namespace SignalCliNet.WsRpcServer.Security.Fail;

/// <summary>
/// Application-level JSON-RPC error codes for the send fail-path (tasks 2.1/2.3/3.3). All sit in the
/// implementation-defined server-error band and are DISTINCT from the already-assigned codes so a client
/// can pattern-match the outcome without inspecting message text.
/// </summary>
/// <remarks>
/// Зайняті сусіди: <c>-32001</c> unauthorized/admin, <c>-32004</c> link-session-invalid, <c>-32005</c>
/// rate-limited (admission/pause), <c>-32006</c> invite-invalid, <c>-32007</c> group send-denied,
/// <c>-32008</c> group quota, <c>-32010</c> upgrade-required. Тут — три ще вільні слоти. Задокументовано
/// у <c>docs/ops-runbook.md</c>.
/// </remarks>
public static class FailPathErrorCodes
{
    /// <summary>
    /// W10: an in-flight send was cancelled by a signal-cli daemon restart (NOT the client) — a transient
    /// condition. The client SHOULD retry (idempotently, ideally with a <c>messageId</c>). The failed
    /// request's id correlates the response so the notification is never silently lost.
    /// </summary>
    public const int ServiceRestarting = -32009;

    /// <summary>Human-safe, PII-free client message for <see cref="ServiceRestarting"/>.</summary>
    public const string ServiceRestartingMessage = "Service is restarting; retry the request.";

    /// <summary>
    /// T6 / W13 at-most-once: an idempotent retry found its <c>messageId</c> in the <c>pending</c> state
    /// (the first attempt may or may not have reached Signal after a crash). The outcome is UNKNOWN; the
    /// server refuses to auto-resend (over-send → ban is the guarded property). The client MUST NOT retry
    /// this id blindly; it may reconcile out-of-band or accept the (rare) under-send.
    /// </summary>
    public const int OutcomeUnknown = -32011;

    /// <summary>Human-safe, PII-free client message for <see cref="OutcomeUnknown"/>.</summary>
    public const string OutcomeUnknownMessage =
        "Previous send with this messageId is unresolved; outcome unknown — do not blindly retry.";

    /// <summary>
    /// A send to a recipient whose Signal identity key is untrusted (upstream <c>-4</c>). Per-recipient and
    /// actionable (verify the safety number, then resend) — deliberately NOT a global pause (unlike -5/-6).
    /// </summary>
    public const int UntrustedIdentity = -32012;

    /// <summary>Human-safe, PII-free client message for <see cref="UntrustedIdentity"/> (no recipient echoed).</summary>
    public const string UntrustedIdentityMessage = "Recipient identity is not trusted; verify safety number and resend.";
}
