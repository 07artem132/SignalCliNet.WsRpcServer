using Microsoft.Extensions.Options;

namespace SignalCliNet.WsRpcServer.Deployment;

// A3: source-gen [OptionsValidator] реалізації IValidateOptions<T> на основі DataAnnotations
// ([Required]) — без рефлексії (AOT-сумісно, як у JSON-RPC.NET JsonRpcServerConfigValidator).
// Крос-польові правила (D4) додаються через .Validate(...) у SecureDeploymentExtensions.

/// <summary>Compile-time validator for <see cref="PersistenceOptions"/> (DataAnnotations).</summary>
[OptionsValidator]
internal sealed partial class PersistenceOptionsValidator : IValidateOptions<PersistenceOptions>;

/// <summary>Compile-time validator for <see cref="TransportSecurityOptions"/> (DataAnnotations).</summary>
[OptionsValidator]
internal sealed partial class TransportSecurityOptionsValidator : IValidateOptions<TransportSecurityOptions>;

/// <summary>Compile-time validator for <see cref="AuthOptions"/> (DataAnnotations [Range] bounds).</summary>
// Межові правила (TtlHours 1..168, TimeoutSeconds 3..120, MaxUnauthenticatedPerIp 1..1024,
// RenewalPerIdentityPerHour 1..60) — через [Range] на властивостях. Per-element формат AllowedOrigins
// валідується .Validate(...) у AddAuthnCore.
[OptionsValidator]
internal sealed partial class AuthOptionsValidator : IValidateOptions<AuthOptions>;

/// <summary>Compile-time validator for <see cref="AdmissionOptions"/> (DataAnnotations [Range] bounds).</summary>
// Межові правила (AggregateBudgetPerWindow 1..100000, BudgetWindowMinutes 1..1440, PerUserFloorPerWindow
// 0..10000, NewRecipientsPerWindow 1..1000, ReserveBlockSize 1..1000) — через [Range]. Крос-правило
// PerUserFloorPerWindow ≤ AggregateBudgetPerWindow — .Validate(...) у AddAdmissionCore.
[OptionsValidator]
internal sealed partial class AdmissionOptionsValidator : IValidateOptions<AdmissionOptions>;

/// <summary>Compile-time validator for <see cref="GroupClaimOptions"/> (DataAnnotations [Range] bounds).</summary>
// Межові правила (ClaimTtlMinutes 1..1440, BindingTtlHours 1..8760, MaxActiveBindingsPerIdentity 1..1000,
// ClaimRequestsPerHourPerIdentity 1..1000, MemberCacheTtlSeconds 5..3600) — через [Range]. Крос-правил немає.
[OptionsValidator]
internal sealed partial class GroupClaimOptionsValidator : IValidateOptions<GroupClaimOptions>;
