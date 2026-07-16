namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The result of <c>TokenService.Authenticate</c>: the resolved <see cref="TokenAuthenticationStatus"/>
/// plus the identity and token hash when a store record was found.
/// </summary>
/// <param name="Status">The authentication outcome.</param>
/// <param name="IdentityId">The authenticated identity, or <c>null</c> when no record matched.</param>
/// <param name="TokenHash">The matched token hash, or <c>null</c> when no record matched.</param>
public sealed record TokenAuthenticationResult(
    TokenAuthenticationStatus Status,
    string? IdentityId,
    string? TokenHash);
