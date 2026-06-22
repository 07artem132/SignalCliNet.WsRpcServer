using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignalCli.Extensions;
using SignalCliNet.WsRpcServer.Extensions;

namespace SignalCliNet.WsRpcServer;

/// <summary>
/// Main entry point for the SignalCli WebSocket JSON-RPC server application
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                var signalSection = hostContext.Configuration.GetSection("SignalCli");

                services.AddSignalCli(options =>
                {
                    options.LibDirectory = signalSection["LibDirectory"] ?? "signal-cli/lib";

                    var appHome = signalSection["AppHome"];
                    options.AppHome = !string.IsNullOrWhiteSpace(appHome)
                        ? appHome
                        : AppContext.BaseDirectory;

                    var storagePath = signalSection["StoragePathCli"];
                    if (!string.IsNullOrWhiteSpace(storagePath))
                        options.StoragePathCli = storagePath;

                    var javaExecutable = signalSection["JavaExecutable"];
                    if (!string.IsNullOrWhiteSpace(javaExecutable))
                        options.JavaExecutable = javaExecutable;

                    if (int.TryParse(signalSection["MaxRestartAttempts"], out var maxRestart))
                        options.MaxRestartAttempts = maxRestart;

                    if (int.TryParse(signalSection["HealthCheckIntervalSeconds"], out var healthInterval))
                        options.HealthCheckIntervalSeconds = healthInterval;

                    if (int.TryParse(signalSection["HealthCheckTimeoutSeconds"], out var healthTimeout))
                        options.HealthCheckTimeoutSeconds = healthTimeout;
                });

                services.AddSignalEvents();

                services.AddSignalJsonRpc(options =>
                {
                    var serverSection = hostContext.Configuration.GetSection("Server");

                    var serverHost = serverSection["Host"];
                    if (!string.IsNullOrWhiteSpace(serverHost))
                        options.Host = serverHost;

                    if (int.TryParse(serverSection["Port"], out var port))
                        options.Port = port;
                });
            })
            .ConfigureLogging((hostContext, logging) =>
            {
                logging.ClearProviders();
                logging.AddConfiguration(hostContext.Configuration.GetSection("Logging"));
                logging.AddConsole();
            })
            .Build();

        await host.RunAsync();
    }
}
