using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// <see cref="IPepperProvider"/> backed by the on-volume token pepper file
/// (<c>&lt;dataDir&gt;/secrets/pepper_token</c>, base64) written by <c>SecretMaterialProvisioner</c>.
/// </summary>
/// <remarks>
/// Читаємо ЛІНИВО при першому <see cref="GetPepper"/>: провіжн секретів відбувається у стартовому
/// hosted-сервісі до старту транспорту, тож на момент першої автентифікації файл уже на місці.
/// Розкодований байт-масив кешується. Значення пеппера НІКОЛИ не логується.
/// </remarks>
public sealed class FilePepperProvider : IPepperProvider
{
    private readonly string _pepperPath;
    private readonly object _sync = new();
    private byte[]? _cached;

    /// <summary>Creates the provider resolving the pepper path from persistence options.</summary>
    /// <param name="persistenceOptions">Persistence options carrying the data directory.</param>
    public FilePepperProvider(IOptions<PersistenceOptions> persistenceOptions)
    {
        ArgumentNullException.ThrowIfNull(persistenceOptions);

        var dataDirectory = SecureDeploymentHostedService.ResolveDataDirectory(
            persistenceOptions.Value.DataDirectory);
        _pepperPath = Path.Combine(dataDirectory, "secrets", "pepper_token");
    }

    /// <inheritdoc />
    public int CurrentVersion => 1;

    /// <inheritdoc />
    public byte[] GetPepper(int version)
    {
        if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Невідома версія пеппера: {version} (підтримується лише {CurrentVersion}).");
        }

        lock (_sync)
        {
            if (_cached is not null)
                return _cached;

            // pepper_token — base64 (пише SecretMaterialProvisioner). Trim на випадок трейлінг-нового рядка.
            var base64 = File.ReadAllText(_pepperPath).Trim();
            _cached = Convert.FromBase64String(base64);
            return _cached;
        }
    }
}
