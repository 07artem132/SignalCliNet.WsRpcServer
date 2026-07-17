using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Extensions;

/// <summary>
/// Composition root for the group-claim receive core (add-group-claim-receive): the claim service +
/// confirm-seam, the per-identity rate limiter, the group member cache, the send-gate, the per-account
/// receive-router (S11) and the shared-bot scanner, plus validated <see cref="GroupClaimOptions"/>.
/// </summary>
/// <remarks>
/// Opt-in (<see cref="GroupClaimOptions.Enabled"/> = <c>false</c> default): усі сервіси реєструються, але
/// поводяться як no-op/passthrough, поки прапорець вимкнено (роутер → passthrough, gate/scanner inert,
/// receive-mode лишається manual). <c>TryAdd*</c> — ідемпотентно (mirrors AddAuthnCore/AddAdmissionCore).
/// </remarks>
public static class GroupClaimExtensions
{
    /// <summary>Registers the group-claim core services and their validated options.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (binds <c>Server:GroupClaim</c>).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddGroupClaimCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Group-claim опції (секція Server:GroupClaim), валідовані на старті ([Range] → source-gen validator).
        services.AddOptionsWithValidateOnStart<GroupClaimOptions>()
            .Bind(configuration.GetSection(GroupClaimOptions.SectionName));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<GroupClaimOptions>, GroupClaimOptionsValidator>());

        // Claim-сервіс (mint/confirm) + confirm-seam для сканера + per-identity request rate-limit.
        services.TryAddSingleton<GroupClaimRateLimiter>();
        services.TryAddSingleton<GroupClaimService>();
        services.TryAddSingleton<IGroupClaimConfirmer>(sp => sp.GetRequiredService<GroupClaimService>());

        // Member-cache (~60с TTL) + send-gate (binding + anchor-membership). Singletons.
        services.TryAddSingleton<GroupMemberCache>();
        services.TryAddSingleton<GroupSendGate>();

        // S11 per-account роутер + shared-bot сканер (non-blocking consumer). Singletons; активні лише
        // коли Enabled=true (роутер сам гейтить прапорцем; EventProcessor підписується ЧЕРЕЗ роутер).
        services.TryAddSingleton<PerAccountReceiveRouter>();
        services.TryAddSingleton<GroupClaimScanner>();

        // Системний час — TryAdd (ідемпотентно з AddAuthnCore/AddAdmissionCore).
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
