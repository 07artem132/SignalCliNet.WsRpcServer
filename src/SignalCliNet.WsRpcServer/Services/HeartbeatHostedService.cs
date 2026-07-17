using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Diagnostics;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Security.Admission;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Emits an app-level JSON-RPC <c>heartbeat</c> notification to every active WS client on a sub-25s
/// cadence (task 3.2, C5 server half). Lets an ephemeral client (MV3 Service Worker) keep a persistent
/// socket alive and tell a live socket from a hung one; a run of missed heartbeats is the client's
/// reconnect signal (documented in the client contract). This is NOT a transport WS-ping — it is an
/// app-level notification through the same fan-out infra as Signal events.
/// </summary>
/// <remarks>
/// Additive/gated: heartbeat лише шле нотіфи (поведінка send незмінна). Період — через
/// <see cref="PeriodicTimer"/> на інжектованому <see cref="TimeProvider"/>. Читання
/// <see cref="IGlobalPauseGate.IsPaused"/> на кожен тік також лениво детектить auto-resume (сурфейс
/// pause-стану в payload, task 2.2c).
/// </remarks>
public sealed class HeartbeatHostedService : BackgroundService
{
    private readonly IAppNotifier _notifier;
    private readonly IGlobalPauseGate _pauseGate;
    private readonly TimeProvider _timeProvider;
    private readonly HeartbeatOptions _options;
    private readonly ILogger<HeartbeatHostedService> _logger;
    private long _seq;

    /// <summary>Creates the heartbeat service from validated <see cref="HeartbeatOptions"/>.</summary>
    public HeartbeatHostedService(
        IAppNotifier notifier,
        IGlobalPauseGate pauseGate,
        TimeProvider timeProvider,
        IOptions<HeartbeatOptions> options,
        ILogger<HeartbeatHostedService> logger)
    {
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _pauseGate = pauseGate ?? throw new ArgumentNullException(nameof(pauseGate));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Heartbeat вимкнено (Server:Heartbeat:Enabled=false) — нотіфи не шлемо.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        using var timer = new PeriodicTimer(interval, _timeProvider);
        _logger.LogInformation("Heartbeat стартовано (інтервал {Interval}s).", _options.IntervalSeconds);

        // WaitForNextTickAsync кине OperationCanceledException на зупинці — це нормальний вихід (host обробить).
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                EmitOnce();
            }
            catch (Exception ex)
            {
                // Межа довготривалого циклу: один поганий тік не має вбити heartbeat (log + continue).
                _logger.LogError(ex, "Heartbeat-тік впав — продовжуємо.");
            }
        }
    }

    /// <summary>
    /// Builds and fans out one heartbeat notification (PII-free: seq + timestamp + pause-state) and counts
    /// the metric. Extracted so the payload/seq/pause-surfacing is unit-testable without the interval loop.
    /// </summary>
    internal void EmitOnce()
    {
        var seq = Interlocked.Increment(ref _seq);
        var payload = new HeartbeatNotification(
            seq,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            _pauseGate.IsPaused);

        _notifier.Broadcast("heartbeat", payload);
        AppDiagnostics.Heartbeat();
    }
}
