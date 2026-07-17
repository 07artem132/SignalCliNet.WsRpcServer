using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

/// <summary>
/// Пінить group-claim адаптер (add-group-claim-receive, секція 2): вимкнено → -32601; requestGroupClaim
/// happy → код; quota → -32008; listMyBindings/revokeMyBinding — лише власні (caller із call-context).
/// </summary>
public sealed class SignalGroupClaimRpcAdapterTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private const string Caller = "id-u";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-groupclaim-adapter-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time;

    public SignalGroupClaimRpcAdapterTests()
    {
        Directory.CreateDirectory(_dataDir);
        _time = new TestTimeProvider(Start);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord(Caller, "user", [], Start));
        _store.UpsertIdentity(new IdentityRecord("other", "user", [], Start));
    }

    private SignalGroupClaimRpcAdapter NewAdapter(GroupClaimOptions? options = null)
    {
        var opts = Options.Create(options ?? new GroupClaimOptions { Enabled = true });
        var rate = new GroupClaimRateLimiter(opts, _time);
        var svc = new GroupClaimService(_store, rate, opts, _time, NullLogger<GroupClaimService>.Instance);
        return new SignalGroupClaimRpcAdapter(svc, _store, opts, NullLogger<SignalGroupClaimRpcAdapter>.Instance);
    }

    private static SignalPrincipal Principal(string name) =>
        new(name, isAdmin: false, hasFullAccess: false, [], []);

    [Fact]
    public async Task RequestGroupClaim_Disabled_MethodNotFound()
    {
        var adapter = NewAdapter(new GroupClaimOptions { Enabled = false });
        using var _ = SignalCallContext.Enter(Principal(Caller));

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.RequestGroupClaim());
        Assert.Equal(-32601, (int)ex.ErrorCode);
    }

    [Fact]
    public async Task RequestGroupClaim_Happy_ReturnsCode()
    {
        var adapter = NewAdapter();
        using var _ = SignalCallContext.Enter(Principal(Caller));

        var response = await adapter.RequestGroupClaim();

        Assert.False(string.IsNullOrWhiteSpace(response.Code));
        Assert.Equal(Start.AddMinutes(10), response.ExpiresAt);
    }

    [Fact]
    public async Task RequestGroupClaim_QuotaExceeded_MapsTo32008()
    {
        var adapter = NewAdapter(new GroupClaimOptions { Enabled = true, MaxActiveBindingsPerIdentity = 1 });
        _store.InsertGroupBinding(new GroupBindingRecord(
            "b-1", Caller, "grp-x", "aci", Start.AddHours(720), false, Start));
        using var _ = SignalCallContext.Enter(Principal(Caller));

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.RequestGroupClaim());
        Assert.Equal(GroupClaimErrorCodes.QuotaExceeded, (int)ex.ErrorCode);
    }

    [Fact]
    public async Task ListMyBindings_ReturnsOnlyOwnNonRevoked()
    {
        var adapter = NewAdapter();
        _store.InsertGroupBinding(new GroupBindingRecord("b-mine", Caller, "grp-1", "aci", Start.AddHours(720), false, Start));
        _store.InsertGroupBinding(new GroupBindingRecord("b-revoked", Caller, "grp-2", "aci", Start.AddHours(720), true, Start));
        _store.InsertGroupBinding(new GroupBindingRecord("b-other", "other", "grp-3", "aci", Start.AddHours(720), false, Start));
        using var _ = SignalCallContext.Enter(Principal(Caller));

        var response = await adapter.ListMyBindings();

        Assert.Single(response.Bindings);
        Assert.Equal("b-mine", response.Bindings[0].BindingId);
    }

    [Fact]
    public async Task RevokeMyBinding_Own_RevokesIt()
    {
        var adapter = NewAdapter();
        _store.InsertGroupBinding(new GroupBindingRecord("b-mine", Caller, "grp-1", "aci", Start.AddHours(720), false, Start));
        using var _ = SignalCallContext.Enter(Principal(Caller));

        var response = await adapter.RevokeMyBinding("b-mine");

        Assert.True(response.Revoked);
        Assert.True(_store.GetGroupBinding(Caller, "grp-1")!.Revoked);
    }

    [Fact]
    public async Task RevokeMyBinding_NotOwn_Rejected()
    {
        var adapter = NewAdapter();
        _store.InsertGroupBinding(new GroupBindingRecord("b-other", "other", "grp-3", "aci", Start.AddHours(720), false, Start));
        using var _ = SignalCallContext.Enter(Principal(Caller));

        await Assert.ThrowsAsync<RpcErrorException>(() => adapter.RevokeMyBinding("b-other"));
        Assert.False(_store.GetGroupBinding("other", "grp-3")!.Revoked); // чужий binding не чіпаємо
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
