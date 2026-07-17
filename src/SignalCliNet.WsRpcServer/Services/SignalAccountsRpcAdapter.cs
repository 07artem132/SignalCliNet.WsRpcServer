using System.Globalization;
using Microsoft.Extensions.Logging;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Accounts;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

public class SignalAccountsRpcAdapter : ISignalAccountsRpc
{
    private readonly ISignalAccounts _signalAccounts;
    private readonly ILogger<SignalAccountsRpcAdapter> _logger;

    // Опційні admin-залежності (add-invites-admin, секція 2). Null у юніт-тестах адаптера, реальні у DI —
    // тоді admin listAccounts аудитується (R3.6) і G10-legacy акаунти bind-яться до admin (task 2.4).
    private readonly IDurableStore? _store;
    private readonly AuditLogService? _auditLog;
    private readonly TimeProvider? _timeProvider;

    public SignalAccountsRpcAdapter(
        ISignalAccounts signalAccounts,
        ILogger<SignalAccountsRpcAdapter> logger,
        IDurableStore? store = null,
        AuditLogService? auditLog = null,
        TimeProvider? timeProvider = null)
    {
        _signalAccounts = signalAccounts ?? throw new ArgumentNullException(nameof(signalAccounts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store;
        _auditLog = auditLog;
        _timeProvider = timeProvider;
    }

    public async Task<ListAccountsResponse> ListAccounts(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Request to list accounts");
        try
        {
            var response = await _signalAccounts.ListAccountsAsync(cancellationToken).ConfigureAwait(false);

            // R3.6: admin — read-only нагляд УСІХ акаунтів. На admin-виклику: (1) G10-bind legacy-акаунтів,
            // (2) audit-подія. Не-admin/loopback — гілка не активна (поведінка незмінна). Outbound-фільтр
            // chokepoint'а (ReadOutputFilter) віддає admin усі акаунти незалежно від цього.
            var principal = SignalCallContext.Principal;
            if (principal is { IsAdmin: true } && _store is not null && _auditLog is not null)
                OnAdminListAccounts(principal, response);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing accounts");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error listing accounts", ex);
        }
    }

    public async Task<SyncAccountsResponse> SyncAccount(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Request to sync accounts");
        try
        {
            return await _signalAccounts.SyncAccountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing accounts");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error syncing accounts", ex);
        }
    }

    // G10 (task 2.4) + audit (task 3.2) на admin listAccounts. Джерело legacy-акаунтів — сам демон
    // (немає прямого bootstrap-API до старту signal-cli), тож bind робимо ЛІНИВО при першому admin-огляді:
    // будь-який акаунт демона БЕЗ binding у сторі → bind до admin-identity (IsPrivate=false ⇒ admin бачить;
    // R3.6). Ідемпотентно (пропускаємо вже прив'язані — не перезаписуємо user-owned bindings).
    private void OnAdminListAccounts(SignalPrincipal admin, ListAccountsResponse response)
    {
        var now = (_timeProvider ?? TimeProvider.System).GetUtcNow();
        var boundLegacy = 0;

        foreach (var account in response.Items)
        {
            var number = account.Number;
            if (string.IsNullOrWhiteSpace(number))
                continue;

            if (_store!.GetAccountBinding(number) is null)
            {
                _store.UpsertAccountBinding(new AccountBinding(number, admin.Name, IsPrivate: false, now));
                boundLegacy++;
            }
        }

        if (boundLegacy > 0)
            _logger.LogInformation("G10: bind {Count} legacy-акаунт(ів) до admin {Admin}.", boundLegacy, admin.Name);

        // Audit: admin listAccounts(all). Деталь — лічильники (без номерів/PII).
        _auditLog!.Append(
            "admin_list_accounts",
            admin.Name,
            string.Create(CultureInfo.InvariantCulture, $"accounts={response.Items.Count};legacyBound={boundLegacy}"));
    }
}
