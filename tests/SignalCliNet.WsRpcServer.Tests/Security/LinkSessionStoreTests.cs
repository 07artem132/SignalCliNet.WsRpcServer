using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Tests.TestSupport;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить link-session стор (add-link-sessions, tasks 1/2/6): 256-bit one-time SessionId, TTL 120с,
/// анти-griefing (чужа identity не гасить сесію), rate-limit 3/хв/identity + скид вікна, redact у логах.
/// </summary>
public class LinkSessionStoreTests
{
    private const string Uri = "sgnl://linkdevice?uuid=abc&pub_key=def";
    private static readonly DateTimeOffset Start = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static LinkSessionStore NewStore(TestTimeProvider clock) =>
        new(clock, NullLogger<LinkSessionStore>.Instance);

    private static TestTimeProvider NewClock() => new(Start);

    [Fact]
    public void Create_ReturnsSession_WithExpectedFields()
    {
        var clock = NewClock();
        var store = NewStore(clock);

        var session = store.Create("id-a", "+15551230001", Uri);

        Assert.Equal("id-a", session.CreatedByIdentityId);
        Assert.Equal("+15551230001", session.TargetAccount);
        Assert.Equal(Uri, session.DeviceLinkUri);
        Assert.Equal(Start + LinkSessionStore.SessionTtl, session.ExpiresAt);
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
    }

    [Fact]
    public void Create_GeneratesUrlSafe256BitId_UniquePerCall()
    {
        var store = NewStore(NewClock());

        var a = store.Create("id-a", null, Uri).SessionId;
        var b = store.Create("id-a", null, Uri).SessionId;

        // 32 CSPRNG-байти → base64url без padding = 43 символи, лише URL-safe алфавіт.
        Assert.Equal(43, a.Length);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), a);
        Assert.NotEqual(a, b); // ентропія: два старти не збігаються
    }

    [Fact]
    public void TryConsume_Owner_ReturnsSession_ThenOneTime()
    {
        var store = NewStore(NewClock());
        var id = store.Create("id-a", null, Uri).SessionId;

        var first = store.TryConsume(id, "id-a");
        var second = store.TryConsume(id, "id-a");

        Assert.NotNull(first);
        Assert.Equal(Uri, first!.DeviceLinkUri);
        Assert.Null(second); // one-time: повторний consume → null
    }

    [Fact]
    public void TryConsume_ForeignIdentity_ReturnsNull_AndKeepsSessionAlive()
    {
        var store = NewStore(NewClock());
        var id = store.Create("id-a", null, Uri).SessionId;

        var foreign = store.TryConsume(id, "id-b"); // анти-griefing: чужа identity
        var ownerAfter = store.TryConsume(id, "id-a"); // сесія має лишитись живою

        Assert.Null(foreign);
        Assert.NotNull(ownerAfter);
    }

    [Fact]
    public void TryConsume_Unknown_ReturnsNull()
    {
        var store = NewStore(NewClock());
        Assert.Null(store.TryConsume("no-such-session", "id-a"));
    }

    [Fact]
    public void TryConsume_Expired_ReturnsNull()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var id = store.Create("id-a", null, Uri).SessionId;

        clock.Advance(LinkSessionStore.SessionTtl); // рівно на межі TTL → прострочена

        Assert.Null(store.TryConsume(id, "id-a"));
    }

    [Fact]
    public void Create_RateLimit_ThreePerWindow_FourthThrows()
    {
        var store = NewStore(NewClock());

        for (var i = 0; i < LinkSessionStore.MaxStartsPerWindow; i++)
            store.Create("id-a", null, Uri);

        Assert.Throws<InvalidOperationException>(() => store.Create("id-a", null, Uri));
    }

    [Fact]
    public void Create_RateLimit_WindowReset_AllowsAgain()
    {
        var clock = NewClock();
        var store = NewStore(clock);

        for (var i = 0; i < LinkSessionStore.MaxStartsPerWindow; i++)
            store.Create("id-a", null, Uri);

        clock.Advance(LinkSessionStore.RateWindow); // ковзне вікно скинулось

        var afterReset = store.Create("id-a", null, Uri); // знову дозволено
        Assert.False(string.IsNullOrWhiteSpace(afterReset.SessionId));
    }

    [Fact]
    public void Create_RateLimit_IsPerIdentity()
    {
        var store = NewStore(NewClock());

        for (var i = 0; i < LinkSessionStore.MaxStartsPerWindow; i++)
            store.Create("id-a", null, Uri); // id-a вичерпав бюджет

        // id-b має власний бюджет — не заблокований сусідом.
        var other = store.Create("id-b", null, Uri);
        Assert.False(string.IsNullOrWhiteSpace(other.SessionId));
    }

    [Fact]
    public void TryConsume_ForeignIdentity_LogsWarning_WithoutFullSessionId()
    {
        var capturing = new CapturingLogger<LinkSessionStore>();
        var store = new LinkSessionStore(NewClock(), capturing);
        var id = store.Create("id-a", null, Uri).SessionId;

        store.TryConsume(id, "id-b"); // чужа identity → warning із префіксом

        // Redact: жоден рядок не містить повного sessionId і сирого URI.
        Assert.False(capturing.AnyContains(id), "Повний sessionId не має з'являтися в лозі.");
        Assert.False(capturing.AnyContains(Uri), "Device-link URI не має з'являтися в лозі.");
        Assert.True(capturing.AnyContains(id[..8]), "Очікували діагностичний префікс sessionId у warning.");
    }
}
