namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The four declarative authorization policies an RPC method can carry. Every dispatchable method
/// MUST map to exactly one; a method with no policy is denied at the chokepoint (default-deny).
/// </summary>
public enum RpcPolicyKind
{
    /// <summary>
    /// Unauthenticated, stateless, PII-free — safe for any caller (e.g. health probe, handshake).
    /// No account or output guarding.
    /// </summary>
    Public,

    /// <summary>
    /// Identity/onboarding operation that runs before an account exists (e.g. device linking).
    /// Requires no account guard because there is no account to isolate yet.
    /// </summary>
    IdentityOnboarding,

    /// <summary>
    /// Account-mutating command. Inbound-guarded: the target <c>account</c> argument is checked against
    /// the principal's allowed accounts (anti-IDOR) before dispatch.
    /// </summary>
    AccountCommand,

    /// <summary>
    /// Account read/query. Inbound-guarded (if it carries an <c>account</c> argument) AND outbound-guarded:
    /// the result is filtered to what the principal may see (fail-closed).
    /// </summary>
    AccountQuery,
}

/// <summary>
/// Which fail-closed outbound filter (if any) an <see cref="RpcPolicyKind.AccountQuery"/> result needs.
/// </summary>
public enum ReadOutputKind
{
    /// <summary>No outbound filtering.</summary>
    None,

    /// <summary>Filter an account listing to the accounts the principal may see (R3.5/R3.6).</summary>
    Accounts,

    /// <summary>Filter a group listing to the principal's claimed/own groups (R3.2, closes G9).</summary>
    Groups,
}
