using Microsoft.Extensions.Logging;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Message;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Security;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

public class SignalMessageRpcAdapter(ISignalMessage signalMessage, ILogger<SignalMessageRpcAdapter> logger)
    : ISignalMessageRpc
{
    private readonly ISignalMessage _signalMessage =
        signalMessage ?? throw new ArgumentNullException(nameof(signalMessage));

    private readonly ILogger<SignalMessageRpcAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<SendMessageResponse> SendTextMessage(
        string account,
        IEnumerable<string> recipients,
        string message,
        CancellationToken cancellationToken = default)
    {
        // privacy (CLAUDE rule #4): не логувати account(=E.164) — лише факт виклику
        _logger.LogInformation("RPC: Request to send text message");

        if (string.IsNullOrWhiteSpace(account))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Account cannot be empty");

        // Розпізнаємо групу-recipient (base64 group-id, НЕ валідний user-recipient) → GroupRecipient;
        // решта — UserRecipient. Без GroupClaim chokepoint відсіює group-id ще до адаптера (лишається
        // user-only поведінка); з GroupClaim gate вже авторизував групу — тут лише коректний recipient-тип.
        var recipientList = recipients?
            .Where(static r => !string.IsNullOrWhiteSpace(r))
            .Select(static r => ToRecipient(r))
            .ToList() ?? [];

        if (recipientList.Count == 0)
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "At least one recipient is required");

        try
        {
            var options = new TextMessageOptions.Builder(
                    account,
                    recipientList,
                    message ?? string.Empty)
                .Build();

            return await _signalMessage.SendTextMessageAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcErrorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending text message");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error sending text message", ex);
        }
    }

    // Класифікація recipient'а: група (base64 group-id, не валідний user-id) → GroupRecipient; інакше
    // UserRecipient. «Не user-id» відсікає довгий E.164/username, який теоретично матчить group-регекс.
    private static IRecipient ToRecipient(string raw) =>
        RecipientValidator.IsValidGroupId(raw) &&
        !RecipientValidator.TryNormalizeUserRecipient(raw, out _, out _)
            ? new GroupRecipient(raw.Trim())
            : new UserRecipient(raw);
}

