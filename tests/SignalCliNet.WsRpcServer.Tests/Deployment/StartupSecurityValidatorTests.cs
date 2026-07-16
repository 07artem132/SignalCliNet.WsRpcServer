using SignalCliNet.WsRpcServer.Deployment;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Deployment;

/// <summary>
/// Пінить D4 fail-closed правило біндингу (<see cref="StartupSecurityValidator"/>): loopback завжди
/// дозволений; не-loopback — лише зі свідомим opt-in ТА сконфігурованим TLS.
/// </summary>
public class StartupSecurityValidatorTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void ValidateBinding_Loopback_IsAllowed_RegardlessOfFlags(string host)
    {
        var (ok, error) = StartupSecurityValidator.ValidateBinding(host, allowNonLoopback: false, tlsConfigured: false);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.10")]
    public void ValidateBinding_NonLoopback_WithoutOptIn_IsRefused(string host)
    {
        var (ok, error) = StartupSecurityValidator.ValidateBinding(host, allowNonLoopback: false, tlsConfigured: true);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateBinding_NonLoopback_OptInButNoTls_IsRefused()
    {
        var (ok, error) = StartupSecurityValidator.ValidateBinding("0.0.0.0", allowNonLoopback: true, tlsConfigured: false);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateBinding_NonLoopback_OptInAndTls_IsAllowed()
    {
        var (ok, error) = StartupSecurityValidator.ValidateBinding("0.0.0.0", allowNonLoopback: true, tlsConfigured: true);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateBinding_MissingHost_IsRefused(string? host)
    {
        var (ok, _) = StartupSecurityValidator.ValidateBinding(host, allowNonLoopback: true, tlsConfigured: true);

        Assert.False(ok);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("not-an-ip", false)]
    public void IsLoopbackBind_ClassifiesHost(string host, bool expected)
    {
        Assert.Equal(expected, StartupSecurityValidator.IsLoopbackBind(host));
    }
}
