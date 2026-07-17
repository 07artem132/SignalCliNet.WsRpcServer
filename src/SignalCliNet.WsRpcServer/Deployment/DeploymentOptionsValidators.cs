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
