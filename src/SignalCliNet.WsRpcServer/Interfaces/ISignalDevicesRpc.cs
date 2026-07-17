using SignalCli.Models.Signal.Devices;
using SignalCliNet.WsRpcServer.Model;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalDevicesRpc : IRpcService
{
    /// <summary>
    /// Starts device linking: mints a one-time link session and returns its <c>sessionId</c> together with
    /// the <c>deviceLinkUri</c> (QR). The raw URI is sensitive and must never be logged.
    /// </summary>
    /// <param name="targetAccount">
    /// Optional target-account hint. If it names an account already bound in the store (a shared bot),
    /// starting a link requires an admin/full-access principal (takeover protection).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StartLinkSessionResponse> StartLink(
        string? targetAccount = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finishes device linking by consuming the one-time <paramref name="sessionId"/> (NOT the raw URI).
    /// The session is consumed only if the caller's identity matches the one that started it.
    /// </summary>
    /// <param name="sessionId">The one-time session id returned by <see cref="StartLink"/>.</param>
    /// <param name="deviceName">The device name to register.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FinishLinkResponse> FinishLink(string sessionId, string deviceName,
        CancellationToken cancellationToken = default);
}
