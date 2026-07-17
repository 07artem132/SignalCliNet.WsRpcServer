using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using SignalCliNet.WsRpcServer.Tests.Security.Admission;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

/// <summary>
/// Пінить онбординг-адаптер (task 1.3): redeem валідного коду → нова identity + перший токен + enrolled
/// device-ключ + abuse-подія; невалідний код → анти-oracle помилка (-32006); порожній/малформ ключ →
/// InvalidParams; перевищення soft rate-cap → -32005.
/// </summary>
public sealed class SignalOnboardingRpcAdapterTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-onboarding-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath;

    public SignalOnboardingRpcAdapterTests()
    {
        Directory.CreateDirectory(_dataDir);
        _dbPath = Path.Combine(_dataDir, "durable.db");
    }

    private sealed record Harness(
        SignalOnboardingRpcAdapter Adapter,
        InviteService Invites,
        TokenService Tokens,
        DurableStore Store);

    private Harness CreateHarness(int rateCap = InviteRedemptionRateLimiter.DefaultMaxPerWindow)
    {
        var time = new TestTimeProvider(FixedTime);
        var store = new DurableStore(_dbPath, NullLogger<DurableStore>.Instance);
        store.Initialize();

        var invites = new InviteService(store, time, NullLogger<InviteService>.Instance);
        var rateLimiter = new InviteRedemptionRateLimiter(time, rateCap);
        var tokens = new TokenService(
            new FakePepperProvider(Pepper), store, time, NullLogger<TokenService>.Instance);
        var enrollment = new DeviceEnrollmentService(
            store, tokens, time, Options.Create(new AuthOptions { TokenTtlHours = 12 }),
            NullLogger<DeviceEnrollmentService>.Instance);
        var abuseLog = new AbuseLogService(
            store, new FakeAbusePepperProvider(), time, NullLogger<AbuseLogService>.Instance);
        var adapter = new SignalOnboardingRpcAdapter(
            invites, rateLimiter, enrollment, abuseLog, store, time,
            NullLogger<SignalOnboardingRpcAdapter>.Instance);

        return new Harness(adapter, invites, tokens, store);
    }

    private static string NewP256Spki()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public async Task RedeemInvite_ValidCode_CreatesIdentity_Enrolls_AndAudits()
    {
        var h = CreateHarness();
        var creation = h.Invites.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);
        var spki = NewP256Spki();

        var response = await h.Adapter.RedeemInvite(creation.Code, spki);

        Assert.False(string.IsNullOrWhiteSpace(response.IdentityId));
        Assert.False(string.IsNullOrWhiteSpace(response.Token));

        // Токен справжній — автентифікується як Valid.
        Assert.Equal(TokenAuthenticationStatus.Valid, h.Tokens.Authenticate(response.Token).Status);

        // Нова identity role=user + enrolled device-ключ.
        var identity = h.Store.GetIdentity(response.IdentityId);
        Assert.NotNull(identity);
        Assert.Equal("user", identity!.Role);
        Assert.Equal(spki, h.Store.GetDeviceSecret(response.IdentityId)!.PublicKey);

        // Інвайт погашено саме цією identity.
        var invite = h.Store.GetInvite(creation.Record.CodeHash);
        Assert.True(invite!.Consumed);
        Assert.Equal(response.IdentityId, invite.ConsumedBy);

        // Abuse-подія invite_redeemed залогована.
        Assert.Equal(1, CountAbuseLog());
    }

    [Fact]
    public async Task RedeemInvite_UnknownCode_ThrowsInviteInvalid()
    {
        var h = CreateHarness();

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => h.Adapter.RedeemInvite("UNKNOWN-CODE-9999", NewP256Spki()));

        Assert.Equal((JsonRpcErrorCode)SignalOnboardingRpcAdapter.InviteInvalidCode, ex.ErrorCode);
    }

    [Fact]
    public async Task RedeemInvite_ConsumedCode_ThrowsIdenticalError()
    {
        var h = CreateHarness();
        var creation = h.Invites.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);
        await h.Adapter.RedeemInvite(creation.Code, NewP256Spki());

        var unknown = await Assert.ThrowsAsync<RpcErrorException>(
            () => h.Adapter.RedeemInvite("SOME-OTHER-CODE-42", NewP256Spki()));
        var consumed = await Assert.ThrowsAsync<RpcErrorException>(
            () => h.Adapter.RedeemInvite(creation.Code, NewP256Spki()));

        // Anti-oracle: погашений і невідомий код дають ІДЕНТИЧНУ помилку.
        Assert.Equal(unknown.ErrorCode, consumed.ErrorCode);
        Assert.Equal(unknown.Message, consumed.Message);
    }

    [Fact]
    public async Task RedeemInvite_EmptyDeviceKey_ThrowsInvalidParams()
    {
        var h = CreateHarness();
        var creation = h.Invites.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => h.Adapter.RedeemInvite(creation.Code, "  "));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        // Порожній ключ відсіяно ДО consume — інвайт не спалено.
        Assert.False(h.Store.GetInvite(creation.Record.CodeHash)!.Consumed);
    }

    [Fact]
    public async Task RedeemInvite_MalformedDeviceKey_ThrowsInvalidParams()
    {
        var h = CreateHarness();
        var creation = h.Invites.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => h.Adapter.RedeemInvite(creation.Code, "not-a-valid-spki-key"));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task RedeemInvite_ExceedsSoftRateCap_ThrowsRateLimited()
    {
        var h = CreateHarness(rateCap: 1);
        var creation = h.Invites.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);

        // Перший редемпшн з'їдає єдиний слот вікна.
        await h.Adapter.RedeemInvite(creation.Code, NewP256Spki());

        // Другий (будь-який код) відсікається soft rate-cap'ом ДО роботи з інвайтом → -32005.
        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => h.Adapter.RedeemInvite("ANY-OTHER-CODE-1", NewP256Spki()));

        Assert.Equal((JsonRpcErrorCode)AdmissionErrorCodes.RateLimited, ex.ErrorCode);
    }

    private int CountAbuseLog()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM abuse_log;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання temp-каталогу
        }
    }

    // Фіксований pepper для версії 1 (як у DeviceEnrollmentServiceTests).
    private sealed class FakePepperProvider(byte[] pepper) : IPepperProvider
    {
        public int CurrentVersion => 1;

        public byte[] GetPepper(int version) =>
            version == 1 ? pepper : throw new InvalidOperationException($"Невідома версія пеппера: {version}.");
    }
}
