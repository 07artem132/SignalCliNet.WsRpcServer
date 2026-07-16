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
    /// A token has authenticated; a proof-of-possession challenge is outstanding. The server has sent a
    /// <c>pop.challenge</c> and awaits a <c>pop.prove</c> whose device-key signature it verifies before
    /// activating. No RPC is allowed in this state (D8); only <c>pop.prove</c> is accepted, and the auth
    /// timeout (<c>4408</c>) bounds the wait. Also used for T5 token renewal (expired token + live device
    /// key), where a successful proof rotates the token instead of merely activating.
    /// </summary>
    PoPPending,

    /// <summary>The session is authenticated and RPC dispatch is allowed.</summary>
    Active,

    /// <summary>The session is closing / has been torn down; no further transitions occur.</summary>
    Closed,
}
