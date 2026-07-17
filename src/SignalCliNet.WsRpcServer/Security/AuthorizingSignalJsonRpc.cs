using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Security.Admission;
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
    private readonly Func<IReadOnlyList<string>, AdmissionDecision>? _admitSends;
    private readonly SignalCliGate? _signalCliGate;
    private readonly int _maxInFlight;

    // Per-connection лічильник in-flight RPC (D12). Interlocked inc на вході dispatch / dec у finally.
    private int _inFlight;

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
    /// <param name="admitSends">
    /// Optional send-admission closure (W16, task 3.2/4.1). When supplied, it is invoked ONLY for
    /// send-like methods (those whose policy declares a <c>recipients</c> argument), AFTER the authz
    /// policy and inbound guards but BEFORE dispatch; a denied decision short-circuits to <c>-32005</c>
    /// with an in-band <c>retry_after</c>. <c>null</c> (admission disabled) skips admission entirely.
    /// </param>
    /// <param name="signalCliGate">
    /// Optional server-wide non-blocking concurrency gate (G6, task 3.3) acquired around dispatch of
    /// non-<see cref="RpcPolicyKind.Public"/> methods. When full, the call is refused with <c>-32005</c>
    /// (<c>retry_after=1</c>). <c>null</c> (admission disabled) leaves dispatch ungated.
    /// </param>
    /// <param name="maxInFlightPerConnection">
    /// Per-connection in-flight dispatch cap (D12, task 3.3) for non-<see cref="RpcPolicyKind.Public"/>
    /// methods. <c>0</c> (admission disabled) means no cap. Exceeding it refuses immediately with
    /// <c>-32005</c> (<c>retry_after=1</c>) — no queue.
    /// </param>
    public AuthorizingSignalJsonRpc(
        IJsonRpcMessageHandler messageHandler,
        SignalPrincipal principal,
        IRpcPolicyRegistry policies,
        ILogger? logger = null,
        Func<bool>? isCredentialLive = null,
        Func<IReadOnlyList<string>, AdmissionDecision>? admitSends = null,
        SignalCliGate? signalCliGate = null,
        int maxInFlightPerConnection = 0)
        : base(messageHandler)
    {
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _logger = logger;
        _isCredentialLive = isCredentialLive;
        _admitSends = admitSends;
        _signalCliGate = signalCliGate;
        _maxInFlight = maxInFlightPerConnection;
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

        // 1b. Admin-гейт (task 3.1): admin-метод для не-admin principal → -32001 (санітизовано, як
        //     default-deny). Admin-канал — окремий mTLS-порт; на плейн-порту жоден principal не IsAdmin,
        //     тож admin-методи там завжди відмовляють. Санітизований generic -32001 не розкриває існування.
        if (policy.Kind == RpcPolicyKind.Admin && !_principal.IsAdmin)
        {
            _logger?.LogWarning("Chokepoint: admin-метод {Method} для не-admin principal — відмова (-32001)", method);
            return Error(request, (JsonRpcErrorCode)AccountIsolationGuard.UnauthorizedErrorCode, "Access denied.");
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

        // 2b. Admission (W16, tasks 3.2/4.1): ЛИШЕ для send-like методів (політика несе recipients).
        //     Виконується ПІСЛЯ authz-політики й guard-ів (default-deny перший), ДО dispatch. Deny →
        //     -32005 з in-band retry_after (S9). recipients витягуємо тим самим шляхом, що й guard (D11).
        if (_admitSends is not null && policy.RecipientsArgName is not null)
        {
            var recipients = ExtractStringList(request, policy.RecipientsArgName, policy.RecipientsArgIndex);
            var decision = _admitSends(recipients);
            if (!decision.Admitted)
            {
                // privacy: логуємо лише метод/причину/retry — БЕЗ recipients і тіла.
                _logger?.LogInformation(
                    "Chokepoint: send {Method} відхилено admission ({Reason}, retry_after={RetryAfter}s) → -32005",
                    method, decision.Reason, decision.RetryAfterSeconds);
                return RateLimitedError(request, decision.RetryAfterSeconds);
            }
        }

        // 3. Per-connection in-flight cap (D12) + server-wide G6-гейт до signal-cli — ЛИШЕ для не-Public
        //    методів (ping/handshake повз обидва). Обидва короткозамикають на -32005 (retry_after=1) без
        //    черги. Диспетч цільового методу — між акваєрами; principal у call-context на час виклику (V8).
        var gated = policy.Kind != RpcPolicyKind.Public;
        var inFlightHeld = false;
        var gateHeld = false;
        try
        {
            if (gated && _maxInFlight > 0)
            {
                var current = Interlocked.Increment(ref _inFlight);
                inFlightHeld = true;
                if (current > _maxInFlight)
                {
                    _logger?.LogInformation(
                        "Chokepoint: {Method} понад per-connection in-flight cap ({Max}) → -32005", method, _maxInFlight);
                    return RateLimitedError(request, 1);
                }
            }

            if (gated && _signalCliGate is not null)
            {
                if (!_signalCliGate.TryEnter())
                {
                    _logger?.LogInformation(
                        "Chokepoint: {Method} — signal-cli гейт зайнятий (G6) → -32005", method);
                    return RateLimitedError(request, 1);
                }

                gateHeld = true;
            }

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
        finally
        {
            // Звільняємо у зворотному порядку; кожен — рівно раз (навіть на ранньому -32005-return).
            if (gateHeld)
                _signalCliGate!.Exit();
            if (inFlightHeld)
                Interlocked.Decrement(ref _inFlight);
        }
    }

    // Детермінований JSON-RPC -32005 (rate-limit) з in-band retry_after у error.data (A1: без секретів).
    private static JsonRpcError RateLimitedError(JsonRpcRequest request, int retryAfterSeconds) => new()
    {
        RequestId = request.RequestId,
        Error = new JsonRpcError.ErrorDetail
        {
            Code = (JsonRpcErrorCode)AdmissionErrorCodes.RateLimited,
            Message = AdmissionErrorCodes.RateLimitedMessage,
            Data = new RateLimitErrorData(retryAfterSeconds),
        },
    };

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
