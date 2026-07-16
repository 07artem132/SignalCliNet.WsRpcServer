namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Declarative registry mapping each RPC method name to its authorization policy — the authority the
/// chokepoint consults before dispatch. Deliberately INDEPENDENT of auto-discovery: a method that is
/// discovered and dispatchable but absent here has no policy and is denied (default-deny), rather than
/// being open-by-omission the way an un-attributed <c>[RpcAuthorize]</c> method would be.
/// </summary>
public interface IRpcPolicyRegistry
{
    /// <summary>
    /// Resolves the policy for <paramref name="method"/> (camelCase wire name), or <c>null</c> if the
    /// method has no explicit policy (→ default-deny at the chokepoint).
    /// </summary>
    /// <param name="method">The camelCase wire method name.</param>
    /// <returns>The method's policy, or <c>null</c>.</returns>
    RpcMethodPolicy? Resolve(string method);

    /// <summary>All declared policies (used by the startup coverage guard).</summary>
    IReadOnlyCollection<RpcMethodPolicy> All { get; }
}
