namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// RPC result of <c>revokeMyBinding</c> (task 2.6): acknowledges revoke of the caller's own group-binding.
/// Carries no secret — registered in the source-gen serializer context.
/// </summary>
/// <param name="BindingId">The binding that was revoked (opaque).</param>
/// <param name="Revoked">Always <c>true</c> on success (the call throws if the binding is not the caller's).</param>
public sealed record RevokeBindingResponse(string BindingId, bool Revoked);
