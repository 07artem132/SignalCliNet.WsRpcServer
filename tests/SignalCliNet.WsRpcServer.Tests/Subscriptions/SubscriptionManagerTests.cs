using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Subscriptions;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Subscriptions;

/// <summary>
/// Регрес-тести міграції на JSON-RPC.NET 2.x (<c>*Core</c>-патерн, generic-менеджер,
/// flags↔collection на межі). Пінять, що база серіалізує мутації без re-entrant-deadlock
/// і що <see cref="SignalEventTypes"/>-bitmask коректно відновлюється з колекції.
/// </summary>
public class SubscriptionManagerTests
{
    private static SubscriptionManager CreateManager(Mock<ISignalEventService> events)
        => new(events.Object, NullLogger<SubscriptionManager>.Instance);

    private static Mock<ISignalEventService> EventsReturning(int signalSubscriptionId)
    {
        var events = new Mock<ISignalEventService>();
        events.Setup(e => e.SubscribeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscribeReceiveResponse(signalSubscriptionId));
        events.Setup(e => e.UnsubscribeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnsubscribeReceiveResponse());
        return events;
    }

    private static BaseSignalEventArgs EventArgs(int subscriptionId, string account)
        => new(subscriptionId, account, null, null, null, null, null, 0, 0, 0);

    [Fact]
    public async Task Subscribe_ValidInput_SubscribesViaFacadeAndReturnsId()
    {
        var events = EventsReturning(42);
        var manager = CreateManager(events);

        var id = await manager.Subscribe(Guid.NewGuid(), "+10000000001", [SignalEventTypes.TextMessages]);

        Assert.True(id > 0);
        events.Verify(e => e.SubscribeAsync("+10000000001", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Subscribe_OverPerClientCap_ThrowsRpcError()
    {
        var events = EventsReturning(42);
        var manager = CreateManager(events);
        var client = Guid.NewGuid();

        // MaxSubscriptionsPerClient default = 10
        for (var i = 0; i < 10; i++)
            await manager.Subscribe(client, "+1000000000" + i, [SignalEventTypes.TextMessages]);

        await Assert.ThrowsAsync<RpcErrorException>(
            () => manager.Subscribe(client, "+19999999999", [SignalEventTypes.TextMessages]));
    }

    [Fact]
    public async Task Subscribe_EmptyEventTypes_ThrowsArgumentException()
    {
        var events = EventsReturning(42);
        var manager = CreateManager(events);

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.Subscribe(Guid.NewGuid(), "+10000000001", [SignalEventTypes.None]));
    }

    [Fact]
    public async Task Subscribe_Concurrent_NoDeadlock_AllComplete()
    {
        // Регрес: *Core НЕ повторно захоплює OperationLock (re-entrant SemaphoreSlim(1,1) = deadlock).
        var events = EventsReturning(42);
        var manager = CreateManager(events);

        var tasks = Enumerable.Range(0, 20)
            .Select(i => manager.Subscribe(Guid.NewGuid(), "+1000000" + i.ToString("D4"),
                [SignalEventTypes.TextMessages]))
            .ToArray();

        // Якщо база самозаблокувалась — WhenAll зависне; таймаут перетворює це на падіння тесту.
        var ids = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(20, ids.Distinct().Count());
    }

    [Fact]
    public async Task GetClientsForEvent_AfterSubscribe_ReturnsSubscribedClient()
    {
        var events = EventsReturning(42);
        var manager = CreateManager(events);
        var client = Guid.NewGuid();

        await manager.Subscribe(client, "+10000000001", [SignalEventTypes.TextMessages]);

        var clients = manager.GetClientsForEvent(
            EventArgs(42, "+10000000001"), SignalEventTypes.TextMessages);

        Assert.Contains(client, clients);
    }

    [Fact]
    public async Task Subscribe_MultipleEventTypesInCollection_AreOrCombined()
    {
        // Пінить flags↔collection: колекція [Text, Reactions] має згорнутись у єдиний bitmask,
        // тож подія будь-якого з типів матчиться.
        var events = EventsReturning(42);
        var manager = CreateManager(events);
        var client = Guid.NewGuid();

        await manager.Subscribe(client, "+10000000001",
            [SignalEventTypes.TextMessages, SignalEventTypes.Reactions]);

        var forText = manager.GetClientsForEvent(EventArgs(42, "+10000000001"), SignalEventTypes.TextMessages);
        var forReactions = manager.GetClientsForEvent(EventArgs(42, "+10000000001"), SignalEventTypes.Reactions);

        Assert.Contains(client, forText);
        Assert.Contains(client, forReactions);
    }
}
