using System.Net;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

// Phase-1 безпека: Server:Host у shipped appsettings.json МУСИТЬ бути loopback (без 0.0.0.0 регресії).
public class BindLoopbackConfigTests
{
    [Fact]
    public void ShippedAppSettings_ServerHost_IsLoopbackAddress()
    {
        // Будуємо конфіг із реального appsettings.json застосунку (залінкований у вихідну теку).
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        var host = configuration["Server:Host"];
        Assert.False(string.IsNullOrWhiteSpace(host), "Server:Host має бути заданий у appsettings.json.");

        // Парсимо як IP-адресу й переконуємось, що це loopback (наприклад, 127.0.0.1).
        var address = IPAddress.Parse(host!);
        Assert.True(IPAddress.IsLoopback(address),
            $"Server:Host '{host}' має бути loopback-адресою (без bind на 0.0.0.0).");
    }
}
