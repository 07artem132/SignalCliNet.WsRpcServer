using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Starts the mTLS admin JSON-RPC port (add-invites-admin, task 2.3) — a SEPARATE listener from the plain
/// token port. Builds the TLS transport from the on-volume server cert + CA (server-auth) and requires a
/// client cert (mTLS) validated against that CA. Registered AFTER <c>SecureDeploymentHostedService</c>, so
/// secrets are already provisioned and the admin credential is bootstrapped before this binds.
/// </summary>
/// <remarks>
/// Порт стартує ЛИШЕ якщо <see cref="AdminOptions.Enabled"/> і секрети (server-cert + CA) є на томі —
/// інакше лог і no-op (additive: без секретів admin-порту немає). D4-логіка: не-loopback bind лише зі
/// свідомим <see cref="AdminOptions.AllowNonLoopback"/> (TLS тут завжди є, бо mTLS).
///
/// Транспорт будуємо ТУТ (у StartAsync), а не через DI-singleton із <c>ValidateOnStart</c>, щоб уникнути
/// раннього читання cert-файлів ДО їх провіжну (порядок hosted-services гарантує, що секрети вже на місці).
/// </remarks>
public sealed class SecureAdminRpcHostedService : IHostedService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AdminOptions _adminOptions;
    private readonly string _dataDirectory;
    private readonly ILogger<SecureAdminRpcHostedService> _logger;

    private SecureAdminRpcServer? _server;

    public SecureAdminRpcHostedService(
        IServiceProvider serviceProvider,
        IOptions<AdminOptions> adminOptions,
        IOptions<PersistenceOptions> persistenceOptions,
        ILogger<SecureAdminRpcHostedService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _adminOptions = (adminOptions ?? throw new ArgumentNullException(nameof(adminOptions))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dataDirectory = SecureDeploymentHostedService.ResolveDataDirectory(
            (persistenceOptions ?? throw new ArgumentNullException(nameof(persistenceOptions))).Value.DataDirectory);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_adminOptions.Enabled)
        {
            _logger.LogInformation("mTLS admin-порт вимкнено (Server:Admin:Enabled=false) — не стартуємо.");
            return Task.CompletedTask;
        }

        var address = IPAddress.Parse(_adminOptions.Host);
        if (!IPAddress.IsLoopback(address) && !_adminOptions.AllowNonLoopback)
        {
            // D4-логіка: не-loopback bin лише зі свідомим opt-in (навіть попри mTLS) — fail-closed.
            _logger.LogWarning(
                "mTLS admin-порт: не-loopback bind {Host} без Server:Admin:AllowNonLoopback=true — пропуск (D4).",
                _adminOptions.Host);
            return Task.CompletedTask;
        }

        var secretsDir = Path.Combine(_dataDirectory, "secrets");
        var serverCertPath = Path.Combine(secretsDir, "server.crt");
        var serverKeyPath = Path.Combine(secretsDir, "server.key");
        var caCertPath = Path.Combine(secretsDir, "ca.crt");

        if (!File.Exists(serverCertPath) || !File.Exists(serverKeyPath) || !File.Exists(caCertPath))
        {
            // Секрети не провіжнено (напр. хост без AddSecureDeployment) → admin-порту немає (additive).
            _logger.LogWarning("mTLS admin-порт: секрети (server-cert/CA) відсутні на томі — пропуск.");
            return Task.CompletedTask;
        }

        try
        {
            var transport = BuildTransport(serverCertPath, serverKeyPath, caCertPath);
            _server = ActivatorUtilities.CreateInstance<SecureAdminRpcServer>(
                _serviceProvider, transport, address, _adminOptions.Port);

            if (_server.Start())
            {
                _logger.LogInformation(
                    "mTLS admin JSON-RPC порт стартував на {Host}:{Port} (client-cert обов'язковий).",
                    _adminOptions.Host, _adminOptions.Port);
            }
            else
            {
                _logger.LogError("Не вдалося стартувати mTLS admin-порт на {Host}:{Port}.",
                    _adminOptions.Host, _adminOptions.Port);
            }
        }
        catch (Exception ex)
        {
            // Admin-порт additive: його падіння НЕ валить основний процес (плейн-порт лишається робочим).
            _logger.LogError(ex, "mTLS admin-порт не піднявся — плейн-порт лишається робочим.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Stop();
        _logger.LogInformation("mTLS admin-порт зупинено.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _server?.Dispose();

    // Будує TLS-транспорт: server-cert (server-auth) з тому + приватний CA як trusted root для mTLS.
    private SecureTransport BuildTransport(string serverCertPath, string serverKeyPath, string caCertPath)
    {
        var tlsOptions = new TlsServerOptions
        {
            ServerCertificate = LoadServerCertificate(serverCertPath, serverKeyPath),
            ClientCertificateRequired = true,
            TrustedRoots = [X509Certificate2.CreateFromPem(File.ReadAllText(caCertPath))],
            // justification: наш авто-CA НЕ має CRL/OCSP-інфраструктури (leaf-и без CDP/AIA), тож будь-яка
            // перевірка відкликання (Offline теж) провалила б ланцюг як RevocationStatusUnknown і відхилила
            // б валідний admin-cert. Відкликання admin-креденшелу — durable-важіль (admin_credentials.revoked,
            // звіряється у AdminPrincipalResolver ПІСЛЯ побудови ланцюга), а не CRL. NoCheck тут свідомий
            // (task 2.3; той самий підхід, що ChainsToCa у SecretMaterialProvisioner).
            RevocationMode = X509RevocationMode.NoCheck,
            SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        };

        var validator = new CustomRootTrustValidator(
            tlsOptions.TrustedRoots,
            spkiPins: null,
            tlsOptions.RevocationMode,
            _serviceProvider.GetRequiredService<ILogger<CustomRootTrustValidator>>());
        var resolver = new SpiffeNodeIdentityResolver();

        return SecureTransport.Create(
            tlsOptions, validator, resolver, _serviceProvider.GetRequiredService<ILogger<SecureTransport>>());
    }

    // Завантажує server-cert із PEM-пари й реекспортує через PKCS#12, щоб приватний ключ був придатний
    // для server-auth у SslStream (на Windows ephemeral-ключ із PEM SChannel не приймає).
    private static X509Certificate2 LoadServerCertificate(string certPath, string keyPath)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }
}
