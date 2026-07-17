using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Повне покриття чистої auth-стан-машини (<see cref="SessionAuthStateMachine"/>): happy-path token,
/// no-token→authenticate, невалідний/прострочений/revoked токен → 4401+reason, auth-таймаут → 4408,
/// revoke → 4401, а також disabled-шлях і термінальна ідемпотентність.
/// </summary>
public class SessionAuthStateMachineTests
{
    // ---------- Ініціалізація ----------

    [Fact]
    public void AuthDisabled_StartsDisabled()
    {
        var machine = new SessionAuthStateMachine(authEnabled: false);
        Assert.Equal(SessionAuthState.Disabled, machine.State);
    }

    [Fact]
    public void AuthEnabled_StartsAwaitingAuthenticate()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);
        Assert.Equal(SessionAuthState.AwaitingAuthenticate, machine.State);
    }

    // ---------- Happy path: валідний токен → PoPPending → успішний PoP → Active ----------

    [Fact]
    public void ValidToken_ThenPopSucceeded_ReachesActive()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);

        var afterToken = machine.OnTokenValidated(TokenAuthenticationStatus.Valid);
        Assert.False(afterToken.ShouldClose);
        Assert.Equal(SessionAuthState.PoPPending, machine.State);

        var afterPoP = machine.OnPopSucceeded();
        Assert.False(afterPoP.ShouldClose);
        Assert.Equal(SessionAuthState.Active, afterPoP.NextState);
        Assert.Equal(SessionAuthState.Active, machine.State);
    }

    // ---------- Провал PoP: PoPPending → 4401 pop-failed ----------

    [Fact]
    public void PopFailed_WhilePoPPending_Closes4401PopFailed()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);
        machine.OnTokenValidated(TokenAuthenticationStatus.Valid); // → PoPPending

        var decision = machine.OnPopFailed();

        Assert.True(decision.ShouldClose);
        Assert.Equal(AuthCloseCodes.TokenRejected, decision.CloseCode);
        Assert.Equal(4401, decision.CloseCode);
        Assert.Equal(AuthCloseReasons.PopFailed, decision.Reason);
        Assert.Equal("pop-failed", decision.Reason);
        Assert.Equal(SessionAuthState.Closed, machine.State);
    }

    [Fact]
    public void PopSucceeded_WhenNotPoPPending_IsNoOp()
    {
        // До входу в PoPPending успіх PoP нічого не робить (перший перехід виграє).
        var machine = new SessionAuthStateMachine(authEnabled: true);

        var decision = machine.OnPopSucceeded();

        Assert.False(decision.ShouldClose);
        Assert.Equal(SessionAuthState.AwaitingAuthenticate, machine.State);
    }

    [Fact]
    public void PopFailed_WhenNotPoPPending_IsNoOp()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);

        var decision = machine.OnPopFailed();

        Assert.False(decision.ShouldClose);
        Assert.Equal(SessionAuthState.AwaitingAuthenticate, machine.State);
    }

    // ---------- D8-пін: у PoPPending будь-що, крім pop.prove (в т.ч. повторний authenticate) → close ----------

    [Fact]
    public void UnknownMethod_WhilePoPPending_Closes4401Invalid()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);
        machine.OnTokenValidated(TokenAuthenticationStatus.Valid); // → PoPPending

        // Сесія маршрутизує лише pop.prove у HandlePopProve; будь-що інше йде сюди й закриває сокет.
        var decision = machine.OnUnknownPreAuthMethod();

        Assert.True(decision.ShouldClose);
        Assert.Equal(AuthCloseCodes.TokenRejected, decision.CloseCode);
        Assert.Equal(AuthCloseReasons.Invalid, decision.Reason);
        Assert.Equal(SessionAuthState.Closed, machine.State);
    }

    // ---------- Невалідний / прострочений / revoked токен → 4401 + машиночитний reason ----------

    [Theory]
    [InlineData(TokenAuthenticationStatus.Invalid, "invalid")]
    [InlineData(TokenAuthenticationStatus.Expired, "expired")]
    [InlineData(TokenAuthenticationStatus.Revoked, "revoked")]
    public void BadToken_Closes4401_WithMatchingReason(TokenAuthenticationStatus status, string expectedReason)
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);

        var decision = machine.OnTokenValidated(status);

        Assert.True(decision.ShouldClose);
        Assert.Equal(AuthCloseCodes.TokenRejected, decision.CloseCode);
        Assert.Equal(4401, decision.CloseCode);
        Assert.Equal(expectedReason, decision.Reason);
        Assert.Equal(SessionAuthState.Closed, machine.State);
    }

    // ---------- Pre-auth: не-authenticate метод → 4401 invalid ----------

    [Fact]
    public void UnknownPreAuthMethod_Closes4401Invalid()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);

        var decision = machine.OnUnknownPreAuthMethod();

        Assert.True(decision.ShouldClose);
        Assert.Equal(AuthCloseCodes.TokenRejected, decision.CloseCode);
        Assert.Equal(AuthCloseReasons.Invalid, decision.Reason);
    }

    // ---------- Auth-таймаут → 4408 auth-timeout ----------

    [Fact]
    public void Timeout_WhileAwaiting_Closes4408()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);

        var decision = machine.OnAuthTimeout();

        Assert.True(decision.ShouldClose);
        Assert.Equal(AuthCloseCodes.AuthTimeout, decision.CloseCode);
        Assert.Equal(4408, decision.CloseCode);
        Assert.Equal("auth-timeout", decision.Reason);
    }

    [Fact]
    public void Timeout_WhilePoPPending_Closes4408()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);
        machine.OnTokenValidated(TokenAuthenticationStatus.Valid); // → PoPPending

        var decision = machine.OnAuthTimeout();

        Assert.True(decision.ShouldClose);
        Assert.Equal(AuthCloseCodes.AuthTimeout, decision.CloseCode);
    }

    [Fact]
    public void Timeout_AfterActive_IsNoOp()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);
        machine.OnTokenValidated(TokenAuthenticationStatus.Valid);
        machine.OnPopSucceeded(); // → Active

        var decision = machine.OnAuthTimeout();

        Assert.False(decision.ShouldClose);
        Assert.Equal(SessionAuthState.Active, machine.State);
    }

    // ---------- Revoke живого конекту → 4401 revoked ----------

    [Fact]
    public void Revoke_WhileActive_Closes4401Revoked()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);
        machine.OnTokenValidated(TokenAuthenticationStatus.Valid);
        machine.OnPopSucceeded(); // → Active

        var decision = machine.OnRevoked();

        Assert.True(decision.ShouldClose);
        Assert.Equal(AuthCloseCodes.TokenRejected, decision.CloseCode);
        Assert.Equal(AuthCloseReasons.Revoked, decision.Reason);
        Assert.Equal(SessionAuthState.Closed, machine.State);
    }

    [Fact]
    public void Revoke_WhileNotActive_IsNoOp()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);

        var decision = machine.OnRevoked();

        Assert.False(decision.ShouldClose);
        Assert.Equal(SessionAuthState.AwaitingAuthenticate, machine.State);
    }

    // ---------- Disabled-шлях: token-події не змінюють стан ----------

    [Fact]
    public void Disabled_IgnoresTokenEvents()
    {
        var machine = new SessionAuthStateMachine(authEnabled: false);

        var decision = machine.OnTokenValidated(TokenAuthenticationStatus.Invalid);

        Assert.False(decision.ShouldClose);
        Assert.Equal(SessionAuthState.Disabled, machine.State);
    }

    // ---------- Термінальна ідемпотентність: перший перехід виграє ----------

    [Fact]
    public void AfterClose_FurtherEvents_AreNoOps()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);
        machine.OnTokenValidated(TokenAuthenticationStatus.Invalid); // → Closed (4401)

        // Гонка timeout ↔ authenticate тощо: подальші події вже не закривають повторно.
        Assert.False(machine.OnAuthTimeout().ShouldClose);
        Assert.False(machine.OnRevoked().ShouldClose);
        Assert.False(machine.OnTokenValidated(TokenAuthenticationStatus.Valid).ShouldClose);
        Assert.Equal(SessionAuthState.Closed, machine.State);
    }

    [Fact]
    public void OnClosed_IsIdempotentTerminal()
    {
        var machine = new SessionAuthStateMachine(authEnabled: true);
        machine.OnClosed();
        machine.OnClosed();

        Assert.Equal(SessionAuthState.Closed, machine.State);
    }
}
