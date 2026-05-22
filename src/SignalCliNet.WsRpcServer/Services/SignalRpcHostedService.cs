using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WsRpcServer.Core;
using WsRpcServer.Events;

namespace SignalCliNet.WsRpcServer.Services;

public class SignalRpcHostedService(
    IServiceProvider serviceProvider,
    IEventProcessor eventProcessor,
    ILogger<SignalRpcHostedService> logger,
    JsonRpcServerConfig config)
    : IHostedService, IDisposable
{
    private readonly SignalRpcServer _server = ActivatorUtilities.CreateInstance<SignalRpcServer>(
        serviceProvider,
        IPAddress.Parse(config.Host),
        config.Port);

    private readonly ILogger<SignalRpcHostedService>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IEventProcessor _eventProcessor =
        eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Signal JSON-RPC WebSocket server...");

        await _eventProcessor.StartAsync(cancellationToken);

        if (_server.Start())
        {
            _logger.LogInformation("Signal JSON-RPC WebSocket server started on {Address}:{Port}",
                _server.Address, _server.Port);
        }
        else
        {
            _logger.LogError("Failed to start Signal JSON-RPC WebSocket server");
            throw new InvalidOperationException("Failed to start WebSocket server");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Signal JSON-RPC WebSocket server...");

        await _eventProcessor.StopAsync(cancellationToken);

        _server.Stop();

        _logger.LogInformation("Signal JSON-RPC WebSocket server stopped");
    }

    public void Dispose()
    {
        _server.Dispose();
    }
}