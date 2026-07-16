using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

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
    private readonly SignalPrincipal _principal;
    private readonly IRpcPolicyRegistry _policies;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates the authorizing JSON-RPC chokepoint.
    /// </summary>
    /// <param name="messageHandler">The transport message handler.</param>
    /// <param name="principal">The session principal (Phase-1 loopback = full access).</param>
    /// <param name="policies">The declarative policy registry.</param>
    /// <param name="logger">Optional logger for denials (server-side only).</param>
    public AuthorizingSignalJsonRpc(
        IJsonRpcMessageHandler messageHandler,
        SignalPrincipal principal,
        IRpcPolicyRegistry policies,
        ILogger? logger = null)
        : base(messageHandler)
    {
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _logger = logger;
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

        // 1. Герметичний default-deny: метод без явної політики → -32601 ще до виконання тіла.
        var policy = _policies.Resolve(method);
        if (policy is null)
        {
            // privacy: логуємо лише ім'я методу (не PII); клієнту — generic -32601.
            _logger?.LogWarning("Chokepoint: RPC-метод {Method} не має явної політики — відмова (-32601)", method);
            return MethodNotFound(request);
        }

        // 2. Диспетч цільового методу; principal публікуємо в call-context на час виклику (V8).
        using (SignalCallContext.Enter(_principal))
        {
            return await base.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false);
        }
    }

    // Generic -32601 без відображення вводу клієнта — узгоджено з політикою санітизації помилок.
    private static JsonRpcError MethodNotFound(JsonRpcRequest request) => new()
    {
        RequestId = request.RequestId,
        Error = new JsonRpcError.ErrorDetail
        {
            Code = JsonRpcErrorCode.MethodNotFound,
            Message = "Method not found.",
        },
    };
}
