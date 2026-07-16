namespace SignalCliNet.WsRpcServer.Security;

/// <summary>The outcome of authenticating an opaque access token against the durable store.</summary>
public enum TokenAuthenticationStatus
{
    /// <summary>Token is well-formed, present, not revoked and not expired.</summary>
    Valid,

    /// <summary>Token is present and not revoked but past its expiry (eligible for PoP renewal, T5).</summary>
    Expired,

    /// <summary>Token is present but revoked — revocation always wins over expiry (D7).</summary>
    Revoked,

    /// <summary>Token is malformed, its version is unknown, or no matching hash exists in the store.</summary>
    Invalid,
}
