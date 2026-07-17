using SignalCli.Models.Signal.Message;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalMessageRpc : IRpcService
{
    /// <summary>
    /// Sends a text message from <paramref name="account"/> to <paramref name="recipients"/>.
    /// </summary>
    /// <param name="account">The sending account.</param>
    /// <param name="recipients">The recipients (user identifiers and/or authorized group ids).</param>
    /// <param name="message">The message body.</param>
    /// <param name="messageId">
    /// Optional client-generated idempotency key (add-ops-observability, task 3.3). When supplied, the
    /// send is deduplicated durably at the chokepoint (a retry with the same key returns the original
    /// result without re-sending or double-charging the budget). When omitted, behaviour is unchanged.
    /// Consumed by the chokepoint, not by signal-cli.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<SendMessageResponse> SendTextMessage(
        string account,
        IEnumerable<string> recipients,
        string message,
        string? messageId = null,
        CancellationToken cancellationToken = default);
}
