namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// The global admission kill-switch checked FIRST in the admission chain (W16): while paused, every send
/// is denied before any per-user / budget side effect runs. The real implementation
/// (<see cref="GlobalPauseGate"/>, add-ops-observability) reacts to upstream rate-limit/captcha (-5/-6).
/// </summary>
public interface IGlobalPauseGate
{
    /// <summary>Whether admission is globally paused right now (auto-resumes on the monotonic deadline).</summary>
    bool IsPaused { get; }

    /// <summary>
    /// Seconds until the current pause auto-resumes (in-band <c>retry_after</c> hint), or <c>0</c> when
    /// not paused. Always ≥ 1 while paused.
    /// </summary>
    int RetryAfterSeconds { get; }

    /// <summary>
    /// Engages (or extends) the global pause for at least <paramref name="minimumDuration"/>, tagged with a
    /// machine-readable, PII-free <paramref name="reason"/> (e.g. <c>rate-limit</c>, <c>captcha</c>).
    /// Called from the send fail-path catch-matrix on upstream <c>-5</c>/<c>-6</c>. No-op on the
    /// <see cref="NoopGlobalPauseGate"/>.
    /// </summary>
    /// <param name="minimumDuration">Baseline pause length; the gate may escalate/jitter on top.</param>
    /// <param name="reason">Machine-readable, PII-free pause reason.</param>
    void PauseFor(TimeSpan minimumDuration, string reason);
}

/// <summary>
/// No-op <see cref="IGlobalPauseGate"/> — never paused. Retained as a test/opt-out double; production DI
/// binds the real <see cref="GlobalPauseGate"/> (add-ops-observability).
/// </summary>
public sealed class NoopGlobalPauseGate : IGlobalPauseGate
{
    /// <inheritdoc />
    public bool IsPaused => false;

    /// <inheritdoc />
    public int RetryAfterSeconds => 0;

    /// <inheritdoc />
    public void PauseFor(TimeSpan minimumDuration, string reason)
    {
        // No-op: заглушка ніколи не паузиться.
    }
}
