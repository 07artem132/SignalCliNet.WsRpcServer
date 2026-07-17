using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;

namespace SignalCliNet.WsRpcServer.Extensions;

/// <summary>
/// Composition root for the admission core (W16 chain: global-pause gate, per-user quota, new-recipient
/// tracker and the reserve-then-send budget) plus its abuse pepper and validated
/// <see cref="AdmissionOptions"/>.
/// </summary>
public static class AdmissionExtensions
{
    /// <summary>
    /// Registers the admission-core services. Builds on the durable store and persistence options wired by
    /// <c>AddSecureDeployment</c>; uses <c>TryAdd*</c> so it composes idempotently (mirrors <c>AddAuthnCore</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (binds <c>Server:Admission</c>).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAdmissionCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Admission-опції (секція Server:Admission), валідовані на старті: межі — через [OptionsValidator]
        // ([Range]); крос-правило floor ≤ aggregate — .Validate(...).
        services.AddOptionsWithValidateOnStart<AdmissionOptions>()
            .Bind(configuration.GetSection(AdmissionOptions.SectionName))
            .Validate(
                o => o.PerUserFloorPerWindow <= o.AggregateBudgetPerWindow,
                "Server:Admission:PerUserFloorPerWindow має бути ≤ AggregateBudgetPerWindow (калібровка 1.1).");
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AdmissionOptions>, AdmissionOptionsValidator>());

        // Домен-розділений abuse-пеппер (лінива читка pepper_abuse з тому) — окремий ключ від token-пеппера.
        services.TryAddSingleton<IAbusePepperProvider, AbusePepperProvider>();

        // Global-pause заглушка (1.4): no-op до Phase-3 add-ops-observability.
        services.TryAddSingleton<IGlobalPauseGate, NoopGlobalPauseGate>();

        // Лімітери W16-ланцюга + фасад — singletons (атомарний in-memory стан під локом).
        services.TryAddSingleton<ReserveThenSendBudget>();
        services.TryAddSingleton<PerUserQuota>();
        services.TryAddSingleton<NewRecipientTracker>();
        services.TryAddSingleton<AdmissionControl>();

        // Транспортні ліміти (task 3.2/3.3): per-identity conn cap + server-wide signal-cli гейт (G6).
        // Обидва — singletons; активні лише коли Enabled=true (сесія/chokepoint передають їх лише тоді).
        services.TryAddSingleton<IdentityConnectionLimiter>();
        services.TryAddSingleton<SignalCliGate>();

        // Системний час (монотонний для вікон, wall-clock для міток). TryAdd — ідемпотентно з AddAuthnCore.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
