using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Adapter for the group-claim RPC surface (add-group-claim-receive, section 2): <c>requestGroupClaim</c>,
/// <c>listMyBindings</c>, <c>revokeMyBinding</c>. All are per-identity self-service — the caller identity is
/// taken from <see cref="SignalCallContext.Principal"/> (anti-IDOR / звірка №1), NEVER from a client argument.
/// </summary>
/// <remarks>
/// Gated: коли <c>Server:GroupClaim:Enabled=false</c> кожен метод відмовляє <c>-32601</c> (поверхня
/// невидима, як «метод не існує»). Privacy: плейнтекст-код повертається рівно один раз і НІКОЛИ не логується.
/// </remarks>
public sealed class SignalGroupClaimRpcAdapter(
    GroupClaimService claimService,
    IDurableStore store,
    IOptions<GroupClaimOptions> options,
    ILogger<SignalGroupClaimRpcAdapter> logger)
    : ISignalGroupClaimRpc
{
    private readonly GroupClaimService _claimService =
        claimService ?? throw new ArgumentNullException(nameof(claimService));

    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly GroupClaimOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value;

    private readonly ILogger<SignalGroupClaimRpcAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<GroupClaimResponse> RequestGroupClaim(CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        var identity = RequireIdentity();

        var result = _claimService.RequestClaim(identity);
        switch (result.Status)
        {
            case GroupClaimStatus.RateLimited:
                throw new RpcErrorException(
                    (JsonRpcErrorCode)AdmissionErrorCodes.RateLimited,
                    AdmissionErrorCodes.RateLimitedMessage,
                    new RateLimitErrorData(60));

            case GroupClaimStatus.QuotaExceeded:
                throw new RpcErrorException(
                    (JsonRpcErrorCode)GroupClaimErrorCodes.QuotaExceeded,
                    "Active binding quota exceeded — revoke a binding and retry.");

            default:
                // НЕ логуємо код — лише факт видачі (сам GroupClaimService логує identity/термін без коду).
                return Task.FromResult(new GroupClaimResponse(result.Code!, result.ExpiresAt!.Value));
        }
    }

    /// <inheritdoc />
    public Task<ListBindingsResponse> ListMyBindings(CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        var identity = RequireIdentity();

        // Лише власні (не-revoked) binding-и — жодних чужих даних (task 2.6).
        var bindings = _store.GetGroupBindingsForIdentity(identity)
            .Where(b => !b.Revoked)
            .Select(b => new GroupBindingInfo(b.BindingId, b.GroupId, b.ExpiresAt))
            .ToList();

        return Task.FromResult(new ListBindingsResponse(bindings));
    }

    /// <inheritdoc />
    public Task<RevokeBindingResponse> RevokeMyBinding(
        string bindingId, CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        var identity = RequireIdentity();

        if (string.IsNullOrWhiteSpace(bindingId))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "bindingId cannot be empty.");

        // Ownership-guard: revoke лише СВІЙ binding (перевіряємо, що id серед власних caller-а).
        var owns = _store.GetGroupBindingsForIdentity(identity).Any(b => string.Equals(b.BindingId, bindingId, StringComparison.Ordinal));
        if (!owns)
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Binding not found.");

        _store.RevokeGroupBinding(bindingId);
        _logger.LogInformation("revokeMyBinding: identity {Identity} відкликала власний binding.", identity);
        return Task.FromResult(new RevokeBindingResponse(bindingId, Revoked: true));
    }

    // Фіча вимкнена → поверхня невидима: -32601 (як «метод не існує»), не розкриваючи існування.
    private void RequireEnabled()
    {
        if (!_options.Enabled)
            throw new RpcErrorException((JsonRpcErrorCode)(-32601), "Method not found.");
    }

    // Caller identity — з call-context principal'а (V8/anti-IDOR), НІКОЛИ з клієнтського аргумента.
    private static string RequireIdentity()
    {
        var principal = SignalCallContext.Principal;
        if (principal is null || string.IsNullOrWhiteSpace(principal.Name))
            throw new RpcErrorException((JsonRpcErrorCode)AccountIsolationGuard.UnauthorizedErrorCode, "Access denied.");

        return principal.Name;
    }
}
