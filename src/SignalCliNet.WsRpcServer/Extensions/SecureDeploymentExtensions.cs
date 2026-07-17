using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Extensions;

/// <summary>
/// Composition root for the secure-deployment / durable-persistence foundation: validated startup
/// options (A3), the D4 fail-closed bind rule, the G1 single-instance lock and (in later sections)
/// the auto-generated secret material and the durable store.
/// </summary>
public static class SecureDeploymentExtensions
{
    /// <summary>
    /// Registers the secure-deployment services and their fail-fast option validation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (binds <c>Persistence</c> / <c>Server</c>).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSecureDeployment(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Persistence-опції: шлях до каталогу даних (валідований на старті).
        services.AddOptionsWithValidateOnStart<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PersistenceOptions>, PersistenceOptionsValidator>());

        // Transport-security опції + крос-польове D4-правило (не-loopback → потрібні AllowNonLoopback + TLS).
        services.AddOptionsWithValidateOnStart<TransportSecurityOptions>()
            .Bind(configuration.GetSection(TransportSecurityOptions.SectionName))
            .Validate(
                o => StartupSecurityValidator.ValidateBinding(o.Host, o.AllowNonLoopback, o.Tls.Enabled).Ok,
                "D4 fail-closed: не-loopback bind заборонений без Server:AllowNonLoopback=true та " +
                "сконфігурованого TLS (Server:Tls:Enabled=true).");
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<TransportSecurityOptions>, TransportSecurityOptionsValidator>());

        // Auto-gen секретів (CA/cert/пеппери) — singleton, викликається зі стартового сервісу.
        services.TryAddSingleton<SecretMaterialProvisioner>();

        // Admin bootstrap (add-invites-admin, task 2.2): ідемпотентна admin-identity + SPKI-mapping
        // admin-cert-а. Викликається стартовим сервісом після init стору й провіжну секретів.
        services.TryAddSingleton<AdminBootstrapService>();

        // Durable-стор (SQLite на томі) — один concrete-singleton у двох ролях: стартовий сервіс
        // тримає concrete (для Initialize: integrity-check + міграції), консюмери — IDurableStore
        // (тільки дані). ServiceProvider диспоузить його при завершенні хоста.
        services.TryAddSingleton(sp =>
        {
            var dataDirectory = SecureDeploymentHostedService.ResolveDataDirectory(
                sp.GetRequiredService<IOptions<PersistenceOptions>>().Value.DataDirectory);
            var databasePath = Path.Combine(dataDirectory, "durable.db");
            return new DurableStore(databasePath, sp.GetRequiredService<ILogger<DurableStore>>());
        });
        services.TryAddSingleton<IDurableStore>(sp => sp.GetRequiredService<DurableStore>());

        // Стартовий сервіс: D4-assertion + G1-lock + провіжн секретів + init durable-стору
        // (реєструється ДО RPC-hosted-service, тож стартує першим).
        services.AddHostedService<SecureDeploymentHostedService>();

        return services;
    }
}
