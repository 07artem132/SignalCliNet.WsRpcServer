namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The authentication state of a single WebSocket session (drives which messages are accepted and
/// when RPC dispatch is allowed). Pure transitions live in <see cref="SessionAuthStateMachine"/>.
/// </summary>
public enum SessionAuthState
{
    /// <summary>
    /// Authentication is disabled (<c>Server:Auth:Enabled=false</c>): the session is immediately active
    /// with <c>LoopbackFullAccess</c> — the unchanged Phase-1 behaviour.
    /// </summary>
    Disabled,

    /// <summary>
    /// Auth is on, no token was supplied on upgrade: the session awaits the fallback first-message
    /// <c>authenticate</c> under an auth timeout (browser path).
    /// </summary>
    AwaitingAuthenticate,

    /// <summary>
    /// A token has authenticated; a proof-of-possession challenge is pending. In section 2 this state is
    /// a transient no-op (advances straight to <see cref="Active"/>); section 3 inserts a real
    /// device-key challenge here before any RPC is allowed.
    /// </summary>
    PoPPending,

    /// <summary>The session is authenticated and RPC dispatch is allowed.</summary>
    Active,

    /// <summary>The session is closing / has been torn down; no further transitions occur.</summary>
    Closed,
}
