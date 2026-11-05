using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Teams;

/// <summary>
/// Пінить coalescing за чатом (add-team-notify): перше повідомлення — одразу, наступні в межах вікна —
/// одним повідомленням у кінці вікна; чати не змішуються; вікно 0 вимикає; graceful stop шле чергу.
/// Час — лише керований <see cref="TestTimeProvider"/>.
/// </summary>
public sealed class ChatCoalescerTests
{
    private const string Bot = "+15550009999";
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly TestTimeProvider _time = new(Start);
    private readonly List<(string Account, string GroupId, string Text)> _sent = [];

    private ChatCoalescer NewCoalescer(int windowSeconds = 300) => new(
        (account, groupId, text, _) =>
        {
            _sent.Add((account, groupId, text));
            return Task.CompletedTask;
        },
        Options.Create(new NotifyOptions { CoalesceWindowSeconds = windowSeconds }),
        _time,
        NullLogger<ChatCoalescer>.Instance);

    [Fact]
    public async Task ThreeMessagesInTwoMinutes_FirstNow_RestJoinedAtWindowEnd()
    {
        var c = NewCoalescer();

        Assert.Equal(CoalesceDecision.SendNow, c.TryCoalesce(Bot, "g1", "one"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CoalesceDecision.Queued, c.TryCoalesce(Bot, "g1", "two"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CoalesceDecision.Queued, c.TryCoalesce(Bot, "g1", "three"));

        await c.FlushDueAsync();
        Assert.Empty(_sent);   // вікно ще не скінчилось

        _time.Advance(TimeSpan.FromMinutes(3));
        await c.FlushDueAsync();

        var batch = Assert.Single(_sent);
        Assert.Equal((Bot, "g1", "two" + ChatCoalescer.Separator + "three"), batch);
    }

    [Fact]
    public async Task DifferentChats_EachFirstGoesNow_NoCrossChatJoin()
    {
        var c = NewCoalescer();

        Assert.Equal(CoalesceDecision.SendNow, c.TryCoalesce(Bot, "g1", "a1"));
        Assert.Equal(CoalesceDecision.SendNow, c.TryCoalesce(Bot, "g2", "b1"));
        Assert.Equal(CoalesceDecision.Queued, c.TryCoalesce(Bot, "g1", "a2"));
        Assert.Equal(CoalesceDecision.Queued, c.TryCoalesce(Bot, "g2", "b2"));

        _time.Advance(TimeSpan.FromMinutes(5));
        await c.FlushDueAsync();

        Assert.Equal(2, _sent.Count);
        Assert.Contains((Bot, "g1", "a2"), _sent);
        Assert.Contains((Bot, "g2", "b2"), _sent);
    }

    [Fact]
    public void WindowZero_DisablesCoalescing()
    {
        var c = NewCoalescer(windowSeconds: 0);

        Assert.False(c.Enabled);
        Assert.Equal(CoalesceDecision.SendNow, c.TryCoalesce(Bot, "g1", "a"));
        Assert.Equal(CoalesceDecision.SendNow, c.TryCoalesce(Bot, "g1", "b"));
    }

    [Fact]
    public async Task EmptyWindow_Closes_NextMessageGoesNow()
    {
        var c = NewCoalescer();

        Assert.Equal(CoalesceDecision.SendNow, c.TryCoalesce(Bot, "g1", "a"));
        _time.Advance(TimeSpan.FromMinutes(5));
        await c.FlushDueAsync();

        Assert.Empty(_sent);
        Assert.Equal(0, c.OpenWindows);
        Assert.Equal(CoalesceDecision.SendNow, c.TryCoalesce(Bot, "g1", "b"));
    }

    [Fact]
    public async Task AfterFlush_WindowRestarts_NextMessageIsQueuedAgain()
    {
        var c = NewCoalescer();

        c.TryCoalesce(Bot, "g1", "a");
        c.TryCoalesce(Bot, "g1", "b");
        _time.Advance(TimeSpan.FromMinutes(5));
        await c.FlushDueAsync();
        Assert.Single(_sent);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CoalesceDecision.Queued, c.TryCoalesce(Bot, "g1", "c"));
    }

    [Fact]
    public async Task FlushAll_SendsQueuedBeforeWindowEnd()
    {
        var c = NewCoalescer();

        c.TryCoalesce(Bot, "g1", "a");
        c.TryCoalesce(Bot, "g1", "b");
        await c.FlushAllAsync();

        Assert.Equal((Bot, "g1", "b"), Assert.Single(_sent));
    }

    [Fact]
    public void OverfullBatch_FallsBackToSendNow()
    {
        var c = NewCoalescer();
        var big = new string('x', ChatCoalescer.MaxJoinedChars - 10);

        c.TryCoalesce(Bot, "g1", "first");
        Assert.Equal(CoalesceDecision.Queued, c.TryCoalesce(Bot, "g1", big));
        Assert.Equal(CoalesceDecision.SendNow, c.TryCoalesce(Bot, "g1", new string('y', 20)));
    }

    [Fact]
    public async Task SendFailure_IsSwallowed_AndDoesNotBlockOtherChats()
    {
        var sent = new List<string>();
        var c = new ChatCoalescer(
            (_, groupId, _, _) => groupId == "g1"
                ? throw new IOException("down")
                : Task.Run(() => sent.Add(groupId)),
            Options.Create(new NotifyOptions()), _time, NullLogger<ChatCoalescer>.Instance);

        c.TryCoalesce(Bot, "g1", "a");
        c.TryCoalesce(Bot, "g1", "b");
        c.TryCoalesce(Bot, "g2", "a");
        c.TryCoalesce(Bot, "g2", "b");
        _time.Advance(TimeSpan.FromMinutes(5));
        await c.FlushDueAsync();

        Assert.Equal(["g2"], sent);
    }
}
