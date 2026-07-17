using SignalCliNet.WsRpcServer.Deployment;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

/// <summary>
/// Пінить валідацію <see cref="AuthOptions"/> (section 2): межі числових параметрів через згенерований
/// <see cref="AuthOptionsValidator"/> ([Range]) і формат-правило <see cref="AuthOptions.IsValidOrigin"/>,
/// яке AddAuthnCore навішує на AllowedOrigins.
/// </summary>
public class AuthOptionsValidatorTests
{
    private static AuthOptions Valid() => new()
    {
        Enabled = true,
        TokenTtlHours = 12,
        AuthTimeoutSeconds = 10,
        MaxUnauthenticatedPerIp = 8,
        RenewalPerIdentityPerHour = 4,
        AllowedOrigins = [],
    };

    [Fact]
    public void Defaults_AreValid()
    {
        var result = new AuthOptionsValidator().Validate(null, Valid());
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(169)]
    public void TokenTtlHours_OutOfRange_Fails(int hours)
    {
        var options = Valid();
        options.TokenTtlHours = hours;

        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(168)]
    public void TokenTtlHours_Boundaries_AreValid(int hours)
    {
        var options = Valid();
        options.TokenTtlHours = hours;

        Assert.True(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(121)]
    public void AuthTimeoutSeconds_OutOfRange_Fails(int seconds)
    {
        var options = Valid();
        options.AuthTimeoutSeconds = seconds;

        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public void MaxUnauthenticatedPerIp_OutOfRange_Fails(int max)
    {
        var options = Valid();
        options.MaxUnauthenticatedPerIp = max;

        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void RenewalPerIdentityPerHour_OutOfRange_Fails(int perHour)
    {
        var options = Valid();
        options.RenewalPerIdentityPerHour = perHour;

        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void RenewalPerIdentityPerHour_Boundaries_AreValid(int perHour)
    {
        var options = Valid();
        options.RenewalPerIdentityPerHour = perHour;

        Assert.True(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(120)]
    public void AuthTimeoutSeconds_Boundaries_AreValid(int seconds)
    {
        var options = Valid();
        options.AuthTimeoutSeconds = seconds;

        Assert.True(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnop")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com:8443")]
    [InlineData("http://localhost:3000")]
    public void IsValidOrigin_AcceptsWellFormedOrigins(string origin)
    {
        Assert.True(AuthOptions.IsValidOrigin(origin));
    }

    [Theory]
    [InlineData("https://example.com/")]      // trailing slash
    [InlineData("https://example.com/path")]  // has path
    [InlineData("example.com")]               // no scheme
    [InlineData("https://")]                  // empty authority
    [InlineData("https://has space")]         // whitespace in authority
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValidOrigin_RejectsMalformedOrigins(string? origin)
    {
        Assert.False(AuthOptions.IsValidOrigin(origin));
    }
}
