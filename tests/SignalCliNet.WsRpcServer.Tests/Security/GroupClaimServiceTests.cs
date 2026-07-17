using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить GroupClaimService (add-group-claim-receive, tasks 2.1/2.2): request→confirm happy, one-time
/// (повторний/перехоплений/expired код → відмова), T3 (група фіксується місцем появи коду), quota-cap і
/// per-identity rate-limit.
/// </summary>
public sealed class GroupClaimServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-groupclaim-svc-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time;

    public GroupClaimServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _time = new TestTimeProvider(Start);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("id-u", "user", [], Start));
    }

    private GroupClaimService NewService(GroupClaimOptions? options = null)
    {
        var opts = Options.Create(options ?? new GroupClaimOptions { Enabled = true });
        var rateLimiter = new GroupClaimRateLimiter(opts, _time);
        return new GroupClaimService(_store, rateLimiter, opts, _time, NullLogger<GroupClaimService>.Instance);
    }

    [Fact]
    public void RequestClaim_Happy_ReturnsCode()
    {
        var svc = NewService();

        var result = svc.RequestClaim("id-u");

        Assert.Equal(GroupClaimStatus.Created, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Code));
        Assert.Equal(Start.AddMinutes(10), result.ExpiresAt);
    }

    [Fact]
    public void RequestThenConfirm_CreatesBinding_WithGroupAndAnchor()
    {
        var svc = NewService();
        var code = svc.RequestClaim("id-u").Code!;
        var hash = GroupClaimService.ComputeCodeHash(code);

        Assert.True(svc.Confirm(hash, "grp-1", "aci-anchor"));

        var binding = _store.GetGroupBinding("id-u", "grp-1");
        Assert.NotNull(binding);
        Assert.Equal("aci-anchor", binding!.AnchorAci);
        Assert.False(binding.Revoked);
        Assert.Equal(Start.AddHours(720), binding.ExpiresAt);
    }

    [Fact]
    public void Confirm_OneTime_SecondUseFails()
    {
        var svc = NewService();
        var hash = GroupClaimService.ComputeCodeHash(svc.RequestClaim("id-u").Code!);

        Assert.True(svc.Confirm(hash, "grp-1", "aci-1"));
        // Повторний/перехоплений код → відмова (consumed).
        Assert.False(svc.Confirm(hash, "grp-1", "aci-1"));
    }

    [Fact]
    public void Confirm_Expired_Fails()
    {
        var svc = NewService();
        var hash = GroupClaimService.ComputeCodeHash(svc.RequestClaim("id-u").Code!);

        _time.Advance(TimeSpan.FromMinutes(11)); // за межами TTL (10 хв)

        Assert.False(svc.Confirm(hash, "grp-1", "aci-1"));
        Assert.Null(_store.GetGroupBinding("id-u", "grp-1"));
    }

    [Fact]
    public void Confirm_T3_GroupFixedByFirstAppearance_SecondGroupGetsNothing()
    {
        var svc = NewService();
        var hash = GroupClaimService.ComputeCodeHash(svc.RequestClaim("id-u").Code!);

        Assert.True(svc.Confirm(hash, "grp-1", "aci-1"));   // binding лише до grp-1
        Assert.False(svc.Confirm(hash, "grp-2", "aci-1"));  // той самий код у другій групі → нічого (one-time)

        Assert.NotNull(_store.GetGroupBinding("id-u", "grp-1"));
        Assert.Null(_store.GetGroupBinding("id-u", "grp-2"));
    }

    [Fact]
    public void RequestClaim_QuotaCap_ReturnsQuotaExceeded()
    {
        var svc = NewService(new GroupClaimOptions { Enabled = true, MaxActiveBindingsPerIdentity = 1 });
        // Один активний binding уже займає квоту.
        _store.InsertGroupBinding(new GroupBindingRecord(
            "b-1", "id-u", "grp-x", "aci", Start.AddHours(720), false, Start));

        Assert.Equal(GroupClaimStatus.QuotaExceeded, svc.RequestClaim("id-u").Status);
    }

    [Fact]
    public void RequestClaim_RateLimited_AfterCap()
    {
        var svc = NewService(new GroupClaimOptions { Enabled = true, ClaimRequestsPerHourPerIdentity = 1 });

        Assert.Equal(GroupClaimStatus.Created, svc.RequestClaim("id-u").Status);
        Assert.Equal(GroupClaimStatus.RateLimited, svc.RequestClaim("id-u").Status);
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
