using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Persistence;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Events;

/// <summary>
/// Пінить per-account receive-роутер S11 (add-group-claim-receive, task 1.4): own-linked (приватний
/// binding) → власнику; shared-bot (без власника АБО admin-owned не-приватний) → сканеру; вимкнено →
/// passthrough (поточна поведінка).
/// </summary>
public sealed class PerAccountReceiveRouterTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-router-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;

    public PerAccountReceiveRouterTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("owner-a", "user", [], Start));
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], Start));
    }

    private PerAccountReceiveRouter NewRouter(bool enabled) =>
        new(_store, Options.Create(new GroupClaimOptions { Enabled = enabled }));

    [Fact]
    public void Disabled_AlwaysPassthrough()
    {
        _store.UpsertAccountBinding(new AccountBinding("+15550000001", "owner-a", IsPrivate: true, Start));
        var router = NewRouter(enabled: false);

        Assert.Equal(ReceiveScope.Passthrough, router.Route("+15550000001").Scope);
    }

    [Fact]
    public void OwnLinked_PrivateBinding_RoutesToOwner()
    {
        _store.UpsertAccountBinding(new AccountBinding("+15550000001", "owner-a", IsPrivate: true, Start));
        var router = NewRouter(enabled: true);

        var route = router.Route("+15550000001");

        Assert.Equal(ReceiveScope.OwnLinked, route.Scope);
        Assert.Equal("owner-a", route.OwningIdentityId);
    }

    [Fact]
    public void SharedBot_NonPrivateBinding_RoutesToScanner()
    {
        _store.UpsertAccountBinding(new AccountBinding("+15550009999", "admin", IsPrivate: false, Start));
        var router = NewRouter(enabled: true);

        Assert.Equal(ReceiveScope.SharedBot, router.Route("+15550009999").Scope);
    }

    [Fact]
    public void SharedBot_NoBinding_RoutesToScanner()
    {
        var router = NewRouter(enabled: true);

        Assert.Equal(ReceiveScope.SharedBot, router.Route("+15550001234").Scope);
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
