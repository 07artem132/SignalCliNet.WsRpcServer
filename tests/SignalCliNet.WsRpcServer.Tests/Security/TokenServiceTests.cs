using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="TokenService"/> поверх реального durable-стору (tasks 1.2–1.4): усі 4 статуси
/// автентифікації, revoked-перемагає-expired (D7), ротація zero-overlap (W21) та відмова ротації без
/// попередника.
/// </summary>
public class TokenServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-token-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time = new(FixedTime);
    private readonly TokenService _service;

    public TokenServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        // Токени мають FK на identities — сідимо identity перед видачею.
        _store.UpsertIdentity(new IdentityRecord("id-1", "user", [], FixedTime));

        _service = new TokenService(
            new FakePepperProvider(Pepper), _store, _time, NullLogger<TokenService>.Instance);
    }

    [Fact]
    public void IssueToken_ThenAuthenticate_ReturnsValid()
    {
        var (token, record) = _service.IssueToken("id-1", TimeSpan.FromHours(12));

        var result = _service.Authenticate(token);
        Assert.Equal(TokenAuthenticationStatus.Valid, result.Status);
        Assert.Equal("id-1", result.IdentityId);
        Assert.Equal(record.TokenHash, result.TokenHash);
    }

    [Fact]
    public void Authenticate_MalformedToken_ReturnsInvalid()
    {
        var result = _service.Authenticate("this-is-not-a-token");
        Assert.Equal(TokenAuthenticationStatus.Invalid, result.Status);
        Assert.Null(result.IdentityId);
    }

    [Fact]
    public void Authenticate_WellFormedButUnknownToken_ReturnsInvalid()
    {
        // Синтаксично валідний токен (той самий pepper-ver), який ніколи не видавали → хеша немає у сторі.
        var stray = TokenFormat.Create(1, out _);

        var result = _service.Authenticate(stray);
        Assert.Equal(TokenAuthenticationStatus.Invalid, result.Status);
    }

    [Fact]
    public void Authenticate_ExpiredToken_ReturnsExpired()
    {
        var (token, _) = _service.IssueToken("id-1", TimeSpan.FromHours(1));

        _time.Advance(TimeSpan.FromHours(2)); // за межами TTL

        var result = _service.Authenticate(token);
        Assert.Equal(TokenAuthenticationStatus.Expired, result.Status);
        Assert.Equal("id-1", result.IdentityId);
    }

    [Fact]
    public void Authenticate_RevokedWins_OverExpired()
    {
        var (token, _) = _service.IssueToken("id-1", TimeSpan.FromHours(1));

        // Revoke + вихід за TTL: IsRevoked має перемогти Expired (D7).
        _store.RevokeIdentityCredentials("id-1");
        _time.Advance(TimeSpan.FromHours(2));

        var result = _service.Authenticate(token);
        Assert.Equal(TokenAuthenticationStatus.Revoked, result.Status);
    }

    [Fact]
    public void Rotate_RevokesOld_AndIssuesLiveNew_ZeroOverlap()
    {
        var (_, oldRecord) = _service.IssueToken("id-1", TimeSpan.FromHours(12));

        var (newToken, newRecord) = _service.Rotate(oldRecord.TokenHash, "id-1", TimeSpan.FromHours(12));

        // Старий revoked, новий живий — обидва у сторі (zero-overlap, W21).
        var oldReloaded = _store.GetToken(oldRecord.TokenHash);
        Assert.NotNull(oldReloaded);
        Assert.True(oldReloaded!.IsRevoked);

        Assert.NotEqual(oldRecord.TokenHash, newRecord.TokenHash);
        Assert.Equal(TokenAuthenticationStatus.Valid, _service.Authenticate(newToken).Status);
    }

    [Fact]
    public void Rotate_WithoutPredecessor_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => _service.Rotate("nonexistent-hash", "id-1", TimeSpan.FromHours(12)));
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
