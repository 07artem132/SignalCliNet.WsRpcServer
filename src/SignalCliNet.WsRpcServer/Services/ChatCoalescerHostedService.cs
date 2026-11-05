using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security.Idempotency;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Drives <see cref="ChatCoalescer"/> (add-team-notify): flushes ended windows once per second, purges
/// chat-scoped dedup rows older than <c>Notify:DedupTtlHours</c> hourly, and flushes every queued batch on
/// graceful stop (a crash loses the in-memory queue — documented).
/// </summary>
public sealed class ChatCoalescerHostedService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    private readonly ChatCoalescer _coalescer;
    private readonly IDurableStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly NotifyOptions _options;
    private readonly ILogger<ChatCoalescerHostedService> _logger;
    private DateTimeOffset _nextPurge = DateTimeOffset.MinValue;

    /// <summary>Creates the service.</summary>
    public ChatCoalescerHostedService(
        ChatCoalescer coalescer,
        IDurableStore store,
        TimeProvider timeProvider,
        IOptions<NotifyOptions> options,
        ILogger<ChatCoalescerHostedService> logger)
    {
        _coalescer = coalescer ?? throw new ArgumentNullException(nameof(coalescer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Межа довготривалого циклу: один поганий тік не зупиняє flush (log + continue).
                _logger.LogError(ex, "Coalescer-тік впав — продовжуємо.");
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _coalescer.FlushAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Coalescer: зупинка перервала відправку черги — частину об'єднаних повідомлень втрачено.");
        }
    }

    /// <summary>One tick: flush ended windows, then purge stale chat dedup rows when due.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completing after the tick.</returns>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _coalescer.FlushDueAsync(cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        if (now < _nextPurge)
            return;

        _nextPurge = now + PurgeEvery;
        var purged = _store.PurgeSendDedup(
            IdempotentSendCoordinator.ChatScopePrefix, now - TimeSpan.FromHours(_options.DedupTtlHours));
        if (purged > 0)
            _logger.LogInformation("Coalescer: прибрано {Count} застарілих dedup-рядків чатів.", purged);
    }
}
