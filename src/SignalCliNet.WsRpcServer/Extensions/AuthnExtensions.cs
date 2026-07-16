using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Extensions;

/// <summary>
/// Composition root for the authn core (token store + lifecycle + handshake): the pepper provider, the
/// token service, the system <see cref="TimeProvider"/>, the validated <see cref="AuthOptions"/> and the
/// per-connection admission/revocation singletons.
/// </summary>
public static class AuthnExtensions
{
    /// <summary>
    /// Registers the authn-core services. Builds on the durable store and persistence options wired by
    /// <c>AddSecureDeployment</c>; uses <c>TryAdd*</c> so it composes idempotently.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (binds <c>Server:Auth</c>).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthnCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Auth-опції (секція Server:Auth), валідовані на старті: межі — через [OptionsValidator]
        // ([Range]); per-element формат AllowedOrigins — крос-правилом .Validate(...).
        services.AddOptionsWithValidateOnStart<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(
                o => o.AllowedOrigins is null || o.AllowedOrigins.All(AuthOptions.IsValidOrigin),
                "Server:Auth:AllowedOrigins — кожен елемент має бути абсолютним origin " +
                "(scheme://host[:port]) без '/' в кінці.");
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>());

        // Пеппер-провайдер (лінива читка pepper_token з тому) + токен-сервіс — singletons.
        services.TryAddSingleton<IPepperProvider, FilePepperProvider>();
        services.TryAddSingleton<TokenService>();

        // Handshake-синглтони: per-IP cap неавтентифікованих сокетів + in-process revoke-фан-аут (1.4).
        services.TryAddSingleton<UnauthenticatedConnectionLimiter>();
        services.TryAddSingleton<RevocationBroadcaster>();

        // Системний час — інжектимо через TimeProvider (тести підміняють фіксованим).
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
