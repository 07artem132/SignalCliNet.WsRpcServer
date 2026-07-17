using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The single pre-dispatch authorization chokepoint. A <see cref="JsonRpc"/> subclass that, on the
/// reflection dispatch path, forces every RPC call through the declarative policy registry BEFORE the
/// target method runs.
/// </summary>
/// <remarks>
/// Hermetic default-deny (звірка №4): unlike the framework's additive <c>[RpcAuthorize]</c> (open unless
/// attributed), a method with no row in <see cref="IRpcPolicyRegistry"/> is refused with <c>-32601</c>
/// here, before dispatch. The session constructs
/// <c>new AuthorizingSignalJsonRpc(handler, principal, policies, logger)</c> instead of <c>new JsonRpc(handler)</c> (V9).
///
/// Per-connection isolation: each session owns one instance carrying that session's
/// <see cref="SignalPrincipal"/>, so the principal is read fresh per invocation and never held by the
/// shared adapters (V8). The principal is also published to <see cref="SignalCallContext"/> for the
/// duration of each dispatch.
/// </remarks>
public sealed class AuthorizingSignalJsonRpc : JsonRpc
{
    /// <summary>Generous upper bound on a message body (chars) checked before dispatch (D11).</summary>
    private const int MaxMessageTextChars = 64_000;

    private readonly SignalPrincipal _principal;
    private readonly IRpcPolicyRegistry _policies;
    private readonly ILogger? _logger;
    private readonly Func<bool>? _isCredentialLive;

    /// <summary>
    /// Creates the authorizing JSON-RPC chokepoint.
    /// </summary>
    /// <param name="messageHandler">The transport message handler.</param>
    /// <param name="principal">The session principal (Phase-1 loopback = full access).</param>
    /// <param name="policies">The declarative policy registry.</param>
    /// <param name="logger">Optional logger for denials (server-side only).</param>
    /// <param name="isCredentialLive">
    /// Optional per-dispatch credential liveness check (D9/task 1.5). When supplied, it is invoked before
    /// policy evaluation on EVERY dispatch; a <c>false</c> result is refused exactly like default-deny
    /// (sanitized <c>-32601</c>) — so a revoked/expired token session stops dispatching mid-connection.
    /// <c>null</c> (loopback sessions) preserves the unchanged behaviour.
    /// </param>
    public AuthorizingSignalJsonRpc(
        IJsonRpcMessageHandler messageHandler,
        SignalPrincipal principal,
        IRpcPolicyRegistry policies,
        ILogger? logger = null,
        Func<bool>? isCredentialLive = null)
        : base(messageHandler)
    {
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _logger = logger;
        _isCredentialLive = isCredentialLive;
    }

    /// <inheritdoc />
    protected override async ValueTask<JsonRpcMessage> DispatchRequestAsync(
        JsonRpcRequest request,
        TargetMethod targetMethod,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targetMethod);

        var method = request.Method ?? string.Empty;

        // 0. Re-чек живучості креденшелу (D9/1.5): ДО політики, на кожен dispatch. Мертвий токен
        //    (revoked/expired) → та сама відмова, що й default-deny (sanitized -32601) + warning.
        if (_isCredentialLive is not null && !_isCredentialLive())
        {
            _logger?.LogWarning(
                "Chokepoint: креденшел сесії більше не живий (revoked/expired) — відмова dispatch {Method} (-32601)",
                method);
            return Error(request, JsonRpcErrorCode.MethodNotFound, "Method not found.");
        }

        // 1. Герметичний default-deny: метод без явної політики → -32601 ще до виконання тіла.
        var policy = _policies.Resolve(method);
        if (policy is null)
        {
            // privacy: логуємо лише ім'я методу (не PII); клієнту — generic -32601.
            _logger?.LogWarning("Chokepoint: RPC-метод {Method} не має явної політики — відмова (-32601)", method);
            return Error(request, JsonRpcErrorCode.MethodNotFound, "Method not found.");
        }

        // 2. Inbound guards (pre-dispatch): anti-IDOR по account + валідація recipient/тексту (D11).
        //    Відмови кидають RpcErrorException; конвертуємо у детермінований JsonRpcError.
        try
        {
            RunInboundGuards(policy, request);
        }
        catch (RpcErrorException ex)
        {
            // Санітизація (3.1): generic клієнту, деталі — в лог; той самий шлях, що й для помилок диспетчу.
            return new JsonRpcError
            {
                RequestId = request.RequestId,
                Error = RpcErrorSanitizer.CreateDetail(ex, method, _logger),
            };
        }

        // 3. Диспетч цільового методу; principal публікуємо в call-context на час виклику (V8).
        JsonRpcMessage response;
        using (SignalCallContext.Enter(_principal))
        {
            response = await base.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false);
        }

        // 4. Outbound-фільтр (AccountQuery) — fail-closed, лише для успішного результату.
        if (policy.Output != ReadOutputKind.None && response is JsonRpcResult result)
        {
            result.Result = ReadOutputFilter.Filter(policy.Output, _principal, result.Result, _logger);
        }

        return response;
    }

    // Проганяє inbound-перевірки, які МУСЯТЬ відпрацювати до dispatch: жоден малформований чи
    // неавторизований виклик не доходить до signal-cli.
    private void RunInboundGuards(RpcMethodPolicy policy, JsonRpcRequest request)
    {
        // Anti-IDOR: якщо метод несе account — звіряємо його з principal (W8).
        if (policy.AccountArgName is not null)
        {
            var account = ExtractString(request, policy.AccountArgName, policy.AccountArgIndex);
            if (string.IsNullOrWhiteSpace(account))
                throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Account cannot be empty.");

            AccountIsolationGuard.AssertAccountAllowed(_principal, account);
        }

        // D11: валідація recipient'ів до dispatch.
        if (policy.RecipientsArgName is not null)
        {
            var recipients = ExtractStringList(request, policy.RecipientsArgName, policy.RecipientsArgIndex);
            RecipientValidator.AssertValidUserRecipients(recipients);
        }

        // D11: розмір/кодування тексту повідомлення до dispatch.
        if (policy.TextArgName is not null)
        {
            var text = ExtractString(request, policy.TextArgName, policy.TextArgIndex);
            if (text is not null && (text.Length > MaxMessageTextChars || text.Contains('\0')))
                throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Message text is invalid or too large.");
        }
    }

    // Дістає рядковий аргумент за іменем АБО позицією (named/positional запити) — через
    // TryGetArgumentByNameOrIndex, який робить типізовану десеріалізацію з форматтера.
    private static string? ExtractString(JsonRpcRequest request, string name, int index) =>
        request.TryGetArgumentByNameOrIndex(name, index, typeof(string), out var value) && value is string s
            ? s
            : null;

    // Дістає масив рядків (recipients). Порожній список, якщо аргумент відсутній/непарситься —
    // порожнечу/малформ обробляють валідатор і адаптер нижче.
    private static IReadOnlyList<string> ExtractStringList(JsonRpcRequest request, string name, int index)
    {
        if (!request.TryGetArgumentByNameOrIndex(name, index, typeof(List<string>), out var value) || value is null)
            return [];

        return value switch
        {
            IReadOnlyList<string> list => list,
            IEnumerable<string> seq => seq.ToList(),
            IEnumerable<object?> objs => objs.Select(static o => o?.ToString() ?? string.Empty).ToList(),
            _ => [],
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Санітизація (3.1): усі помилки диспетчу (виняток адаптера тощо) проходять тут — замість дефолтної
    /// StreamJsonRpc-обгортки з <c>CommonErrorData</c> (стек клієнту) віддаємо generic-деталь, повний
    /// виняток логуючи на сервері.
    /// </remarks>
    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception) =>
        RpcErrorSanitizer.CreateDetail(exception, request?.Method, _logger);

    // Детермінований JSON-RPC error із заданими кодом/повідомленням (повне керування, без CommonErrorData).
    private static JsonRpcError Error(JsonRpcRequest request, JsonRpcErrorCode code, string message) => new()
    {
        RequestId = request.RequestId,
        Error = new JsonRpcError.ErrorDetail
        {
            Code = code,
            Message = message,
        },
    };
}
