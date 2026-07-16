using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// Server-wide non-blocking concurrency gate in front of signal-cli dispatch (G6, task 3.3). A single
/// <see cref="SemaphoreSlim"/> bounds how many RPC dispatches may be in signal-cli at once; acquisition is
/// NON-blocking (<see cref="SemaphoreSlim.Wait(int)"/> with a 0 timeout) so a flood of slow sends is
/// refused up-front with <c>-32005</c> instead of growing an unbounded pending queue upstream
/// (<c>_pendingRequests</c> without a cap).
/// </summary>
/// <remarks>
/// <para>
/// Це chokepoint-рівневий гейт у <see cref="AuthorizingSignalJsonRpc"/> (services-рівень), а НЕ декоратор
/// над <c>ISignalCliClient</c>: він досягає тієї ж мети (G6 — pending не росте unbounded), не тягнучи
/// upstream-інтерфейс і не форкаючи бібліотечну поведінку (див. відхилення у tasks.md).
/// </para>
/// <para>
/// Singleton; активний лише коли admission увімкнено (сесія передає гейт у chokepoint тільки при
/// <c>AdmissionOptions.Enabled</c>). Public-методи (<c>ping</c>/<c>handshake</c>) проходять повз гейт.
/// </para>
/// </remarks>
public sealed class SignalCliGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    /// <summary>Creates the gate sized to <see cref="AdmissionOptions.SignalCliConcurrencyLimit"/>.</summary>
    public SignalCliGate(IOptions<AdmissionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limit = options.Value.SignalCliConcurrencyLimit;
        _semaphore = new SemaphoreSlim(limit, limit);
    }

    /// <summary>
    /// Tries to enter the gate WITHOUT blocking. Returns <c>true</c> if a slot was taken (caller MUST call
    /// <see cref="Exit"/> once), <c>false</c> if the gate is currently full (caller returns <c>-32005</c>).
    /// </summary>
    public bool TryEnter() => _semaphore.Wait(0);

    /// <summary>Releases a slot previously taken by <see cref="TryEnter"/>.</summary>
    public void Exit() => _semaphore.Release();

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();
}
