namespace SignalCliNet.WsRpcServer.Security;

/// <summary>WebSocket close codes used by the auth handshake.</summary>
/// <remarks>
/// <c>4401</c> — зарезервований під token-відмови T4 (invalid/expired/revoked): синтаксично валідний,
/// але непридатний токен закриває сокет in-band, щоб браузер відрізнив re-auth від мережевого фейлу.
/// <c>4408</c> — окремий код для auth-таймауту (сесія не стала Active за відведений час), щоб не
/// плутати таймаут із token-відмовою.
/// </remarks>
public static class AuthCloseCodes
{
    /// <summary>Token rejected (invalid / expired / revoked) — in-band auth failure (T4).</summary>
    public const int TokenRejected = 4401;

    /// <summary>Authentication handshake timed out before the session became active.</summary>
    public const int AuthTimeout = 4408;
}

/// <summary>Machine-readable close reasons carried in the close-frame payload (single word).</summary>
public static class AuthCloseReasons
{
    /// <summary>Token malformed, unknown, or its identity is absent.</summary>
    public const string Invalid = "invalid";

    /// <summary>Token present and not revoked but past its expiry.</summary>
    public const string Expired = "expired";

    /// <summary>Token (or its identity) has been revoked.</summary>
    public const string Revoked = "revoked";

    /// <summary>The session did not authenticate within the auth timeout.</summary>
    public const string AuthTimeout = "auth-timeout";
}

/// <summary>
/// The outcome of an auth-state transition: the next state plus, when the session must close, the WS
/// close code and machine-readable reason.
/// </summary>
/// <param name="NextState">The state after the transition.</param>
/// <param name="CloseCode">The WS close code, or 0 when the session should not close.</param>
/// <param name="Reason">The close reason word, or <c>null</c> when the session should not close.</param>
public readonly record struct AuthDecision(SessionAuthState NextState, int CloseCode, string? Reason)
{
    /// <summary>Whether this decision closes the session.</summary>
    public bool ShouldClose => CloseCode != 0;

    /// <summary>A non-closing transition to <paramref name="state"/>.</summary>
    public static AuthDecision To(SessionAuthState state) => new(state, 0, null);
}

/// <summary>
/// Pure, NetCoreServer-independent auth-handshake transition logic — the testable core of the session
/// state machine. The session serialises access (it is not internally locked) and executes the returned
/// <see cref="AuthDecision"/> (activate / close-with-code) against the transport.
/// </summary>
public sealed class SessionAuthStateMachine
{
    /// <summary>Creates the machine in its initial state for the given auth posture.</summary>
    /// <param name="authEnabled">
    /// <c>false</c> ⇒ start <see cref="SessionAuthState.Disabled"/> (loopback full-access);
    /// <c>true</c> ⇒ start <see cref="SessionAuthState.AwaitingAuthenticate"/> until a token resolves.
    /// </param>
    public SessionAuthStateMachine(bool authEnabled) =>
        State = authEnabled ? SessionAuthState.AwaitingAuthenticate : SessionAuthState.Disabled;

    /// <summary>The current auth state.</summary>
    public SessionAuthState State { get; private set; }

    /// <summary>
    /// Resolves a presented token (via subprotocol on upgrade OR via the fallback <c>authenticate</c>).
    /// <c>Valid</c> ⇒ advance to <see cref="SessionAuthState.PoPPending"/>; otherwise close <c>4401</c>
    /// with the matching reason. A no-op once already terminal/active.
    /// </summary>
    public AuthDecision OnTokenValidated(TokenAuthenticationStatus status)
    {
        if (IsTerminalOrActive())
            return AuthDecision.To(State);

        switch (status)
        {
            case TokenAuthenticationStatus.Valid:
                State = SessionAuthState.PoPPending;
                return AuthDecision.To(State);
            case TokenAuthenticationStatus.Expired:
                return Close(AuthCloseCodes.TokenRejected, AuthCloseReasons.Expired);
            case TokenAuthenticationStatus.Revoked:
                return Close(AuthCloseCodes.TokenRejected, AuthCloseReasons.Revoked);
            default:
                return Close(AuthCloseCodes.TokenRejected, AuthCloseReasons.Invalid);
        }
    }

    /// <summary>
    /// Completes the (section-2 transient) PoP step: <see cref="SessionAuthState.PoPPending"/> ⇒
    /// <see cref="SessionAuthState.Active"/>. Section 3 replaces this with a real challenge.
    /// </summary>
    public AuthDecision CompletePoP()
    {
        if (State != SessionAuthState.PoPPending)
            return AuthDecision.To(State);

        State = SessionAuthState.Active;
        return AuthDecision.To(State);
    }

    /// <summary>
    /// A pre-auth message whose method is not <c>authenticate</c> ⇒ close <c>4401</c> <c>invalid</c>.
    /// </summary>
    public AuthDecision OnUnknownPreAuthMethod()
    {
        if (IsTerminalOrActive())
            return AuthDecision.To(State);

        return Close(AuthCloseCodes.TokenRejected, AuthCloseReasons.Invalid);
    }

    /// <summary>
    /// The auth timer elapsed: if the session never reached Active ⇒ close <c>4408</c>
    /// <c>auth-timeout</c>; otherwise a no-op.
    /// </summary>
    public AuthDecision OnAuthTimeout()
    {
        if (State is SessionAuthState.AwaitingAuthenticate or SessionAuthState.PoPPending)
            return Close(AuthCloseCodes.AuthTimeout, AuthCloseReasons.AuthTimeout);

        return AuthDecision.To(State);
    }

    /// <summary>
    /// The session's identity was revoked in-process: if active ⇒ close <c>4401</c> <c>revoked</c>;
    /// otherwise a no-op.
    /// </summary>
    public AuthDecision OnRevoked()
    {
        if (State == SessionAuthState.Active)
            return Close(AuthCloseCodes.TokenRejected, AuthCloseReasons.Revoked);

        return AuthDecision.To(State);
    }

    /// <summary>Marks the session terminal (teardown). Idempotent.</summary>
    public void OnClosed() => State = SessionAuthState.Closed;

    // Термінальні/активні стани не реагують на token/pre-auth-події (гонки timeout↔authenticate,
    // повторні повідомлення тощо розв'язуються тим, що перший перехід виграє).
    private bool IsTerminalOrActive() =>
        State is SessionAuthState.Closed or SessionAuthState.Active or SessionAuthState.Disabled;

    private AuthDecision Close(int code, string reason)
    {
        State = SessionAuthState.Closed;
        return new AuthDecision(SessionAuthState.Closed, code, reason);
    }
}
