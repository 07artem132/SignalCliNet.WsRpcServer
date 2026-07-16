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
