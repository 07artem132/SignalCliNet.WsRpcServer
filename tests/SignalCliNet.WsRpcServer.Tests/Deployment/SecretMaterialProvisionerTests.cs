using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Deployment;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Deployment;

/// <summary>
/// Пінить auto-gen секретів (R3.4/R3.7): first-boot генерує CA/cert/пеппери; рестарт переюзає їх
/// (persist-once, CA НІКОЛИ не перегенеровується); прострочений leaf переоформлюється тим самим CA.
/// </summary>
public class SecretMaterialProvisionerTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-secret-tests-" + Guid.NewGuid().ToString("N"));

    private static SecretMaterialProvisioner NewProvisioner() =>
        new(NullLogger<SecretMaterialProvisioner>.Instance);

    [Fact]
    public void Provision_CreatesAllSecretFiles()
    {
        var secrets = NewProvisioner().Provision(_dataDir, hostname: null);

        Assert.True(File.Exists(secrets.CaCertificatePath));
        Assert.True(File.Exists(secrets.CaKeyPath));
        Assert.True(File.Exists(secrets.ServerCertificatePath));
        Assert.True(File.Exists(secrets.ServerKeyPath));
        Assert.True(File.Exists(secrets.AdminClientCertificatePath));
        Assert.True(File.Exists(secrets.AdminClientKeyPath));
        Assert.True(File.Exists(secrets.TokenPepperPath));
        Assert.True(File.Exists(secrets.AbusePepperPath));
    }

    [Fact]
    public void Provision_OnRestart_ReusesCaAndPeppers()
    {
        var first = NewProvisioner().Provision(_dataDir, hostname: null);
        var caBefore = File.ReadAllText(first.CaCertificatePath);
        var caKeyBefore = File.ReadAllText(first.CaKeyPath);
        var tokenBefore = File.ReadAllText(first.TokenPepperPath);
        var abuseBefore = File.ReadAllText(first.AbusePepperPath);

        // «Рестарт»: свіжий provisioner на тому ж каталозі.
        var second = NewProvisioner().Provision(_dataDir, hostname: null);

        Assert.Equal(caBefore, File.ReadAllText(second.CaCertificatePath));
        Assert.Equal(caKeyBefore, File.ReadAllText(second.CaKeyPath));
        Assert.Equal(tokenBefore, File.ReadAllText(second.TokenPepperPath));
        Assert.Equal(abuseBefore, File.ReadAllText(second.AbusePepperPath));
    }

    [Fact]
    public void Provision_Peppers_Are256BitAndDistinct()
    {
        var secrets = NewProvisioner().Provision(_dataDir, hostname: null);

        var token = Convert.FromBase64String(File.ReadAllText(secrets.TokenPepperPath));
        var abuse = Convert.FromBase64String(File.ReadAllText(secrets.AbusePepperPath));

        Assert.Equal(32, token.Length);
        Assert.Equal(32, abuse.Length);
        Assert.NotEqual(Convert.ToBase64String(token), Convert.ToBase64String(abuse));
    }

    [Fact]
    public void Provision_ServerAndAdminCerts_AreSignedByCa()
    {
        var secrets = NewProvisioner().Provision(_dataDir, hostname: null);

        // CreateFromPem(текст) завантажує лише сертифікат (ключ ігнорується); CreateFromPemFile(шлях)
        // навпаки очікує cert+key в одному файлі — тут нам потрібен саме public-cert.
        using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(secrets.CaCertificatePath));
        using var server = X509Certificate2.CreateFromPem(File.ReadAllText(secrets.ServerCertificatePath));
        using var admin = X509Certificate2.CreateFromPem(File.ReadAllText(secrets.AdminClientCertificatePath));

        Assert.Equal(ca.SubjectName.Name, server.IssuerName.Name);
        Assert.Equal(ca.SubjectName.Name, admin.IssuerName.Name);
        // Leaf-и не є CA (BasicConstraints certificateAuthority=false).
        Assert.NotEqual(ca.Thumbprint, server.Thumbprint);
    }

    [Fact]
    public void Provision_ExpiredServerCert_IsReissued_CaUnchanged()
    {
        var first = NewProvisioner().Provision(_dataDir, hostname: null);
        var caBefore = File.ReadAllText(first.CaCertificatePath);

        // Підміняємо server-leaf на near-expiry (у межах renewal-вікна 30 днів), підписаний ТИМ САМИМ
        // persisted CA. notBefore має бути НЕ раніше notBefore самого CA — тому беремо now-1хв.
        DateTime staleNotAfter;
        using (var ca = X509Certificate2.CreateFromPemFile(first.CaCertificatePath, first.CaKeyPath))
        using (var nearExpiry = CertificateFactory.IssueServerCertificate(
                   ca, hostname: null,
                   DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(10)))
        {
            staleNotAfter = nearExpiry.NotAfter.ToUniversalTime();
            File.WriteAllText(first.ServerCertificatePath, nearExpiry.ExportCertificatePem());
            using var key = nearExpiry.GetECDsaPrivateKey()!;
            File.WriteAllText(first.ServerKeyPath, key.ExportPkcs8PrivateKeyPem());
        }

        // «Рестарт»: провіжн має помітити близькість простроки й переоформити leaf тим самим CA.
        var second = NewProvisioner().Provision(_dataDir, hostname: null);

        using var renewed = X509Certificate2.CreateFromPem(File.ReadAllText(second.ServerCertificatePath));
        Assert.True(renewed.NotAfter.ToUniversalTime() > staleNotAfter);              // переоформлено
        Assert.True(renewed.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(60)); // свіжа довга валідність
        Assert.Equal(caBefore, File.ReadAllText(second.CaCertificatePath));           // CA не перегенеровано
    }

    [Fact]
    public void Provision_LeafCerts_CarryAuthorityKeyIdentifier()
    {
        // Regression: strict-клієнти (python 3.13+, VERIFY_X509_STRICT) відхиляють ланцюг без AKI
        // на leaf-ах (RFC 5280) — "Missing Authority Key Identifier".
        var secrets = NewProvisioner().Provision(_dataDir, hostname: null);

        using var server = X509Certificate2.CreateFromPem(File.ReadAllText(secrets.ServerCertificatePath));
        using var admin = X509Certificate2.CreateFromPem(File.ReadAllText(secrets.AdminClientCertificatePath));

        Assert.Contains(server.Extensions.Cast<X509Extension>(), e => e.Oid?.Value == "2.5.29.35");
        Assert.Contains(admin.Extensions.Cast<X509Extension>(), e => e.Oid?.Value == "2.5.29.35");
    }

    [Fact]
    public void Provision_LegacyLeafWithoutAki_IsReissued_CaUnchanged()
    {
        // Self-heal старих томів: leaf старого формату (без AKI, ще довго валідний, ланцюг цілий)
        // мусить переоформитись на старті; CA лишається той самий (client-trust не ламається).
        var first = NewProvisioner().Provision(_dataDir, hostname: null);
        var caBefore = File.ReadAllText(first.CaCertificatePath);

        using (var ca = X509Certificate2.CreateFromPemFile(first.CaCertificatePath, first.CaKeyPath))
        using (var key = System.Security.Cryptography.ECDsa.Create(
                   System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
        {
            // Мінімальний legacy-leaf: підписаний тим самим CA, БЕЗ AKI, поза renewal-вікном.
            var request = new CertificateRequest(
                "CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

            var serial = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            serial[0] = 0x01;
            using var legacy = request.Create(
                ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(200), serial);

            File.WriteAllText(first.ServerCertificatePath, legacy.ExportCertificatePem());
            File.WriteAllText(first.ServerKeyPath, key.ExportPkcs8PrivateKeyPem());
        }

        // «Рестарт»: гілка «без AKI» має спрацювати попри валідний термін і цілий ланцюг.
        var second = NewProvisioner().Provision(_dataDir, hostname: null);

        using var reissued = X509Certificate2.CreateFromPem(File.ReadAllText(second.ServerCertificatePath));
        Assert.Contains(reissued.Extensions.Cast<X509Extension>(), e => e.Oid?.Value == "2.5.29.35");
        Assert.Equal(caBefore, File.ReadAllText(second.CaCertificatePath));
    }

    [Fact]
    public void Provision_KeyFiles_AreOwnerOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows())
            return; // 0600 — best-effort лише на Unix (задокументовано)

        var secrets = NewProvisioner().Provision(_dataDir, hostname: null);

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(secrets.CaKeyPath));
        Assert.Equal(expected, File.GetUnixFileMode(secrets.ServerKeyPath));
        Assert.Equal(expected, File.GetUnixFileMode(secrets.TokenPepperPath));
        Assert.Equal(expected, File.GetUnixFileMode(secrets.AbusePepperPath));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання
        }
    }
}
