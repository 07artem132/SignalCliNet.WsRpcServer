using SignalCli.Models.Signal.Message;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalMessageRpc : IRpcService
{
    /// <param name="recipients">Отримувачі-користувачі (E.164 або UUID). Опційний, щоб виклик
    /// лише з <paramref name="groups"/> проходив біндинг StreamJsonRpc (без дефолта named-виклик
    /// без цього параметра падає -32602 ще до методу); JSON-RPC клієнти передають параметри
    /// за іменами, тож порядок не є частиною wire-контракту.</param>
    /// <param name="groups">Отримувачі-групи (base64 groupId з <c>listGroups</c>);
    /// обмеження signal-cli: максимум одна група і без змішування з користувачами.</param>
    Task<SendMessageResponse> SendTextMessage(
        string account,
        string message,
        IEnumerable<string>? recipients = null,
        IEnumerable<string>? groups = null,
        CancellationToken cancellationToken = default);
}
