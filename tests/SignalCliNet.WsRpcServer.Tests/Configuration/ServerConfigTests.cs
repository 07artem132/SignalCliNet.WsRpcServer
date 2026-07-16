using Microsoft.Extensions.Configuration;
using WsRpcServer.Core;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

/// <summary>
/// Пінить біндинг секції "Server" у <see cref="JsonRpcServerConfig"/> через
/// <see cref="Program.ApplyServerConfig"/> та V3-контракт: shipped <c>appsettings.json</c> МУСИТЬ
/// задавати <c>MaxMessageSizeBytes</c> = 64KB (framework-дефолт 100MB завеликий для JSON-RPC-кадрів).
/// </summary>
public class ServerConfigTests
{
    private const int SixtyFourKib = 65536;

    private static JsonRpcServerConfig Apply(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = new JsonRpcServerConfig();
        Program.ApplyServerConfig(config.GetSection("Server"), options);
        return options;
    }

    [Fact]
    public void ShippedAppSettings_MaxMessageSize_Is64Kib()
    {
        // Реальний appsettings.json застосунку (залінкований у вихідну теку) — V3-дефолт деплою.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        Assert.Equal(SixtyFourKib.ToString(), configuration["Server:MaxMessageSizeBytes"]);
    }

    [Fact]
    public void MaxMessageSizeBytes_IsAppliedFromConfiguration()
    {
        var options = Apply(new() { ["Server:MaxMessageSizeBytes"] = SixtyFourKib.ToString() });

        Assert.Equal(SixtyFourKib, options.MaxMessageSizeBytes);
    }

    [Fact]
    public void MaxMessageSizeBytes_WhenAbsent_KeepsFrameworkDefault()
    {
        var options = Apply([]);

        // Відсутній ключ не чіпає framework-дефолт (100MB не міняємо у бібліотеці).
        Assert.Equal(new JsonRpcServerConfig().MaxMessageSizeBytes, options.MaxMessageSizeBytes);
    }

    [Fact]
    public void HostAndPort_AreApplied()
    {
        var options = Apply(new()
        {
            ["Server:Host"] = "127.0.0.1",
            ["Server:Port"] = "9100",
        });

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(9100, options.Port);
    }
}
