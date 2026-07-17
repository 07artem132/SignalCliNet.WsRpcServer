namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Простий керований <see cref="TimeProvider"/> з фіксованим часом (без зовнішнього пакета
/// FakeTimeProvider). Тести рухають час явно через <see cref="Advance"/>.
/// </summary>
/// <remarks>
/// Монотонний годинник прив'язаний до того ж <c>_now</c>: <see cref="GetTimestamp"/> повертає UTC-тіки,
/// а <see cref="TimestampFrequency"/> = <see cref="TimeSpan.TicksPerSecond"/>, тож базовий
/// <see cref="TimeProvider.GetElapsedTime(long)"/> дає рівно пройдений <see cref="Advance"/>-інтервал.
/// Це дозволяє тестувати монотонні вікна (D15) тим самим <see cref="Advance"/>, що й wall-clock.
/// </remarks>
internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _now += by;
}
