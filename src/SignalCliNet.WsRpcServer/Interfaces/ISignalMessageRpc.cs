using SignalCli.Models.Signal.Message;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalMessageRpc : IRpcService
{
    /// <param name="recipients">Отримувачі-користувачі (E.164 або UUID).</param>
    /// <param name="groups">Отримувачі-групи (base64 groupId з <c>listGroups</c>);
    /// обмеження signal-cli: максимум одна група і без змішування з користувачами.</param>
    Task<SendMessageResponse> SendTextMessage(
        string account,
        IEnumerable<string>? recipients,
        string message,
        IEnumerable<string>? groups = null,
        CancellationToken cancellationToken = default);
}
