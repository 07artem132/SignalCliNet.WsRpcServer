using Microsoft.Extensions.Configuration;
using SignalCli.Models;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

/// <summary>
/// Пінить passthrough конфіг-ключів секції "SignalCli" у <see cref="SignalCliOptions"/>
/// через <see cref="Program.ApplySignalCliConfig"/>.
/// </summary>
/// <remarks>
/// Регресія 2026-07-16: <c>RequestTimeoutSeconds</c> не було у whitelist —
/// <c>SignalCli__RequestTimeoutSeconds=300</c> у Docker мовчки ігнорувався, і
/// <c>finishLink</c> (який чекає на скан QR користувачем) падав на дефолтних 30с
/// (D16+W9). Whitelist-підхід означає: кожен новий ключ треба явно додати і
/// сюди, і у Program.cs — цей тест ловить розсинхрон для критичних ключів.
/// </remarks>
public class SignalCliConfigPassthroughTests
{
    private static SignalCliOptions Apply(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = new SignalCliOptions();
        Program.ApplySignalCliConfig(config.GetSection("SignalCli"), options);
        return options;
    }

    [Fact]
    public void RequestTimeoutSeconds_IsAppliedFromConfiguration()
    {
        var options = Apply(new() { ["SignalCli:RequestTimeoutSeconds"] = "300" });

        Assert.Equal(300, options.RequestTimeoutSeconds);
    }

    [Fact]
    public void RequestTimeoutSeconds_WhenAbsent_KeepsLibraryDefault()
    {
        var options = Apply([]);

        Assert.Equal(new SignalCliOptions().RequestTimeoutSeconds, options.RequestTimeoutSeconds);
    }

    [Fact]
    public void CoreKeys_AreApplied()
    {
        var options = Apply(new()
        {
            ["SignalCli:StoragePathCli"] = "/data/store",
            ["SignalCli:JavaExecutable"] = "/opt/jdk25/bin/java",
            ["SignalCli:MaxRestartAttempts"] = "5",
            ["SignalCli:HealthCheckIntervalSeconds"] = "77",
            ["SignalCli:HealthCheckTimeoutSeconds"] = "11",
        });

        Assert.Equal("/data/store", options.StoragePathCli);
        Assert.Equal("/opt/jdk25/bin/java", options.JavaExecutable);
        Assert.Equal(5, options.MaxRestartAttempts);
        Assert.Equal(77, options.HealthCheckIntervalSeconds);
        Assert.Equal(11, options.HealthCheckTimeoutSeconds);
    }
}
