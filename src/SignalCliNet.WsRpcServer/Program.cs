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
                // Секюр-деплой/durable-фундамент (G1 lock, D4 fail-closed, auto-gen секретів, стор).
                // Реєструємо ПЕРШИМ — його hosted-service стартує раніше за signal-cli-демон і RPC-транспорт:
                // спершу захоплюємо lock на data-dir і валідуємо bind, лише потім хтось торкається тому.
                services.AddSecureDeployment(hostContext.Configuration);

                var signalSection = hostContext.Configuration.GetSection("SignalCli");

                services.AddSignalCli(options => ApplySignalCliConfig(signalSection, options));

                services.AddSignalEvents();

                services.AddSignalJsonRpc(hostContext.Configuration, options =>
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

    /// <summary>
    /// Maps the "SignalCli" configuration section onto <see cref="SignalCli.Models.SignalCliOptions"/>.
    /// Extracted from the host-builder lambda so the key passthrough is unit-testable.
    /// </summary>
    public static void ApplySignalCliConfig(
        IConfigurationSection signalSection, SignalCli.Models.SignalCliOptions options)
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

        // finishLink чекає на скан QR користувачем — дефолтних 30с замало
        // (D16+W9: TTL link-сесії 120с; per-call timeout — питання до upstream).
        if (int.TryParse(signalSection["RequestTimeoutSeconds"], out var requestTimeout))
            options.RequestTimeoutSeconds = requestTimeout;
    }
}
