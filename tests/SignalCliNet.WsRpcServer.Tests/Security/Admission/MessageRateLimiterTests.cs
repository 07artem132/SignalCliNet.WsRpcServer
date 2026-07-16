using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Пінить <see cref="MessageRateLimiter"/> (token-bucket, task 3.2): burst = ємність, монотонний рефіл,
/// перевищення й <c>retry_after</c>. Час рухаємо явно через <see cref="TestTimeProvider"/> (D15).
/// </summary>
public class MessageRateLimiterTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static (MessageRateLimiter Limiter, TestTimeProvider Time) New(int capacity)
    {
        var time = new TestTimeProvider(FixedTime);
        return (new MessageRateLimiter(capacity, TimeSpan.FromMinutes(1), time), time);
    }

    [Fact]
    public void Burst_UpToCapacity_ThenRefuses()
    {
        var (limiter, _) = New(5);

        // Свіжий бакет = повна ємність: рівно 5 кадрів проходять поспіль.
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryConsume());

        Assert.False(limiter.TryConsume()); // 6-й — понад burst
    }

    [Fact]
    public void Refill_IsMonotonic_AndProportionalToElapsedTime()
    {
        var (limiter, time) = New(60); // 60/хв ⇒ 1 токен/с

        // Вичерпуємо весь бакет.
        for (var i = 0; i < 60; i++)
            Assert.True(limiter.TryConsume());
        Assert.False(limiter.TryConsume());

        // Проходить 10с монотонного часу → доливається ~10 токенів.
        time.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryConsume());
        Assert.False(limiter.TryConsume());
    }

    [Fact]
    public void Refill_CapsAtCapacity_NoUnboundedAccrual()
    {
        var (limiter, time) = New(5);

        // Довге мовчання не накопичує понад ємність — burst лишається = 5.
        time.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryConsume());
        Assert.False(limiter.TryConsume());
    }

    [Fact]
    public void RetryAfter_IsPositive_WhenExhausted()
    {
        var (limiter, _) = New(60); // 1 токен/с

        for (var i = 0; i < 60; i++)
            Assert.True(limiter.TryConsume());
        Assert.False(limiter.TryConsume());

        // Порожній бакет: до наступного токена ~1с → retry ≥ 1.
        Assert.True(limiter.RetryAfterSeconds() >= 1);
    }

    [Fact]
    public void RetryAfter_ScalesWithSlowRefill()
    {
        var (limiter, _) = New(1); // 1/хв ⇒ ~1 токен на 60с

        Assert.True(limiter.TryConsume());
        Assert.False(limiter.TryConsume());

        // На повільному рефілі до наступного токена — близько вікна (секунди, а не 1).
        Assert.True(limiter.RetryAfterSeconds() > 1);
    }
}
