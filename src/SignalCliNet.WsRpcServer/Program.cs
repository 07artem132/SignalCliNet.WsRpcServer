using System.Diagnostics;
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
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                Console.WriteLine($"▶️ Start: {activity.DisplayName} | TraceId: {activity.TraceId}");
            },
            ActivityStopped = activity =>
            {
                Console.WriteLine($"⏹️ Stop:  {activity.DisplayName} | Duration: {activity.Duration}");
            }
        });

        // Create and configure the host
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                // Register core Signal CLI services
                services.AddSignalCli(config =>
                {
                    config.LibDirectory = "signal-cli/lib";
                });
                
                services.AddSignalEvents();
                services.AddLogging();
                // Add JSON-RPC services with configuration
                services.AddSignalJsonRpc(options =>
                {
                    // Load host/port from configuration
                    var rpcConfig = hostContext.Configuration.GetSection("SignalRpc");
                    //     options. = rpcConfig["Host"] ?? "0.0.0.0";
                    //       options.Port = int.Parse(rpcConfig["Port"] ?? "9000");
                });
            })
            .ConfigureLogging((hostContext, logging) =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                
                logging.SetMinimumLevel(LogLevel.Trace);
            })
            .Build();

        // Run the host
        await host.RunAsync();
    }
}