using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Supplies the domain-separated <c>abuse</c> pepper (HMAC key) used to hash recipient identifiers for
/// the new-recipient tracker. Deliberately a SEPARATE key from the token pepper (<see cref="IPepperProvider"/>):
/// recipient-hash and token-hash MUST NOT share key material (domain separation).
/// </summary>
/// <remarks>
/// Значення пеппера — секрет: реалізації НІКОЛИ не логують його і не віддають у stdout/env.
/// </remarks>
public interface IAbusePepperProvider
{
    /// <summary>Returns the abuse pepper key bytes.</summary>
    byte[] GetPepper();
}

/// <summary>
/// <see cref="IAbusePepperProvider"/> backed by the on-volume abuse pepper file
/// (<c>&lt;dataDir&gt;/secrets/pepper_abuse</c>, base64) written by <c>SecretMaterialProvisioner</c>.
/// </summary>
/// <remarks>
/// Той самий lazy+cache патерн, що й <see cref="FilePepperProvider"/> (token-домен): читаємо ЛІНИВО при
/// першому <see cref="GetPepper"/> — провіжн секретів відбувається у стартовому hosted-сервісі до старту
/// транспорту, тож на момент першого admission-виклику файл уже на томі. Значення НІКОЛИ не логується.
/// </remarks>
public sealed class AbusePepperProvider : IAbusePepperProvider
{
    private readonly string _pepperPath;
    private readonly object _sync = new();
    private byte[]? _cached;

    /// <summary>Creates the provider resolving the pepper path from persistence options.</summary>
    /// <param name="persistenceOptions">Persistence options carrying the data directory.</param>
    public AbusePepperProvider(IOptions<PersistenceOptions> persistenceOptions)
    {
        ArgumentNullException.ThrowIfNull(persistenceOptions);

        var dataDirectory = SecureDeploymentHostedService.ResolveDataDirectory(
            persistenceOptions.Value.DataDirectory);
        _pepperPath = Path.Combine(dataDirectory, "secrets", "pepper_abuse");
    }

    /// <inheritdoc />
    public byte[] GetPepper()
    {
        lock (_sync)
        {
            if (_cached is not null)
                return _cached;

            // pepper_abuse — base64 (пише SecretMaterialProvisioner). Trim на випадок трейлінг-нового рядка.
            var base64 = File.ReadAllText(_pepperPath).Trim();
            _cached = Convert.FromBase64String(base64);
            return _cached;
        }
    }
}
