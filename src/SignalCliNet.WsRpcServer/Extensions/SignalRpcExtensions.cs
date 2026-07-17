using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Security;
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
        IConfiguration configuration,
        Action<JsonRpcServerConfig>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Add core JSON-RPC services
        services.AddJsonRpcCore(configureOptions);

        // Override core services with Signal-specific implementations
        services.AddSingleton<ISubscriptionManager<SignalEventTypes, BaseSignalEventArgs>, SubscriptionManager>();
        services.AddSingleton<IEventProcessor, EventProcessor>();
        services.AddSingleton<IRpcServiceRegistry, RpcServiceRegistry>();

        // Pre-dispatch authorization chokepoint: декларативний реєстр політик (default-deny),
        // незалежний від авто-дискавері. Singleton — таблиця незмінна на весь час життя процесу.
        services.AddSingleton<IRpcPolicyRegistry, SignalRpcPolicyRegistry>();

        // Link-session стор (add-link-sessions): in-memory one-time сесії лінкування пристрою
        // (256-bit SessionId, TTL 120с, rate-limit 3/хв/identity). Singleton — стан живе на весь процес;
        // TimeProvider бере з AddAuthnCore (реєструється нижче), тож порядок реєстрації байдужий (DI-lazy).
        services.AddSingleton<LinkSessionStore>();

        // Authn-core (token store + lifecycle + handshake): пеппер-провайдер, токен-сервіс, TimeProvider,
        // AuthOptions (Server:Auth), per-IP cap + revoke-фан-аут. Спирається на durable-стор і Persistence-
        // опції з AddSecureDeployment (реєструється у Program.cs).
        services.AddAuthnCore(configuration);

        // Admission-core (W16 ланцюг: global-pause → per-user quota → new-recipient → aggregate budget):
        // reserve-then-send бюджет, abuse-пеппер, AdmissionOptions (Server:Admission). Опт-ін (Enabled=false
        // за замовчуванням); спирається на той самий durable-стор. Реєструє й IAbusePepperProvider, тож
        // AbuseLogService (нижче) має домен-розділений pepper_abuse незалежно від Admission:Enabled.
        services.AddAdmissionCore(configuration);

        // Онбординг/інвайти (add-invites-admin): InviteService (mint/redeem), AbuseLogService (shared-bot
        // audit, W24) і soft global rate-cap редемпшну (task 1.2) — singletons (стан/лічильники живуть на
        // весь процес). TimeProvider/IDurableStore/IAbusePepperProvider беруться з AddAuthnCore/AddAdmissionCore.
        services.AddSingleton<InviteService>();
        services.AddSingleton<AbuseLogService>();
        // Явна фабрика: soft-cap за замовчуванням (не покладаємось на резолв default-значення ctor-параметра
        // контейнером). Дефолт-політику cap винесе секція 2 (конфіг), поки — DefaultMaxPerWindow.
        services.AddSingleton(sp => new InviteRedemptionRateLimiter(sp.GetRequiredService<TimeProvider>()));

        // Admin (add-invites-admin, секція 2): tamper-evident audit-trail (hash-chain, task 3.2), валідовані
        // AdminOptions (Server:Admin) + mTLS admin-порт. AuditLogService — singleton (єдиний записувач лога).
        services.AddSingleton<AuditLogService>();
        services.AddOptionsWithValidateOnStart<AdminOptions>()
            .Bind(configuration.GetSection(AdminOptions.SectionName))
            .ValidateDataAnnotations();

        // RPC adapters
        services.AddScoped<ISignalAccountsRpc, SignalAccountsRpcAdapter>();
        services.AddScoped<ISignalDevicesRpc, SignalDevicesRpcAdapter>();
        services.AddScoped<ISignalMessageRpc, SignalMessageRpcAdapter>();
        services.AddScoped<ISignalGroupsRpc, SignalGroupsRpcAdapter>();
        services.AddScoped<ISignalOnboardingRpc, SignalOnboardingRpcAdapter>();
        services.AddScoped<ISignalAdminRpc, SignalAdminRpcAdapter>();
        services.AddScoped<ISystemRpc, SystemRpcAdapter>();

        // Server hosted service (плейн token-порт)
        services.AddHostedService<SignalRpcHostedService>();

        // mTLS admin-порт (task 2.3) — ОКРЕМИЙ hosted-service. Реєструється ПІСЛЯ плейн-порту й після
        // AddSecureDeployment (Program.cs) — тож секрети провіжнено й admin-credential bootstrapped до bind-у.
        services.AddHostedService<SecureAdminRpcHostedService>();

        return services;
    }
}