using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// Reserve-then-send aggregate send budget (W13 / G3 / D15). Combines an in-memory atomic counter (race
/// safety) with durable block-reservation (crash safety) so the aggregate ceiling can NEVER be
/// over-sent: a unit is handed out only when it is already covered by a durably-reserved block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Race safety (G3).</b> <see cref="TryReserveUnit"/> does the whole read-modify-write (including the
/// durable block-reservation) under one lock — no check-then-act escapes it. N concurrent calls under a
/// budget of 1 therefore yield exactly one <c>true</c>.
/// </para>
/// <para>
/// <b>Crash safety / forfeit (W13).</b> A block covers K units; the first is spent immediately and the
/// remaining K-1 live only in <see cref="_available"/> (memory). On cold start NOTHING is restored:
/// the durably-reserved blocks stay in the table (so the next reservation resumes at the next
/// <c>block_index</c> and the ceiling still holds), while the un-spent in-memory remainder of the last
/// block is forfeited. Net effect: over-send is impossible ever; under-send is ≤ K per crash.
/// </para>
/// <para>
/// <b>Clock split (D15).</b> The window transition (refill moment) is detected on the MONOTONIC clock
/// (<see cref="TimeProvider.GetElapsedTime(long)"/> vs <see cref="_windowStartedMonotonic"/>) — immune to
/// wall-clock jumps. The wall clock is used ONLY to stamp <see cref="_windowKey"/> (the durable window
/// label), captured once per monotonic window and held constant for its whole duration. The two are
/// deliberately independent: correctness comes from the monotonic timer plus durable rows keyed by
/// <see cref="_windowKey"/>; the label is a best-effort human-readable stamp that may drift from the
/// wall-clock grid.
/// </para>
/// </remarks>
public sealed class ReserveThenSendBudget
{
    private readonly IDurableStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReserveThenSendBudget> _logger;

    private readonly int _aggregateBudget;
    private readonly int _blockSize;
    private readonly TimeSpan _windowDuration;
    private readonly object _sync = new();

    private long _windowStartedMonotonic;
    private string _windowKey;
    private int _available;

    /// <summary>Creates the budget from validated <see cref="AdmissionOptions"/>.</summary>
    /// <param name="store">The durable reservation store (block rows keyed by window + index).</param>
    /// <param name="options">Admission options (aggregate ceiling, block size, window length).</param>
    /// <param name="timeProvider">System clock — monotonic for windowing, wall-clock for the label.</param>
    /// <param name="logger">Logger (never receives recipient/message data).</param>
    public ReserveThenSendBudget(
        IDurableStore store,
        IOptions<AdmissionOptions> options,
        TimeProvider timeProvider,
        ILogger<ReserveThenSendBudget> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var value = options.Value;
        _aggregateBudget = value.AggregateBudgetPerWindow;
        _blockSize = value.ReserveBlockSize;
        _windowDuration = TimeSpan.FromMinutes(value.BudgetWindowMinutes);

        _windowStartedMonotonic = _timeProvider.GetTimestamp();
        _windowKey = ComputeWindowKey(_timeProvider.GetUtcNow());
    }

    /// <summary>The current window's durable label (recomputed on each monotonic window transition).</summary>
    public string CurrentWindowKey
    {
        get
        {
            lock (_sync)
            {
                EnsureCurrentWindow();
                return _windowKey;
            }
        }
    }

    /// <summary>
    /// Atomically reserves one send unit against the aggregate budget (G3). Returns <c>true</c> if a unit
    /// was granted; <c>false</c> if the window's ceiling is exhausted.
    /// </summary>
    public bool TryReserveUnit()
    {
        lock (_sync)
        {
            EnsureCurrentWindow();

            // Одиниця вже покрита раніше взятим durable-блоком — просто споживаємо її.
            if (_available > 0)
            {
                _available--;
                return true;
            }

            // Залишку немає — пробуємо взяти НОВИЙ durable-блок. Стеля рахується по блоках (гранула K):
            // ефективна межа = ceil(aggregate / K) × K.
            var blocks = _store.CountBudgetBlocks(_windowKey);
            if ((long)blocks * _blockSize >= _aggregateBudget)
                return false;

            // Резервуємо наступний індекс (PK-конфлікт у сторі кинув би — захист від double-reserve).
            _store.InsertBudgetBlock(_windowKey, blocks, _blockSize, _timeProvider.GetUtcNow());
            _available += _blockSize - 1; // одну одиницю блоку споживаємо зараз
            return true;
        }
    }

    /// <summary>Seconds remaining in the current window (for the S9 in-band <c>retry_after</c>), ≥ 1.</summary>
    public int RetryAfterSeconds()
    {
        lock (_sync)
        {
            EnsureCurrentWindow();
            var remaining = _windowDuration - _timeProvider.GetElapsedTime(_windowStartedMonotonic);
            return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        }
    }

    // Детект переходу вікна монотонним годинником (D15). Скидання форфейтить невитрачений залишок.
    private void EnsureCurrentWindow()
    {
        if (_timeProvider.GetElapsedTime(_windowStartedMonotonic) < _windowDuration)
            return;

        _windowStartedMonotonic = _timeProvider.GetTimestamp();
        _windowKey = ComputeWindowKey(_timeProvider.GetUtcNow());
        _available = 0; // форфейт (W13): невитрачені одиниці попереднього вікна не переносяться
    }

    // Мітка вікна з wall-clock: floor поточного UTC до BudgetWindowMinutes-сітки від Unix-епохи.
    private string ComputeWindowKey(DateTimeOffset now)
    {
        var minutesSinceEpoch = (long)Math.Floor((now.UtcDateTime - DateTime.UnixEpoch).TotalMinutes);
        var windowMinutes = (long)_windowDuration.TotalMinutes;
        var windowStart = DateTime.UnixEpoch.AddMinutes(minutesSinceEpoch / windowMinutes * windowMinutes);
        return windowStart.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
    }
}
