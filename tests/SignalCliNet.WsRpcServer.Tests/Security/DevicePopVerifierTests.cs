using System.Security.Cryptography;
using System.Text;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="DevicePopVerifier"/> (task 3.1): валідний ECDSA P-256 підпис (IEEE P1363) над
/// <c>nonce ‖ connId</c> проходить; інший connId (релей, G8) / інший nonce / інший ключ / сміття → false
/// без винятку.
/// </summary>
public class DevicePopVerifierTests
{
    private const string Nonce = "u4Xk9_Qb2Zr7Nonce-base64url-01";
    private const string ConnId = "11111111-1111-1111-1111-111111111111";

    private static (string SpkiBase64, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static string Sign(ECDsa key, string nonce, string connId)
    {
        var data = Encoding.UTF8.GetBytes(nonce + connId);
        var signature = key.SignData(
            data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var (spki, key) = NewKey();
        using (key)
        {
            var signature = Sign(key, Nonce, ConnId);
            Assert.True(DevicePopVerifier.Verify(spki, Nonce, ConnId, signature));
        }
    }

    [Fact]
    public void Verify_DifferentConnId_ReturnsFalse()
    {
        // G8: релей того ж підпису на інший сокет ламається — інший connId у pre-image.
        var (spki, key) = NewKey();
        using (key)
        {
            var signature = Sign(key, Nonce, ConnId);
            Assert.False(DevicePopVerifier.Verify(
                spki, Nonce, "22222222-2222-2222-2222-222222222222", signature));
        }
    }

    [Fact]
    public void Verify_DifferentNonce_ReturnsFalse()
    {
        var (spki, key) = NewKey();
        using (key)
        {
            var signature = Sign(key, Nonce, ConnId);
            Assert.False(DevicePopVerifier.Verify(spki, "some-other-nonce", ConnId, signature));
        }
    }

    [Fact]
    public void Verify_WrongKey_ReturnsFalse()
    {
        var (_, signer) = NewKey();
        var (otherSpki, other) = NewKey();
        using (signer)
        using (other)
        {
            var signature = Sign(signer, Nonce, ConnId);
            Assert.False(DevicePopVerifier.Verify(otherSpki, Nonce, ConnId, signature));
        }
    }

    [Theory]
    [InlineData("not-base64!!", "QUFBQQ==")]  // малформлений ключ
    [InlineData("QUFBQQ==", "not-base64!!")]  // малформлений підпис
    public void Verify_MalformedBase64_ReturnsFalse(string spki, string signature)
    {
        Assert.False(DevicePopVerifier.Verify(spki, Nonce, ConnId, signature));
    }

    [Fact]
    public void Verify_GarbageSpki_ReturnsFalse()
    {
        // Валідний base64, але не SPKI ECDSA-ключ → CryptographicException всередині → false.
        var garbageSpki = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]);
        var (_, key) = NewKey();
        using (key)
        {
            var signature = Sign(key, Nonce, ConnId);
            Assert.False(DevicePopVerifier.Verify(garbageSpki, Nonce, ConnId, signature));
        }
    }

    [Fact]
    public void Verify_EmptyKeyOrSignature_ReturnsFalse()
    {
        var (spki, key) = NewKey();
        using (key)
        {
            Assert.False(DevicePopVerifier.Verify("", Nonce, ConnId, "QUFBQQ=="));
            Assert.False(DevicePopVerifier.Verify(spki, Nonce, ConnId, ""));
        }
    }
}
