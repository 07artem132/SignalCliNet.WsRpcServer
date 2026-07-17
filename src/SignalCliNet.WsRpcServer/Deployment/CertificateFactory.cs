using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Pure certificate-authority helpers: create a self-signed CA and issue server / client leaf
/// certificates signed by it. No I/O, no persistence — used by <see cref="SecretMaterialProvisioner"/>.
/// </summary>
/// <remarks>
/// Ключі — ECDSA P-256 (сучасно, компактно). CA — довготривалий корінь (R3.4: fresh CA щоразу =
/// зламаний client-trust), leaf-и коротші й переоформлюються на старті при простроченні (2.3).
/// EKU: <c>serverAuth</c> для сервера, <c>clientAuth</c> для admin mTLS-бандла (S3-канон).
/// </remarks>
internal static class CertificateFactory
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>Creates a self-signed CA certificate (with private key attached).</summary>
    public static X509Certificate2 CreateCertificateAuthority(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=SignalCliNet.WsRpcServer Auto-CA", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    /// <summary>Issues a server-auth leaf certificate (SAN: localhost, loopback, optional hostname) signed by the CA.</summary>
    public static X509Certificate2 IssueServerCertificate(
        X509Certificate2 ca, string? hostname, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var subjectName = string.IsNullOrWhiteSpace(hostname) ? "localhost" : hostname;

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        if (!string.IsNullOrWhiteSpace(hostname) &&
            !string.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            sanBuilder.AddDnsName(hostname);
        }
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

        return IssueLeaf(ca, $"CN={subjectName}", ServerAuthOid, sanBuilder, notBefore, notAfter);
    }

    /// <summary>Issues a client-auth leaf certificate (admin mTLS bundle) signed by the CA.</summary>
    public static X509Certificate2 IssueAdminClientCertificate(
        X509Certificate2 ca, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        IssueLeaf(ca, "CN=admin", ClientAuthOid, san: null, notBefore, notAfter);

    private static X509Certificate2 IssueLeaf(
        X509Certificate2 ca,
        string subject,
        string enhancedKeyUsageOid,
        SubjectAlternativeNameBuilder? san,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(enhancedKeyUsageOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        // Strict-клієнти (python 3.13+, VERIFY_X509_STRICT) вимагають Authority Key Identifier за RFC 5280.
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
            ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        if (san is not null)
            request.CertificateExtensions.Add(san.Build());

        using var issued = request.Create(ca, notBefore, notAfter, NewSerialNumber());
        return issued.CopyWithPrivateKey(key);
    }

    // Серійник: 16 випадкових байтів, старший біт скинутий (гарантовано додатне ціле), ненульовий.
    private static byte[] NewSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        if (serial[0] == 0)
            serial[0] = 0x01;
        return serial;
    }
}
