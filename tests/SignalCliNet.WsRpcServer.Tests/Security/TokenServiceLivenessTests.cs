using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить token-liveness re-чек <see cref="TokenService.IsLive"/> (D9/1.5, 4 стани) і
/// <see cref="TokenService.RevokeIdentity"/> (1.4 — каскад + розрив живих конектів через broadcaster).
/// </summary>
public class TokenServiceLivenessTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-token-live-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time = new(FixedTime);
    private readonly RevocationBroadcaster _broadcaster = new(NullLogger<RevocationBroadcaster>.Instance);
    private readonly TokenService _service;

    public TokenServiceLivenessTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));

        _service = new TokenService(
            new FakePepperProvider(Pepper), _store, _time, NullLogger<TokenService>.Instance, _broadcaster);
    }

    [Fact]
    public void IsLive_ValidToken_ReturnsTrue()
    {
        var (_, record) = _service.IssueToken("id-1", TimeSpan.FromHours(12));
        Assert.True(_service.IsLive(record.TokenHash));
    }

    [Fact]
    public void IsLive_ExpiredToken_ReturnsFalse()
    {
        var (_, record) = _service.IssueToken("id-1", TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromHours(2));

        Assert.False(_service.IsLive(record.TokenHash));
    }

    [Fact]
    public void IsLive_RevokedToken_ReturnsFalse()
    {
        var (_, record) = _service.IssueToken("id-1", TimeSpan.FromHours(12));
        _store.RevokeIdentityCredentials("id-1");

        Assert.False(_service.IsLive(record.TokenHash));
    }

    [Fact]
    public void IsLive_UnknownHash_ReturnsFalse()
    {
        Assert.False(_service.IsLive("hash-that-was-never-issued"));
    }

    [Fact]
    public void RevokeIdentity_RevokesTokens_AndFiresBroadcaster()
    {
        var (_, record) = _service.IssueToken("id-1", TimeSpan.FromHours(12));
        var revokedCallbackFired = false;
        using var _ = _broadcaster.Subscribe("id-1", () => revokedCallbackFired = true);

        _service.RevokeIdentity("id-1");

        Assert.True(revokedCallbackFired);                    // розрив живих конектів (1.4)
        Assert.False(_service.IsLive(record.TokenHash));      // токен мертвий
        Assert.True(_store.GetToken(record.TokenHash)!.IsRevoked);
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
