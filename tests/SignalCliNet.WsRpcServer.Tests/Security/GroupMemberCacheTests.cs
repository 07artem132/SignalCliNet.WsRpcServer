using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить GroupMemberCache (add-group-claim-receive, task 2.5 / T2): Member/NotMember, TTL-кешування (без
/// зайвих викликів signal-cli), інвалідизація, група-відсутня → NotMember, транзієнт → Unknown (fail-closed).
/// </summary>
public sealed class GroupMemberCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private const string Account = "+15550009999";
    private const string GroupId = "grp-1";

    private static Group GroupWith(params string[] memberAcis) =>
        new(GroupId, "G", null, IsMember: true, IsBlocked: false, MessageExpirationTime: 0,
            Members: memberAcis.Select(a => new Member(null, a)).ToList(),
            PendingMembers: [], RequestingMembers: [], Admins: [], Banned: [],
            PermissionAddMember: "EVERY_MEMBER", PermissionEditDetails: "EVERY_MEMBER",
            PermissionSendMessage: "EVERY_MEMBER", GroupInviteLink: null);

    private static (GroupMemberCache Cache, Mock<ISignalGroups> Groups, TestTimeProvider Time) NewCache(
        int ttlSeconds = 60)
    {
        var groups = new Mock<ISignalGroups>();
        var time = new TestTimeProvider(Start);
        var opts = Options.Create(new GroupClaimOptions { Enabled = true, MemberCacheTtlSeconds = ttlSeconds });
        var cache = new GroupMemberCache(groups.Object, opts, time, NullLogger<GroupMemberCache>.Instance);
        return (cache, groups, time);
    }

    [Fact]
    public async Task Member_WhenAnchorInGroup()
    {
        var (cache, groups, _) = NewCache();
        groups.Setup(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListGroupsResponse([GroupWith("aci-1", "aci-2")]));

        Assert.Equal(GroupMembership.Member, await cache.CheckAsync(Account, GroupId, "aci-1"));
    }

    [Fact]
    public async Task NotMember_WhenAnchorAbsent()
    {
        var (cache, groups, _) = NewCache();
        groups.Setup(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListGroupsResponse([GroupWith("aci-2")]));

        Assert.Equal(GroupMembership.NotMember, await cache.CheckAsync(Account, GroupId, "aci-1"));
    }

    [Fact]
    public async Task NotMember_WhenGroupAbsentFromList()
    {
        var (cache, groups, _) = NewCache();
        groups.Setup(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListGroupsResponse([])); // бот більше не в групі

        Assert.Equal(GroupMembership.NotMember, await cache.CheckAsync(Account, GroupId, "aci-1"));
    }

    [Fact]
    public async Task Unknown_OnTransientError()
    {
        var (cache, groups, _) = NewCache();
        groups.Setup(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        Assert.Equal(GroupMembership.Unknown, await cache.CheckAsync(Account, GroupId, "aci-1"));
    }

    [Fact]
    public async Task Ttl_CachesWithinWindow_RefetchesAfter()
    {
        var (cache, groups, time) = NewCache(ttlSeconds: 60);
        groups.Setup(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListGroupsResponse([GroupWith("aci-1")]));

        await cache.CheckAsync(Account, GroupId, "aci-1");
        await cache.CheckAsync(Account, GroupId, "aci-1"); // у межах TTL → з кешу
        groups.Verify(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()), Times.Once);

        time.Advance(TimeSpan.FromSeconds(61)); // TTL протух
        await cache.CheckAsync(Account, GroupId, "aci-1");
        groups.Verify(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Invalidate_ForcesRefetch()
    {
        var (cache, groups, _) = NewCache(ttlSeconds: 600);
        groups.Setup(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListGroupsResponse([GroupWith("aci-1")]));

        await cache.CheckAsync(Account, GroupId, "aci-1");
        cache.Invalidate(GroupId);
        await cache.CheckAsync(Account, GroupId, "aci-1");

        groups.Verify(g => g.ListGroupsAsync(Account, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
