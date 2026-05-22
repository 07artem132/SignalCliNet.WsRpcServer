using Microsoft.Extensions.DependencyInjection;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Subscriptions;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Extensions;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Extensions;

/// <summary>
/// Extension methods for registering Signal JSON-RPC services in the DI container
/// </summary>
public static class SignalRpcExtensions
{
    /// <summary>
    /// Adds Signal JSON-RPC server services to the service collection
    /// </summary>
    public static IServiceCollection AddSignalJsonRpc(
        this IServiceCollection services,
        Action<JsonRpcServerConfig>? configureOptions = null)
    {
        // Add core JSON-RPC services
        services.AddJsonRpcCore(configureOptions);

        // Override core services with Signal-specific implementations
        services.AddSingleton<ISubscriptionManager, SubscriptionManager>();
        services.AddSingleton<IEventProcessor, EventProcessor>();
        services.AddSingleton<IRpcServiceRegistry, RpcServiceRegistry>();

        // RPC adapters
        services.AddScoped<ISignalAccountsRpc, SignalAccountsRpcAdapter>();
        services.AddScoped<ISignalDevicesRpc, SignalDevicesRpcAdapter>();

        // Server hosted service
        services.AddHostedService<SignalRpcHostedService>();

        return services;
    }
}