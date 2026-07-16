using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SignalCliNet.WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Extensions;

/// <summary>
/// Composition root for the authn core (token store + lifecycle): the pepper provider, the token
/// service and the system <see cref="TimeProvider"/>.
/// </summary>
public static class AuthnExtensions
{
    /// <summary>
    /// Registers the authn-core services. Builds on the durable store and persistence options wired by
    /// <c>AddSecureDeployment</c>; uses <c>TryAdd*</c> so it composes idempotently.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthnCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Пеппер-провайдер (лінива читка pepper_token з тому) + токен-сервіс — singletons.
        services.TryAddSingleton<IPepperProvider, FilePepperProvider>();
        services.TryAddSingleton<TokenService>();

        // Системний час — інжектимо через TimeProvider (тести підміняють фіксованим).
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
