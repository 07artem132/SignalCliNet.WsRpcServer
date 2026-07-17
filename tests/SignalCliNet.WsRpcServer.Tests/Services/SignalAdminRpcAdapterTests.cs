using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

/// <summary>
/// Пінить admin-адаптер (add-invites-admin, task 3.1): createInvite → InviteService (invite у сторі) +
/// audit-подія; revokeIdentity → TokenService каскад revoke + audit; актор береться з call-context
/// principal'а (не з аргумента); без admin-principal → -32001 (belt-and-suspenders поза chokepoint'ом).
/// </summary>
public sealed class SignalAdminRpcAdapterTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-admin-adapter-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time;
    private readonly InviteService _invites;
    private readonly TokenService _tokens;
    private readonly AuditLogService _audit;
    private readonly SignalAdminRpcAdapter _adapter;

    public SignalAdminRpcAdapterTests()
    {
        Directory.CreateDirectory(_dataDir);
        _time = new TestTimeProvider(FixedTime);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], FixedTime));

        _invites = new InviteService(_store, _time, NullLogger<InviteService>.Instance);
        _tokens = new TokenService(new FakePepperProvider(Pepper), _store, _time, NullLogger<TokenService>.Instance);
        _audit = new AuditLogService(_store, _time, NullLogger<AuditLogService>.Instance);
        _adapter = new SignalAdminRpcAdapter(
            _invites, _tokens, _audit, Options.Create(new AdminOptions()), _time,
            NullLogger<SignalAdminRpcAdapter>.Instance);
    }

    private static SignalPrincipal Admin() =>
        new("admin", isAdmin: true, hasFullAccess: false, [], []);

    [Fact]
    public async Task CreateInvite_AsAdmin_MintsInvite_AndAudits()
    {
        using var _ = SignalCallContext.Enter(Admin());

        var response = await _adapter.CreateInvite(ttlSeconds: 3600, maxAttempts: 5);

        Assert.False(string.IsNullOrWhiteSpace(response.Code));
        Assert.Equal(FixedTime.AddSeconds(3600), response.ExpiresAt);

        // Інвайт справді у сторі, виданий admin-ом.
        var invite = _store.GetInvite(InviteService.ComputeCodeHash(response.Code));
        Assert.NotNull(invite);
        Assert.Equal("admin", invite!.CreatedBy);
        Assert.Equal(5, invite.MaxAttempts);

        // Audit-подія invite_created; ланцюг цілий.
        Assert.True(_audit.VerifyChain());
        Assert.Contains(_store.GetAuditEntries(), e => e.EventType == "invite_created" && e.ActorIdentity == "admin");
    }

    [Fact]
    public async Task CreateInvite_OmittedArgs_UsesConfiguredDefaults()
    {
        var options = new AdminOptions { DefaultInviteTtlSeconds = 120, DefaultInviteMaxAttempts = 2 };
        var adapter = new SignalAdminRpcAdapter(
            _invites, _tokens, _audit, Options.Create(options), _time, NullLogger<SignalAdminRpcAdapter>.Instance);
        using var _ = SignalCallContext.Enter(Admin());

        var response = await adapter.CreateInvite();

        Assert.Equal(FixedTime.AddSeconds(120), response.ExpiresAt);
        Assert.Equal(2, _store.GetInvite(InviteService.ComputeCodeHash(response.Code))!.MaxAttempts);
    }

    [Fact]
    public async Task RevokeIdentity_AsAdmin_CascadeRevokes_AndAudits()
    {
        // Ціль із живим токеном.
        _store.UpsertIdentity(new IdentityRecord("victim", "user", [], FixedTime));
        var (token, _) = _tokens.IssueToken("victim", TimeSpan.FromHours(1));
        Assert.Equal(TokenAuthenticationStatus.Valid, _tokens.Authenticate(token).Status);

        using var _ = SignalCallContext.Enter(Admin());
        var response = await _adapter.RevokeIdentity("victim");

        Assert.True(response.Revoked);
        Assert.Equal("victim", response.IdentityId);
        // Токен більше не живий (каскад revoke).
        Assert.Equal(TokenAuthenticationStatus.Revoked, _tokens.Authenticate(token).Status);

        Assert.True(_audit.VerifyChain());
        Assert.Contains(_store.GetAuditEntries(), e => e.EventType == "identity_revoked" && e.ActorIdentity == "admin");
    }

    [Fact]
    public async Task CreateInvite_WithoutAdminPrincipal_ThrowsUnauthorized()
    {
        // Немає call-context principal'а → -32001 (belt-and-suspenders; chokepoint зазвичай ловить раніше).
        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => _adapter.CreateInvite());
        Assert.Equal((JsonRpcErrorCode)AccountIsolationGuard.UnauthorizedErrorCode, ex.ErrorCode);
    }

    [Fact]
    public async Task RevokeIdentity_EmptyId_ThrowsInvalidParams()
    {
        using var _ = SignalCallContext.Enter(Admin());
        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => _adapter.RevokeIdentity("  "));
        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
    }

    public void Dispose()
    {
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

    // Фіксований pepper для версії 1 (як у інших security-тестах).
    private sealed class FakePepperProvider(byte[] pepper) : IPepperProvider
    {
        public int CurrentVersion => 1;

        public byte[] GetPepper(int version) =>
            version == 1 ? pepper : throw new InvalidOperationException($"Невідома версія пеппера: {version}.");
    }
}
