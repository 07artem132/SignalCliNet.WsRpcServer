using SignalCliNet.WsRpcServer.Model;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

/// <summary>
/// The group-claim RPC surface (add-group-claim-receive, section 2): an authenticated identity requests a
/// one-time claim code, lists its own bindings, and revokes an unwanted one. All three are per-identity
/// self-service (the caller identity comes from the session principal, never a client argument).
/// </summary>
/// <remarks>
/// Gated by <c>Server:GroupClaim:Enabled</c>: when the feature is off, the adapter refuses with
/// <c>-32601</c> (method-not-found) so the surface is invisible. The methods are classified
/// <c>AccountCommand</c> (authenticated, no account argument) in the policy registry.
/// </remarks>
public interface ISignalGroupClaimRpc : IRpcService
{
    /// <summary>
    /// Requests a fresh one-time claim code for the caller identity (T3: group-agnostic — no group argument).
    /// The caller pastes the returned code into the target group; the scanner fixes the group + anchor.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plaintext claim code (secret, once) and its expiry.</returns>
    Task<GroupClaimResponse> RequestGroupClaim(CancellationToken cancellationToken = default);

    /// <summary>Lists the caller's OWN active group-bindings (self-management, task 2.6; no other identity's data).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The caller's own bindings.</returns>
    Task<ListBindingsResponse> ListMyBindings(CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes one of the caller's OWN group-bindings by id (e.g. a binding created by pasting the caller's
    /// code into an unwanted group — residual T3). Fails if the binding is not the caller's.
    /// </summary>
    /// <param name="bindingId">The caller's binding id to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The revoke acknowledgement.</returns>
    Task<RevokeBindingResponse> RevokeMyBinding(string bindingId, CancellationToken cancellationToken = default);
}
