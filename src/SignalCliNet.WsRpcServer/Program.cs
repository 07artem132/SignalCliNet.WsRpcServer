using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignalCli.Extensions;
using SignalCliNet.WsRpcServer.Extensions;
using WsRpcServer.Core;

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

                // R3.1 (add-group-claim-receive): безперервний авто-receive лише коли GroupClaim увімкнено —
                // це скасовує send-only передумову V2 і потрібне, щоб сканер матчив claim-коди у вхідному потоці.
                var groupClaimEnabled =
                    hostContext.Configuration.GetValue<bool>("Server:GroupClaim:Enabled");

                services.AddSignalCli(options => ApplySignalCliConfig(signalSection, options, groupClaimEnabled));

                services.AddSignalEvents();

                services.AddSignalJsonRpc(hostContext.Configuration, options =>
                    ApplyServerConfig(hostContext.Configuration.GetSection("Server"), options));
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
    /// Maps the "Server" configuration section onto <see cref="JsonRpcServerConfig"/> (Host/Port and the
    /// deploy message-size cap). Extracted from the host-builder lambda so the passthrough is unit-testable.
    /// </summary>
    /// <param name="serverSection">The "Server" configuration section.</param>
    /// <param name="options">The framework JSON-RPC server config to populate.</param>
    public static void ApplyServerConfig(IConfigurationSection serverSection, JsonRpcServerConfig options)
    {
        ArgumentNullException.ThrowIfNull(serverSection);
        ArgumentNullException.ThrowIfNull(options);

        var serverHost = serverSection["Host"];
        if (!string.IsNullOrWhiteSpace(serverHost))
            options.Host = serverHost;

        if (int.TryParse(serverSection["Port"], out var port))
            options.Port = port;

        // V3: дефолт деплою — 64KB (65536). Framework-дефолт 100MB завеликий для JSON-RPC-керівних
        // кадрів; менша стеля обмежує пам'ять на кадр. Задається у appsettings.json / Docker env.
        if (int.TryParse(serverSection["MaxMessageSizeBytes"], out var maxMessageSize))
            options.MaxMessageSizeBytes = maxMessageSize;

        // Browser interop (JSON-RPC.NET 2.8.0): WHATWG-браузерні WS-клієнти очікують ТЕКСТОВИЙ кадр
        // (event.data = string, а не Blob). Framework-дефолт — Binary; цей app обслуговує браузерні
        // клієнти, тож вмикаємо Text для вихідних JSON-RPC кадрів. Не-браузерні споживачі приймають обидва.
        options.UseTextFramesForOutgoingMessages = true;
    }

    /// <summary>
    /// Maps the "SignalCli" configuration section onto <see cref="SignalCli.Models.SignalCliOptions"/>.
    /// Extracted from the host-builder lambda so the key passthrough is unit-testable.
    /// </summary>
    /// <param name="signalSection">The "SignalCli" configuration section.</param>
    /// <param name="options">The signal-cli options to populate.</param>
    /// <param name="continuousReceive">
    /// When <c>true</c> (gated by <c>Server:GroupClaim:Enabled</c>, R3.1), starts the daemon in continuous
    /// auto-receive (<c>UseManualReceiveMode=false</c>) so the group-claim scanner sees incoming group chats.
    /// Default <c>false</c> keeps the library default (manual, send-only) — current behaviour unchanged.
    /// </param>
    public static void ApplySignalCliConfig(
        IConfigurationSection signalSection, SignalCli.Models.SignalCliOptions options,
        bool continuousReceive = false)
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

        // R3.1: авто-receive лише за прапорцем GroupClaim. Інакше лишаємо бібліотечний дефолт
        // (UseManualReceiveMode=true — send-only), тобто демон не ресивить за всі акаунти (Фаза-1 поведінка).
        if (continuousReceive)
            options.UseManualReceiveMode = false;
    }
}
