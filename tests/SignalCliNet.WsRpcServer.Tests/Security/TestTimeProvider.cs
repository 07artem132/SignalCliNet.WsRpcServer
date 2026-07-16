namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Простий керований <see cref="TimeProvider"/> з фіксованим часом (без зовнішнього пакета
/// FakeTimeProvider). Тести рухають час явно через <see cref="Advance"/>.
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
