namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// The global admission kill-switch checked FIRST in the admission chain (W16): while paused, every send
/// is denied before any per-user / budget side effect runs.
/// </summary>
/// <remarks>
/// Заглушка секції 1.4 — інтерфейс + no-op реалізація. Реальну реакцію (пауза у відповідь на upstream
/// backpressure / сигнали -5/-6) дасть Phase-3 <c>add-ops-observability</c>: там зʼявиться реалізація,
/// яка перемикає <see cref="IsPaused"/> у <c>true</c>. Тут ми лише фіксуємо шов.
/// </remarks>
public interface IGlobalPauseGate
{
    /// <summary>Whether admission is globally paused right now.</summary>
    bool IsPaused { get; }
}

/// <summary>
/// No-op <see cref="IGlobalPauseGate"/> — never paused. The default binding until
/// <c>add-ops-observability</c> (Phase-3) supplies a real, signal-driven gate.
/// </summary>
public sealed class NoopGlobalPauseGate : IGlobalPauseGate
{
    /// <inheritdoc />
    public bool IsPaused => false;
}
