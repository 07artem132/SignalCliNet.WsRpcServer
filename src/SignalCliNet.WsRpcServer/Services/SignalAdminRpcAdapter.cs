using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Adapter for the admin RPC surface (add-invites-admin, task 3.1): <c>createInvite</c> and
/// <c>revokeIdentity</c>. Reachable ONLY through the mTLS admin port — the chokepoint refuses these
/// methods for any non-admin principal (<c>-32001</c>), and no plain-port principal is ever admin.
/// </summary>
/// <remarks>
/// Актор кожної дії — admin identity id з <see cref="SignalCallContext.Principal"/> (виведений із
/// валідованого mTLS-cert-а), НІКОЛИ з клієнтського аргумента (anti-IDOR / звірка №1). Кожна дія пише
/// подію у tamper-evident audit-trail (<see cref="AuditLogService"/>, task 3.2).
///
/// Privacy: плейнтекст-код інвайту повертається адміну рівно один раз і НІКОЛИ не логується; у audit
/// потрапляють лише ідентифікатори (code_hash, identity id), не секрети.
/// </remarks>
public sealed class SignalAdminRpcAdapter(
    InviteService inviteService,
    TokenService tokenService,
    AuditLogService auditLog,
    IDurableStore store,
    IOptions<AdminOptions> adminOptions,
    TimeProvider timeProvider,
    ILogger<SignalAdminRpcAdapter> logger)
    : ISignalAdminRpc
{
    private readonly InviteService _inviteService =
        inviteService ?? throw new ArgumentNullException(nameof(inviteService));

    private readonly TokenService _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));

    private readonly AuditLogService _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));

    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly AdminOptions _adminOptions =
        (adminOptions ?? throw new ArgumentNullException(nameof(adminOptions))).Value;

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<SignalAdminRpcAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<CreateInviteResponse> CreateInvite(
        int? ttlSeconds = null, int? maxAttempts = null, CancellationToken cancellationToken = default)
    {
        var actor = RequireAdminActor();

        // Дефолти з конфігу; клієнтські значення валідуються на межі RPC (InvalidParams, не InvocationError).
        var ttl = TimeSpan.FromSeconds(ttlSeconds ?? _adminOptions.DefaultInviteTtlSeconds);
        var cap = maxAttempts ?? _adminOptions.DefaultInviteMaxAttempts;
        if (ttl <= TimeSpan.Zero)
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "ttlSeconds must be positive.");
        if (cap < 1)
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "maxAttempts must be at least 1.");

        var creation = _inviteService.Create(actor, ttl, cap);

        // Audit: подія invite_created (актор = admin, деталь = code_hash — ідентифікатор, не секрет).
        _auditLog.Append("invite_created", actor, creation.Record.CodeHash);

        // НЕ логуємо код — лише факт видачі (сам InviteService.Create уже логує емітента/термін без коду).
        return Task.FromResult(new CreateInviteResponse(creation.Code, creation.Record.ExpiresAt));
    }

    /// <inheritdoc />
    public Task<RevokeIdentityResponse> RevokeIdentity(
        string identityId, CancellationToken cancellationToken = default)
    {
        var actor = RequireAdminActor();

        if (string.IsNullOrWhiteSpace(identityId))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "identityId cannot be empty.");

        // Каскад revoke (токени + device-секрет) + розрив живих конектів identity (change 3).
        _tokenService.RevokeIdentity(identityId);

        // Audit: подія identity_revoked (актор = admin, деталь = ціль).
        _auditLog.Append("identity_revoked", actor, identityId);

        _logger.LogInformation("Admin {Actor}: revoke identity {IdentityId}.", actor, identityId);
        return Task.FromResult(new RevokeIdentityResponse(identityId, Revoked: true));
    }

    /// <inheritdoc />
    public Task<RevokeBindingResponse> RevokeBinding(
        string identityId, string bindingId, CancellationToken cancellationToken = default)
    {
        var actor = RequireAdminActor();

        if (string.IsNullOrWhiteSpace(identityId))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "identityId cannot be empty.");
        if (string.IsNullOrWhiteSpace(bindingId))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "bindingId cannot be empty.");

        // Ownership-guard: binding МУСИТЬ належати саме вказаній identity — хірургічний revoke ОДНОГО
        // права чужого юзера (на відміну від revokeIdentity, що нукає всю identity). Немає збігу →
        // InvalidParams «Binding not found» (анти-oracle: невідомий bindingId і чужий — однакова помилка).
        var owns = _store.GetGroupBindingsForIdentity(identityId)
            .Any(b => string.Equals(b.BindingId, bindingId, StringComparison.Ordinal));
        if (!owns)
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Binding not found.");

        _store.RevokeGroupBinding(bindingId);

        // Audit: admin_binding_revoked (актор = admin; деталь = identity+binding — опакові id, не PII/секрети).
        _auditLog.Append("admin_binding_revoked", actor, $"{identityId}:{bindingId}");

        _logger.LogInformation("Admin {Actor}: revoke binding {BindingId} identity {IdentityId}.",
            actor, bindingId, identityId);
        return Task.FromResult(new RevokeBindingResponse(bindingId, Revoked: true));
    }

    // Актор — admin identity id з call-context principal'а. На admin-порту principal завжди IsAdmin
    // (mTLS-резолв); guard тут — belt-and-suspenders (chokepoint уже відсік не-admin -32001).
    private static string RequireAdminActor()
    {
        var principal = SignalCallContext.Principal;
        if (principal is null || !principal.IsAdmin)
            throw new RpcErrorException((JsonRpcErrorCode)AccountIsolationGuard.UnauthorizedErrorCode, "Access denied.");

        return principal.Name;
    }
}
