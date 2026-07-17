using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="RevocationBroadcaster"/> (task 1.4): publish → callback, dispose відписує, кілька
/// підписників, стійкість до збійного підписника.
/// </summary>
public class RevocationBroadcasterTests
{
    private static RevocationBroadcaster Create() => new(NullLogger<RevocationBroadcaster>.Instance);

    [Fact]
    public void Publish_InvokesSubscriberForThatIdentity()
    {
        var broadcaster = Create();
        var fired = 0;
        broadcaster.Subscribe("id-1", () => fired++);

        broadcaster.Publish("id-1");

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Publish_DoesNotInvokeOtherIdentities()
    {
        var broadcaster = Create();
        var fired = 0;
        broadcaster.Subscribe("id-1", () => fired++);

        broadcaster.Publish("id-2");

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var broadcaster = Create();
        var fired = 0;
        var subscription = broadcaster.Subscribe("id-1", () => fired++);

        subscription.Dispose();
        broadcaster.Publish("id-1");

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Publish_InvokesAllSubscribersOfIdentity()
    {
        var broadcaster = Create();
        var a = 0;
        var b = 0;
        broadcaster.Subscribe("id-1", () => a++);
        broadcaster.Subscribe("id-1", () => b++);

        broadcaster.Publish("id-1");

        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void Publish_UnknownIdentity_IsNoOp()
    {
        var broadcaster = Create();

        // Не кидає, коли підписників немає.
        broadcaster.Publish("nobody");
    }

    [Fact]
    public void Publish_FaultySubscriber_DoesNotBlockOthers()
    {
        var broadcaster = Create();
        var reached = false;
        broadcaster.Subscribe("id-1", () => throw new InvalidOperationException("boom"));
        broadcaster.Subscribe("id-1", () => reached = true);

        // Межа фан-ауту: збій одного підписника логується й не зриває розсилку іншим.
        broadcaster.Publish("id-1");

        Assert.True(reached);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var broadcaster = Create();
        var fired = 0;
        var subscription = broadcaster.Subscribe("id-1", () => fired++);

        subscription.Dispose();
        subscription.Dispose(); // друга відписка не кидає

        broadcaster.Publish("id-1");
        Assert.Equal(0, fired);
    }
}
