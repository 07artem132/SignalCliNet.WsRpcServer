namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// A soft, GLOBAL rate cap on invite-redeem attempts (add-invites-admin, task 1.2): at most
/// <see cref="MaxPerWindow"/> redeem attempts across the whole server per rolling minute. Adds friction
/// to online brute-forcing of invite codes on top of the per-code attempt cap (G12).
/// </summary>
/// <remarks>
/// <b>Чому «м'який» і глобальний.</b> Ліміт свідомо soft: перевищення дає лише ТИМЧАСОВУ відмову, що
/// самоочищається зі зсувом вікна (клієнт ретраїть за <see cref="RetryAfterSeconds"/>), — НЕ бан і не
/// persistent-локаут. Жорсткий глобальний cap перетворив би анти-brute-force-контроль на важіль
/// shared-fate DoS: один зловмисник вичерпав би спільний бюджет і заблокував онбординг усім. Тому cap
/// щедрий (дефолт 20/хв — редемпшн рідкісний), а справжня стійкість до підбору — у per-code cap+ентропії
/// коду; це вікно лише зрізає пікову частоту.
///
/// Монотонне ковзне вікно (патерн <see cref="RenewalRateLimiter"/>/<see cref="LinkSessionStore"/>): при
/// кожній спробі відкидаємо мітки, старші за вікно. Singleton, лише в пам'яті процесу (рестарт скидає
/// вікно — прийнятно: це anti-abuse throttle, а не безпекова гарантія). <see cref="TimeProvider"/>
/// інжектимо для тестабельності.
/// </remarks>
public sealed class InviteRedemptionRateLimiter
{
    /// <summary>Default soft cap on redeem attempts per rolling window (server-wide).</summary>
    public const int DefaultMaxPerWindow = 20;

    /// <summary>The rolling window (1 minute).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly int _max;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Queue<DateTimeOffset> _attempts = new();

    /// <summary>Creates the limiter.</summary>
    /// <param name="timeProvider">System clock (tests substitute a fixed clock).</param>
    /// <param name="maxPerWindow">Soft cap per window (defaults to <see cref="DefaultMaxPerWindow"/>).</param>
    public InviteRedemptionRateLimiter(TimeProvider timeProvider, int maxPerWindow = DefaultMaxPerWindow)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerWindow, 1);
        _max = maxPerWindow;
    }

    /// <summary>The configured soft cap per window.</summary>
    public int MaxPerWindow => _max;

    /// <summary>Suggested client retry delay when throttled (the window length, in seconds).</summary>
    public int RetryAfterSeconds() => (int)Window.TotalSeconds;

    /// <summary>
    /// Tries to admit one redeem attempt within the global rolling-window budget.
    /// </summary>
    /// <returns><c>true</c> if a slot was available (and now consumed); <c>false</c> if the window is full.</returns>
    public bool TryAcquire()
    {
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            // Відкидаємо мітки, що вийшли за межі ковзного вікна.
            while (_attempts.Count > 0 && now - _attempts.Peek() >= Window)
                _attempts.Dequeue();

            if (_attempts.Count >= _max)
                return false;

            _attempts.Enqueue(now);
            return true;
        }
    }
}
