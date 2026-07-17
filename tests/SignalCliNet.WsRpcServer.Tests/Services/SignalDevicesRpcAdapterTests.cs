using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Devices;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using SignalCliNet.WsRpcServer.Tests.TestSupport;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

/// <summary>
/// Пінить devices-адаптер із link-сесіями (add-link-sessions): startLink повертає sessionId+uri,
/// finishLink маплить sessionId→uri, anti-oracle на невалідну сесію (-32004), admin-guard спільного бота,
/// приватний bind нового номера, rate-limit та redact URI у логах.
/// </summary>
public sealed class SignalDevicesRpcAdapterTests : IDisposable
{
    private const string Uri = "sgnl://linkdevice?uuid=abc&pub_key=def";
    private static readonly DateTimeOffset Start = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-link-tests-" + Guid.NewGuid().ToString("N"));

    public SignalDevicesRpcAdapterTests() => Directory.CreateDirectory(_dataDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання temp-каталогу
        }
    }

    private sealed record Harness(
        SignalDevicesRpcAdapter Adapter,
        Mock<ISignalDevices> Facade,
        LinkSessionStore LinkSessions,
        DurableStore Store,
        TestTimeProvider Clock);

    private Harness CreateHarness(ILogger<SignalDevicesRpcAdapter>? adapterLogger = null)
    {
        var clock = new TestTimeProvider(Start);
        var store = new DurableStore(Path.Combine(_dataDir, Guid.NewGuid().ToString("N") + ".db"),
            NullLogger<DurableStore>.Instance);
        store.Initialize();
        var linkSessions = new LinkSessionStore(clock, NullLogger<LinkSessionStore>.Instance);
        var facade = new Mock<ISignalDevices>();
        facade.Setup(d => d.StartLinkAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartLinkResponse(Uri));
        var adapter = new SignalDevicesRpcAdapter(
            facade.Object, linkSessions, store, clock,
            adapterLogger ?? NullLogger<SignalDevicesRpcAdapter>.Instance);
        return new Harness(adapter, facade, linkSessions, store, clock);
    }

    private static SignalPrincipal NonAdmin(string name) => new(name, isAdmin: false, hasFullAccess: false, [], []);

    private static SignalPrincipal Admin(string name) => new(name, isAdmin: true, hasFullAccess: false, [], []);

    [Fact]
    public async Task StartLink_ReturnsSessionId_AndUri()
    {
        var h = CreateHarness();

        var result = await h.Adapter.StartLink();

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.Equal(Uri, result.DeviceLinkUri);
        // Сесія реально створена — власник loopback може її спожити.
        Assert.NotNull(h.LinkSessions.TryConsume(result.SessionId, "loopback"));
    }

    [Fact]
    public async Task FinishLink_MapsSessionIdToUri_AndReturnsResponse()
    {
        var h = CreateHarness();
        h.Facade.Setup(d => d.FinishLinkAsync(Uri, "device", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinishLinkResponse("+10000000003"));

        var started = await h.Adapter.StartLink();
        var result = await h.Adapter.FinishLink(started.SessionId, "device");

        Assert.Equal("+10000000003", result.Number);
        // Клієнт передав лише sessionId — адаптер підставив справжній URI сесії в upstream.
        h.Facade.Verify(d => d.FinishLinkAsync(Uri, "device", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinishLink_Unknown_Foreign_Expired_ProduceIdenticalError()
    {
        // --- unknown ---
        var h1 = CreateHarness();
        var unknown = await Assert.ThrowsAsync<RpcErrorException>(
            () => h1.Adapter.FinishLink("no-such-session", "dev"));

        // --- foreign (сесію створила id-a, завершує id-b) ---
        var h2 = CreateHarness();
        string foreignSessionId;
        using (SignalCallContext.Enter(NonAdmin("id-a")))
            foreignSessionId = (await h2.Adapter.StartLink()).SessionId;

        RpcErrorException foreign;
        using (SignalCallContext.Enter(NonAdmin("id-b")))
            foreign = await Assert.ThrowsAsync<RpcErrorException>(
                () => h2.Adapter.FinishLink(foreignSessionId, "dev"));

        // --- expired ---
        var h3 = CreateHarness();
        var expiredSessionId = (await h3.Adapter.StartLink()).SessionId;
        h3.Clock.Advance(LinkSessionStore.SessionTtl);
        var expired = await Assert.ThrowsAsync<RpcErrorException>(
            () => h3.Adapter.FinishLink(expiredSessionId, "dev"));

        // Anti-oracle: код і повідомлення ІДЕНТИЧНІ у всіх трьох випадках.
        Assert.Equal((JsonRpcErrorCode)SignalDevicesRpcAdapter.LinkSessionInvalidCode, unknown.ErrorCode);
        Assert.Equal(unknown.ErrorCode, foreign.ErrorCode);
        Assert.Equal(unknown.ErrorCode, expired.ErrorCode);
        Assert.Equal(unknown.Message, foreign.Message);
        Assert.Equal(unknown.Message, expired.Message);
    }

    [Fact]
    public async Task FinishLink_ForeignIdentity_DoesNotConsumeSession()
    {
        var h = CreateHarness();
        h.Facade.Setup(d => d.FinishLinkAsync(Uri, "dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinishLinkResponse("+10000000003"));

        string sessionId;
        using (SignalCallContext.Enter(NonAdmin("id-a")))
            sessionId = (await h.Adapter.StartLink()).SessionId;

        using (SignalCallContext.Enter(NonAdmin("id-b")))
            await Assert.ThrowsAsync<RpcErrorException>(() => h.Adapter.FinishLink(sessionId, "dev"));

        // Сесія A пережила griefing-спробу — власник A завершує успішно.
        FinishLinkResponse ok;
        using (SignalCallContext.Enter(NonAdmin("id-a")))
            ok = await h.Adapter.FinishLink(sessionId, "dev");

        Assert.Equal("+10000000003", ok.Number);
    }

    [Fact]
    public async Task StartLink_TargetIsKnownBinding_NonAdmin_Denied()
    {
        var h = CreateHarness();
        // Спільний бот-акаунт уже прив'язаний у сторі (owner-x — enrolled identity, FK-передумова binding).
        h.Store.UpsertIdentity(new IdentityRecord("owner-x", "user", [], Start));
        h.Store.UpsertAccountBinding(new AccountBinding("+15550000000", "owner-x", IsPrivate: false, Start));

        RpcErrorException ex;
        using (SignalCallContext.Enter(NonAdmin("intruder")))
            ex = await Assert.ThrowsAsync<RpcErrorException>(
                () => h.Adapter.StartLink("+15550000000"));

        Assert.Equal((JsonRpcErrorCode)AccountIsolationGuard.UnauthorizedErrorCode, ex.ErrorCode);
        // До upstream навіть не дійшли — відмова до StartLinkAsync.
        h.Facade.Verify(d => d.StartLinkAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartLink_TargetIsKnownBinding_Admin_Allowed()
    {
        var h = CreateHarness();
        h.Store.UpsertIdentity(new IdentityRecord("owner-x", "user", [], Start));
        h.Store.UpsertAccountBinding(new AccountBinding("+15550000000", "owner-x", IsPrivate: false, Start));

        StartLinkSessionResponse? result = null;
        using (SignalCallContext.Enter(Admin("admin-1")))
            result = await h.Adapter.StartLink("+15550000000");

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.SessionId));
    }

    [Fact]
    public async Task FinishLink_NewNumber_CreatesPrivateBindToCaller()
    {
        var h = CreateHarness();
        h.Facade.Setup(d => d.FinishLinkAsync(Uri, "dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinishLinkResponse("+19998887777"));

        using (SignalCallContext.Enter(NonAdmin("id-a")))
        {
            var started = await h.Adapter.StartLink();
            await h.Adapter.FinishLink(started.SessionId, "dev");
        }

        var binding = h.Store.GetAccountBinding("+19998887777");
        Assert.NotNull(binding);
        Assert.Equal("id-a", binding!.OwningIdentityId);
        Assert.True(binding.IsPrivate); // R3.5: приватний bind нового власного номера
    }

    [Fact]
    public async Task StartLink_RateLimitExceeded_MapsToRateLimitedError()
    {
        var h = CreateHarness();

        for (var i = 0; i < LinkSessionStore.MaxStartsPerWindow; i++)
            await h.Adapter.StartLink();

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => h.Adapter.StartLink());
        Assert.Equal((JsonRpcErrorCode)AdmissionErrorCodes.RateLimited, ex.ErrorCode);
    }

    [Fact]
    public async Task LinkFlow_NeverLogsDeviceLinkUri()
    {
        var capturing = new CapturingLogger<SignalDevicesRpcAdapter>();
        var h = CreateHarness(capturing);
        h.Facade.Setup(d => d.FinishLinkAsync(Uri, "dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinishLinkResponse("+19998887777"));

        var started = await h.Adapter.StartLink();
        await h.Adapter.FinishLink(started.SessionId, "dev");

        // Redact (task 6): сирий device-link URI не з'являється в жодному лог-рядку адаптера.
        Assert.False(capturing.AnyContains(Uri), "Device-link URI не має потрапляти в лог.");
    }
}
