using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Auto-generates and persists the deployment's secret material on first boot and reuses it on every
/// subsequent start (R3.4/R3.7): TLS CA + server cert + admin mTLS client bundle, plus the token and
/// abuse peppers. Never regenerates the CA (a fresh CA each boot breaks client trust); leaf certs are
/// re-issued by the SAME CA on expiry.
/// </summary>
/// <remarks>
/// Privacy: НІКОЛИ не логуємо матеріал ключів чи значення пепперів — лише шляхи, статус
/// (generated/reused) і дати дійсності. Секрети не йдуть у stdout/env/шари образу (R3.7).
/// </remarks>
public sealed class SecretMaterialProvisioner(ILogger<SecretMaterialProvisioner> logger)
{
    private const string SecretsSubdirectory = "secrets";
    private const int CaValidityYears = 10;
    private const int LeafValidityDays = 397;   // 13 місяців — типовий стель для leaf-cert
    private const int RenewalWindowDays = 30;   // переоформлюємо, якщо лишилось менше цього до простроки
    private const int PepperBytes = 32;         // 256-bit CSPRNG

    private readonly ILogger<SecretMaterialProvisioner> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private ProvisionedSecrets? _cached;

    /// <summary>
    /// Ensures all secret material exists under <c>&lt;dataDirectory&gt;/secrets</c> and returns the
    /// resolved paths. Idempotent (persist-once); safe to call repeatedly.
    /// </summary>
    /// <param name="dataDirectory">The data directory root (typically the mounted encrypted volume).</param>
    /// <param name="hostname">Optional internal hostname added to the server cert SAN.</param>
    /// <returns>The resolved on-disk paths of the provisioned secrets.</returns>
    public ProvisionedSecrets Provision(string dataDirectory, string? hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        if (_cached is not null)
            return _cached;

        var secretsDir = Path.Combine(dataDirectory, SecretsSubdirectory);
        Directory.CreateDirectory(secretsDir);
        FilePermissions.HardenDirectoryToOwnerOnly(secretsDir);

        var caCertPath = Path.Combine(secretsDir, "ca.crt");
        var caKeyPath = Path.Combine(secretsDir, "ca.key");
        var serverCertPath = Path.Combine(secretsDir, "server.crt");
        var serverKeyPath = Path.Combine(secretsDir, "server.key");
        var adminCertPath = Path.Combine(secretsDir, "admin-client.crt");
        var adminKeyPath = Path.Combine(secretsDir, "admin-client.key");
        var tokenPepperPath = Path.Combine(secretsDir, "pepper_token");
        var abusePepperPath = Path.Combine(secretsDir, "pepper_abuse");

        var now = DateTimeOffset.UtcNow;

        // 1. CA — persist-once, НІКОЛИ не перегенеровувати (fresh CA = зламаний client-trust).
        using var ca = EnsureCertificateAuthority(caCertPath, caKeyPath, now);

        // 2. Server (serverAuth) leaf — генеруємо або переоформлюємо при простроченні (2.3).
        EnsureLeafCertificate(
            serverCertPath, serverKeyPath, "server",
            () => CertificateFactory.IssueServerCertificate(ca, hostname, now.AddMinutes(-5), now.AddDays(LeafValidityDays)),
            now);

        // 3. Admin (clientAuth) mTLS-бандл — те саме, підписаний тим самим CA (S3-канон).
        EnsureLeafCertificate(
            adminCertPath, adminKeyPath, "admin-client",
            () => CertificateFactory.IssueAdminClientCertificate(ca, now.AddMinutes(-5), now.AddDays(LeafValidityDays)),
            now);

        // 4. Пеппери — persist-once (CSPRNG).
        EnsurePepper(tokenPepperPath, "token");
        EnsurePepper(abusePepperPath, "abuse");

        _cached = new ProvisionedSecrets
        {
            CaCertificatePath = caCertPath,
            CaKeyPath = caKeyPath,
            ServerCertificatePath = serverCertPath,
            ServerKeyPath = serverKeyPath,
            AdminClientCertificatePath = adminCertPath,
            AdminClientKeyPath = adminKeyPath,
            TokenPepperPath = tokenPepperPath,
            AbusePepperPath = abusePepperPath,
        };

        return _cached;
    }

    private X509Certificate2 EnsureCertificateAuthority(string certPath, string keyPath, DateTimeOffset now)
    {
        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            _logger.LogInformation("Переюз наявного авто-CA з тому (persist-once).");
            return X509Certificate2.CreateFromPemFile(certPath, keyPath);
        }

        _logger.LogInformation("Генерація нового авто-CA (first-boot; термін {Years} р.).", CaValidityYears);
        var ca = CertificateFactory.CreateCertificateAuthority(now.AddMinutes(-5), now.AddYears(CaValidityYears));
        try
        {
            WritePemPair(certPath, keyPath, ca);
        }
        catch
        {
            ca.Dispose();
            throw;
        }

        return ca;
    }

    private void EnsureLeafCertificate(
        string certPath, string keyPath, string name, Func<X509Certificate2> issue, DateTimeOffset now)
    {
        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            using var existing = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            var notAfter = existing.NotAfter.ToUniversalTime();

            if (notAfter > now.UtcDateTime.AddDays(RenewalWindowDays))
            {
                _logger.LogInformation(
                    "Переюз наявного cert '{Name}' (дійсний до {NotAfter:yyyy-MM-dd}).", name, notAfter);
                return;
            }

            _logger.LogInformation(
                "Cert '{Name}' прострочений/близький до простроки ({NotAfter:yyyy-MM-dd}) — переоформлення тим самим CA.",
                name, notAfter);
        }
        else
        {
            _logger.LogInformation("Генерація cert '{Name}' (first-boot).", name);
        }

        using var leaf = issue();
        WritePemPair(certPath, keyPath, leaf);
    }

    private void EnsurePepper(string path, string name)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            _logger.LogInformation("Переюз наявного пеппера '{Name}' (persist-once).", name);
            return;
        }

        // CSPRNG 256-bit → base64. НІКОЛИ не логуємо саме значення.
        var pepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PepperBytes));
        File.WriteAllText(path, pepper);
        FilePermissions.HardenFileToOwnerOnly(path);
        _logger.LogInformation("Згенеровано пеппер '{Name}' (CSPRNG {Bits}-bit).", name, PepperBytes * 8);
    }

    // Записує cert (PEM, public) + приватний ключ (PEM, 0600) поруч.
    private static void WritePemPair(string certPath, string keyPath, X509Certificate2 cert)
    {
        File.WriteAllText(certPath, cert.ExportCertificatePem());

        using var privateKey = cert.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("Сертифікат не містить приватного ключа для експорту.");
        File.WriteAllText(keyPath, privateKey.ExportPkcs8PrivateKeyPem());
        FilePermissions.HardenFileToOwnerOnly(keyPath);
    }
}
