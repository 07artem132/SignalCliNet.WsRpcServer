namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// RPC result of the admin <c>revokeIdentity</c>: acknowledges the cascade revoke (all access tokens +
/// device secret) of the target identity. Carries no secret — registered in the source-gen context.
/// </summary>
/// <param name="IdentityId">The identity whose credentials were revoked (opaque; never a phone number).</param>
/// <param name="Revoked">Always <c>true</c> on success (the call throws on failure).</param>
public sealed record RevokeIdentityResponse(string IdentityId, bool Revoked);
