using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Diagnostics;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Model;

namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// The real, signal-driven global-pause gate (task 2.2). Replaces <see cref="NoopGlobalPauseGate"/> in DI.
/// A rate-limit (<c>-5</c>) or captcha (<c>-6</c>) on the send path calls <see cref="PauseFor"/>; while the
/// pause is live, <see cref="AdmissionControl"/>'s first W16 check denies every send. The pause auto-resumes
/// on a MONOTONIC deadline (D15 — immune to wall-clock jumps), with jittered, escalating backoff so a
/// persistent upstream throttle doesn't turn into a tight retry loop (anti-ban).
/// </summary>
/// <remarks>
/// <para>
/// <b>Чому -5/-6 глобальні, а -4 ні.</b> Rate-limit і captcha — це стан АКАУНТА-відправника проти
/// Signal-сервера: будь-який наступний send через того самого бота отримає те саме → пауза мусить бути
/// глобальною (спільний бот), інакше клієнти «долбитимуть» і поглиблять бан. Untrusted-identity (-4) — це
/// стан КОНКРЕТНОГО отримувача (зміна ключа); він per-recipient і actionable (verify safety number), тож
/// НЕ паузить бота — інакше один «поганий» отримувач заблокував би відправку всім.
/// </para>
/// <para>
/// <b>Surface (task 2.2a-c).</b> Транзиції сурфейсяться: (a) <c>bot.paused</c>/<c>bot.resumed</c> нотіфи
/// клієнтам (reason+retryAfter, БЕЗ PII) через <see cref="IAppNotifier"/>; (b) метрика
/// <c>wsrpc.app.global_pause</c>; (c) прапорець <c>paused</c> у heartbeat-payload. Resume детектується
/// лениво: будь-яке читання <see cref="IsPaused"/> (admission на кожен send / heartbeat-тік) після
/// deadline перемикає стан і фаєрить side-effects (метрика + <c>bot.resumed</c>). Нотіфи шлемо ПОЗА локом.
/// </para>
/// </remarks>
public sealed class GlobalPauseGate : IGlobalPauseGate
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GlobalPauseGate> _logger;
    private readonly IAppNotifier? _notifier;
    private readonly int _basePauseSeconds;
    private readonly int _maxPauseSeconds;
    private readonly object _sync = new();

    private bool _active;
    private long _pausedAtMonotonic;      // монотонна мітка початку паузи
    private TimeSpan _pauseDuration;      // ефективна тривалість (з ескалацією/джитером)
    private string _reason = string.Empty;
    private int _consecutive;             // лічильник ескалації в межах «серії»

    /// <summary>Creates the pause gate from validated <see cref="GlobalPauseOptions"/>.</summary>
    /// <param name="options">Pause backoff options (base / max seconds).</param>
    /// <param name="timeProvider">System clock — monotonic for the auto-resume deadline (D15).</param>
    /// <param name="logger">Logger (receives reason/retry only — never recipient/message data).</param>
    /// <param name="notifier">Optional app-notifier for <c>bot.paused</c>/<c>bot.resumed</c> fan-out.</param>
    public GlobalPauseGate(
        IOptions<GlobalPauseOptions> options,
        TimeProvider timeProvider,
        ILogger<GlobalPauseGate> logger,
        IAppNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notifier = notifier;

        var value = options.Value;
        _basePauseSeconds = value.BasePauseSeconds;
        _maxPauseSeconds = value.MaxPauseSeconds;
    }

    /// <inheritdoc />
    public bool IsPaused
    {
        get
        {
            PauseStateNotification? resumeNotify;
            bool active;
            lock (_sync)
            {
                resumeNotify = RefreshNoLock();
                active = _active;
            }

            FireResume(resumeNotify);
            return active;
        }
    }

    /// <inheritdoc />
    public int RetryAfterSeconds
    {
        get
        {
            PauseStateNotification? resumeNotify;
            int seconds;
            lock (_sync)
            {
                resumeNotify = RefreshNoLock();
                seconds = _active ? RemainingSecondsNoLock() : 0;
            }

            FireResume(resumeNotify);
            return seconds;
        }
    }

    /// <inheritdoc />
    public void PauseFor(TimeSpan minimumDuration, string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();

        PauseStateNotification pauseNotify;
        bool transitionedToPaused;
        lock (_sync)
        {
            // Спершу підчистимо застарілу паузу (щоб ескалація рахувала «серію», а не давню подію).
            RefreshNoLock();

            // Ескалація: кожен новий тригер поки активні (або одразу після) ×2, з capом на MaxPauseSeconds.
            _consecutive = Math.Min(_consecutive + 1, 16);
            var baseSeconds = Math.Max(1, (int)Math.Ceiling(minimumDuration.TotalSeconds));
            var escalated = ScaleWithEscalation(baseSeconds, _consecutive);
            var jittered = ApplyJitter(escalated);
            var effective = TimeSpan.FromSeconds(Math.Min(jittered, _maxPauseSeconds));

            transitionedToPaused = !_active;
            var now = _timeProvider.GetTimestamp();

            if (_active)
            {
                // Уже паузнуто — подовжуємо deadline лише вперед (не скорочуємо).
                var elapsed = _timeProvider.GetElapsedTime(_pausedAtMonotonic);
                var remaining = _pauseDuration - elapsed;
                if (effective > remaining)
                {
                    _pausedAtMonotonic = now;
                    _pauseDuration = effective;
                }
            }
            else
            {
                _pausedAtMonotonic = now;
                _pauseDuration = effective;
                _active = true;
            }

            _reason = reason;
            pauseNotify = new PauseStateNotification(reason, RemainingSecondsNoLock());
        }

        if (transitionedToPaused)
            AppDiagnostics.GlobalPauseActivated();

        // privacy: логуємо лише reason/retry — жодних recipient/тіл.
        _logger.LogWarning(
            "Global-pause активовано ({Reason}); retry_after≈{RetryAfter}s (escalated).",
            pauseNotify.Reason, pauseNotify.RetryAfterSeconds);

        _notifier?.Broadcast("bot.paused", pauseNotify);
    }

    // Лениво знімає паузу, якщо монотонний deadline минув. Повертає resume-нотіф для фаєрингу ПОЗА локом
    // (null — якщо транзиції не було). Метрику decrement робимо тут (під локом), нотіф — назовні.
    private PauseStateNotification? RefreshNoLock()
    {
        if (!_active)
            return null;

        if (_timeProvider.GetElapsedTime(_pausedAtMonotonic) < _pauseDuration)
            return null;

        _active = false;
        _consecutive = 0;                 // серія завершилась — ескалація скидається
        var reason = _reason;
        _reason = string.Empty;
        AppDiagnostics.GlobalPauseDeactivated();
        return new PauseStateNotification(reason, 0);
    }

    private int RemainingSecondsNoLock()
    {
        var remaining = _pauseDuration - _timeProvider.GetElapsedTime(_pausedAtMonotonic);
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
    }

    private void FireResume(PauseStateNotification? resumeNotify)
    {
        if (resumeNotify is null)
            return;

        _logger.LogInformation("Global-pause знято (auto-resume).");
        _notifier?.Broadcast("bot.resumed", resumeNotify);
    }

    // Ескалація base × 2^(consecutive-1), захищена від overflow (cap застосовується викликачем).
    private static double ScaleWithEscalation(int baseSeconds, int consecutive)
    {
        var factor = Math.Pow(2, Math.Min(consecutive - 1, 16));
        return baseSeconds * factor;
    }

    // Джитер [0, +25%) — розводить синхронні ретраї (G13). Random.Shared потокобезпечний.
    private static double ApplyJitter(double seconds) =>
        seconds * (1.0 + Random.Shared.NextDouble() * 0.25);
}
