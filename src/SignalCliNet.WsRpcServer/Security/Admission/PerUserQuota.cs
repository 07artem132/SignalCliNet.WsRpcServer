using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// Per-identity floor / fair-share gate under the aggregate budget (G2). Every active identity is
/// guaranteed a floor of sends per window; beyond the floor, identities compete for the remaining
/// aggregate on a fair basis, so one identity exhausting its fair-share can NEVER starve another's floor.
/// </summary>
/// <remarks>
/// <para>
/// In-memory only (a restart resets the windows — acceptable: this is an anti-starvation throttle, not a
/// security guarantee). Each identity's window resets on the monotonic clock (D15), independent of the
/// wall clock.
/// </para>
/// <para>
/// <b>Fair-share rule.</b> A below-floor send is always admitted. An above-floor send is admitted only
/// while the committed demand of active identities — <c>Σ max(used_i, floor)</c> — is still below the
/// aggregate. That sum reserves the floor of every active identity (even one it has not fully used yet)
/// before granting any identity a unit above its floor.
/// </para>
/// <para>
/// <b>Approximation.</b> "Active" = identities that have appeared in the current window; a floor is
/// reserved only once its identity is seen. Against a flooder that exhausts budget BEFORE any other
/// identity appears, a hard per-identity ceiling of <c>aggregate − floor</c> keeps at least one floor's
/// worth of aggregate headroom for a late-comer. Full fairness for N unknown late-comers is not
/// attempted; the floor guarantee holds while Σ(active identities) × floor ≤ aggregate (documented on
/// <see cref="AdmissionOptions"/>).
/// </para>
/// </remarks>
public sealed class PerUserQuota
{
    private readonly int _floor;
    private readonly int _aggregate;
    private readonly TimeSpan _windowDuration;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<string, UsageWindow> _usage = new(StringComparer.Ordinal);

    /// <summary>Creates the quota from validated <see cref="AdmissionOptions"/>.</summary>
    /// <param name="options">Admission options (floor, aggregate, window length).</param>
    /// <param name="timeProvider">System clock (monotonic windowing, D15).</param>
    public PerUserQuota(IOptions<AdmissionOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        var value = options.Value;
        _floor = value.PerUserFloorPerWindow;
        _aggregate = value.AggregateBudgetPerWindow;
        _windowDuration = TimeSpan.FromMinutes(value.BudgetWindowMinutes);
    }

    /// <summary>
    /// Tries to admit one send for <paramref name="identityId"/>: always within the floor, and above the
    /// floor only while the aggregate has fair-share room over the committed floors of active identities.
    /// </summary>
    public bool TryAdmit(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        lock (_sync)
        {
            ExpireWindows();
            var window = GetOrCreate(identityId);

            // У межах floor — завжди (гарантований мінімум кожній identity).
            if (window.Used < _floor)
            {
                window.Used++;
                return true;
            }

            // Понад floor — fair-share: сумарний попит активних identity (з резервом floor кожній) має
            // лишати запас під агрегатом. committed += 1 після дозволу, тож умова — committed < aggregate.
            long committed = 0;
            foreach (var w in _usage.Values)
                committed += Math.Max(w.Used, _floor);

            if (committed >= _aggregate)
                return false;

            // G2-headroom для ЩЕ НЕ активних identity: Σ-умова вище резервує floor лише «побаченим»
            // у вікні identity, тож heavy-юзер, що флудить ДО першої появи інших, з'їв би весь
            // aggregate-бюджет і їхній floor став би недосяжним (діру зловив DoD 5.2 через реальний
            // шлях). Тому одна identity ніколи не бере понад aggregate − floor: принаймні один
            // floor-обсяг завжди лишається під агрегатом для латекамера. Наближення (повна
            // справедливість для довільного N латекамерів вимагала б знати їх наперед) —
            // задокументовано у <remarks>.
            if (_floor > 0 && window.Used + 1 > _aggregate - _floor)
                return false;

            window.Used++;
            return true;
        }
    }

    /// <summary>Seconds remaining in <paramref name="identityId"/>'s current window (for retry_after), ≥ 1.</summary>
    public int RetryAfterSeconds(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        lock (_sync)
        {
            ExpireWindows();
            var remaining = _usage.TryGetValue(identityId, out var window)
                ? _windowDuration - _timeProvider.GetElapsedTime(window.StartedMonotonic)
                : _windowDuration;
            return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        }
    }

    private UsageWindow GetOrCreate(string identityId)
    {
        if (!_usage.TryGetValue(identityId, out var window))
        {
            window = new UsageWindow { StartedMonotonic = _timeProvider.GetTimestamp() };
            _usage[identityId] = window;
        }

        return window;
    }

    // Скидаємо (видаляємо) вікна identity, що вийшли за монотонну тривалість вікна — наступна поява
    // почне свіже вікно. Заразом тримає словник обмеженим лише активними identity.
    private void ExpireWindows()
    {
        List<string>? expired = null;
        foreach (var (id, window) in _usage)
        {
            if (_timeProvider.GetElapsedTime(window.StartedMonotonic) >= _windowDuration)
                (expired ??= []).Add(id);
        }

        if (expired is null)
            return;

        foreach (var id in expired)
            _usage.Remove(id);
    }

    private sealed class UsageWindow
    {
        public long StartedMonotonic { get; init; }
        public int Used { get; set; }
    }
}
