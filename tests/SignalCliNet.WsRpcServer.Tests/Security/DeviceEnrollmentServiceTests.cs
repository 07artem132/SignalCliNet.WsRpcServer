using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="DeviceEnrollmentService"/> (task 3.3) поверх реального durable-стору: успіх →
/// перший токен + збережений device-секрет; TOFU-повтор → відмова (D1); re-enroll після revoke → ok;
/// невалідний / не-P256 / не-ECDSA ключ → ArgumentException; неіснуюча identity → InvalidOperationException.
/// </summary>
public class DeviceEnrollmentServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-enroll-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time = new(FixedTime);
    private readonly TokenService _tokenService;
    private readonly DeviceEnrollmentService _service;

    public DeviceEnrollmentServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));

        _tokenService = new TokenService(
            new FakePepperProvider(Pepper), _store, _time, NullLogger<TokenService>.Instance);
        _service = new DeviceEnrollmentService(
            _store, _tokenService, _time,
            Options.Create(new AuthOptions { TokenTtlHours = 12 }),
            NullLogger<DeviceEnrollmentService>.Instance);
    }

    private static string NewP256Spki()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void Enroll_NewIdentity_IssuesTokenAndStoresSecret()
    {
        var spki = NewP256Spki();

        var (token, record) = _service.Enroll("id-1", spki);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal("id-1", record.IdentityId);
        Assert.Equal(TokenAuthenticationStatus.Valid, _tokenService.Authenticate(token).Status);

        var secret = _store.GetDeviceSecret("id-1");
        Assert.NotNull(secret);
        Assert.Equal(spki, secret!.PublicKey);
        Assert.Equal("ES256", secret.Algorithm);
        Assert.False(secret.IsRevoked);
    }

    [Fact]
    public void Enroll_TofuRepeat_WithLiveSecret_Throws()
    {
        _service.Enroll("id-1", NewP256Spki());

        // Активний device-секрет уже є → тиха заміна ключа заборонена (D1).
        Assert.Throws<InvalidOperationException>(() => _service.Enroll("id-1", NewP256Spki()));
    }

    [Fact]
    public void Enroll_AfterRevoke_ReenrollSucceeds()
    {
        _service.Enroll("id-1", NewP256Spki());
        _store.RevokeIdentityCredentials("id-1");   // device-секрет → revoked (re-invite)

        var newSpki = NewP256Spki();
        var (token, _) = _service.Enroll("id-1", newSpki);

        Assert.Equal(TokenAuthenticationStatus.Valid, _tokenService.Authenticate(token).Status);
        var secret = _store.GetDeviceSecret("id-1");
        Assert.Equal(newSpki, secret!.PublicKey);
        Assert.False(secret.IsRevoked);
    }

    [Fact]
    public void Enroll_InvalidBase64Key_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => _service.Enroll("id-1", "not-base64!!"));
    }

    [Fact]
    public void Enroll_NonP256Curve_ThrowsArgument()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var spki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        Assert.Throws<ArgumentException>(() => _service.Enroll("id-1", spki));
    }

    [Fact]
    public void Enroll_NonEcdsaKey_ThrowsArgument()
    {
        using var rsa = RSA.Create(2048);
        var spki = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        Assert.Throws<ArgumentException>(() => _service.Enroll("id-1", spki));
    }

    [Fact]
    public void Enroll_MissingIdentity_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() => _service.Enroll("ghost", NewP256Spki()));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання темп-каталогу
        }
    }

    // Фіксований pepper для версії 1; будь-яка інша версія — невідома (як FilePepperProvider).
    private sealed class FakePepperProvider(byte[] pepper) : IPepperProvider
    {
        public int CurrentVersion => 1;

        public byte[] GetPepper(int version) =>
            version == 1
                ? pepper
                : throw new InvalidOperationException($"Невідома версія пеппера: {version}.");
    }
}
