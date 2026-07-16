using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The single inbound anti-IDOR line: asserts that a principal may operate on the account it named in
/// a call. The allowed set comes from the principal, never from the argument (звірка №1).
/// </summary>
/// <remarks>
/// W8: викликається на КОЖНОМУ account-bearing методі (не лише 5 MVP) — див.
/// <see cref="SignalRpcPolicyRegistry"/>. Відмова кидає <c>-32001</c> (узгоджено з фреймворковим
/// <c>RpcAuthorizationEnforcer.UnauthorizedErrorCode</c>) з generic-повідомленням, що НЕ розкриває,
/// чи існує чужий акаунт (anti-enumeration); деталі — лише в серверний лог (санітизація, розділ 3).
/// </remarks>
public static class AccountIsolationGuard
{
    /// <summary>JSON-RPC error code for an authorization denial (application level).</summary>
    public const int UnauthorizedErrorCode = -32001;

    /// <summary>
    /// Asserts that <paramref name="principal"/> may operate on <paramref name="account"/>; throws
    /// <see cref="RpcErrorException"/> (<c>-32001</c>) otherwise. Does not reach signal-cli on denial.
    /// </summary>
    /// <param name="principal">The session principal (source of allowed accounts).</param>
    /// <param name="account">The account named by the caller.</param>
    /// <exception cref="RpcErrorException">If the principal may not operate on the account.</exception>
    public static void AssertAccountAllowed(SignalPrincipal principal, string? account)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.CanOperateAccount(account))
            return;

        // Generic client message — не розкриваємо існування акаунта B.
        throw new RpcErrorException((JsonRpcErrorCode)UnauthorizedErrorCode, "Access denied.");
    }
}
