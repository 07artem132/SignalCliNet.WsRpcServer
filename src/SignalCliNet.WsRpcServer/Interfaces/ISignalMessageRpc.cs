using SignalCli.Models.Signal.Message;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalMessageRpc : IRpcService
{
    Task<SendMessageResponse> SendTextMessage(
        string account,
        IEnumerable<string> recipients,
        string message,
        CancellationToken cancellationToken = default);
}
