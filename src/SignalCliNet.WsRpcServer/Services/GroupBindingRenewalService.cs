using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCli.Interfaces.Signal;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>Outcome counters of one renewal pass (diagnostics + tests).</summary>
/// <param name="Renewed">Bindings whose expiry was moved forward.</param>
/// <param name="AnchorGone">Bindings left to expire because no shared bot sees the anchor in the group.</param>
/// <param name="Deferred">Bindings skipped this pass because membership could not be determined (retried next pass).</param>
public readonly record struct BindingRenewalResult(int Renewed, int AnchorGone, int Deferred);

/// <summary>
/// Renews group bindings before their backstop TTL runs out (add-binding-auto-renew). Once an active binding
/// (per-identity or team alias) enters the renewal window, the service re-checks the same condition the claim
/// established, <c>anchorAci ∈ members(group)</c> as seen by a shared-bot account, and on success moves its expiry
/// to <c>now + BindingTtlHours</c>.
/// </summary>
/// <remarks>
/// Поновлення не послаблює гейт: anchor-перевірка та сама, що на кожному send (<see cref="GroupMemberCache"/>).
/// Anchor не член жодної shared-bot групи → прив'язку НЕ поновлюємо і НЕ відкликаємо (з кількома ботами «не член»
/// на одному акаунті ще не доказ); вона спливе сама, а send-gate відкличе її на першій спробі. Транзієнт (Unknown)
/// або збій <c>listAccounts</c> → пропускаємо, наступний прохід спробує ще раз (вікно ≫ інтервалу). Запис —
/// умовний <c>UPDATE</c> (<see cref="IDurableStore.TryExtendGroupBinding"/>), тож паралельний revoke/перепривʼязка
/// завжди виграє. Інертний, коли <see cref="GroupClaimOptions.Enabled"/> або
/// <see cref="GroupClaimOptions.AutoRenewBindings"/> вимкнено.
/// </remarks>
public sealed class GroupBindingRenewalService : BackgroundService
{
    // Верхня межа інтервалу проходу: годину. Для коротких вікон — частіше (половина вікна), щоб прив'язка не
    // спливла між двома проходами.
    private static readonly TimeSpan MaxPassInterval = TimeSpan.FromHours(1);

    private readonly IDurableStore _store;
    private readonly ISignalAccounts _signalAccounts;
    private readonly GroupMemberCache _memberCache;
    private readonly GroupSendGate _sendGate;
    private readonly GroupClaimOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GroupBindingRenewalService> _logger;

    /// <summary>Creates the renewal service.</summary>
    public GroupBindingRenewalService(
        IDurableStore store,
        ISignalAccounts signalAccounts,
        GroupMemberCache memberCache,
        GroupSendGate sendGate,
        IOptions<GroupClaimOptions> options,
        TimeProvider timeProvider,
        ILogger<GroupBindingRenewalService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _signalAccounts = signalAccounts ?? throw new ArgumentNullException(nameof(signalAccounts));
        _memberCache = memberCache ?? throw new ArgumentNullException(nameof(memberCache));
        _sendGate = sendGate ?? throw new ArgumentNullException(nameof(sendGate));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether renewal runs at all (group-claim enabled AND auto-renew on).</summary>
    public bool Active => _options.Enabled && _options.AutoRenewBindings;

    /// <summary>The effective renewal window: <c>BindingRenewWindowHours</c>, capped at half the binding TTL.</summary>
    public TimeSpan RenewWindow
    {
        get
        {
            var window = TimeSpan.FromHours(_options.BindingRenewWindowHours);
            var halfTtl = TimeSpan.FromHours(_options.BindingTtlHours) / 2;
            return window < halfTtl ? window : halfTtl;
        }
    }

    /// <summary>The pass interval: half the renewal window, at most one hour.</summary>
    public TimeSpan PassInterval
    {
        get
        {
            var half = RenewWindow / 2;
            return half < MaxPassInterval ? half : MaxPassInterval;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Active)
        {
            _logger.LogInformation("Автоподовження прив'язок груп вимкнено — сервіс неактивний.");
            return;
        }

        _logger.LogInformation(
            "Автоподовження прив'язок груп стартовано (вікно {WindowHours} год, прохід кожні {IntervalMinutes} хв).",
            RenewWindow.TotalHours, PassInterval.TotalMinutes);

        using var timer = new PeriodicTimer(PassInterval, _timeProvider);
        do
        {
            try
            {
                await RenewOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Межа довготривалого циклу: один поганий прохід не вбиває сервіс (log + наступний тік).
                _logger.LogError(ex, "Прохід автоподовження прив'язок упав — спробуємо на наступному тіку.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs one renewal pass over every active binding inside the renewal window. Extracted from the timer loop so
    /// tests drive it directly.
    /// </summary>
    /// <exception cref="Exception">Propagates a failure of <c>listAccounts</c> (the pass is skipped as a whole).</exception>
    internal async Task<BindingRenewalResult> RenewOnceAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var horizon = now + RenewWindow;
        var groupBindings = _store.GetGroupBindingsExpiringBefore(now, horizon);
        var teamBindings = _store.GetTeamBindingsExpiringBefore(now, horizon);
        if (groupBindings.Count == 0 && teamBindings.Count == 0)
            return default;

        var accounts = await GetSharedBotAccountsAsync(cancellationToken).ConfigureAwait(false);
        var newExpiresAt = now + TimeSpan.FromHours(_options.BindingTtlHours);
        int renewed = 0, anchorGone = 0, deferred = 0;

        foreach (var binding in groupBindings)
        {
            var membership = await CheckAnchorAsync(accounts, binding.GroupId, binding.AnchorAci, cancellationToken)
                .ConfigureAwait(false);
            if (membership == GroupMembership.Member)
            {
                if (_store.TryExtendGroupBinding(binding.BindingId, now, newExpiresAt))
                {
                    renewed++;
                    _logger.LogInformation(
                        "Прив'язку подовжено (identity {IdentityId}, група {GroupId}) до {ExpiresAt:O}.",
                        binding.IdentityId, binding.GroupId, newExpiresAt);
                }
            }
            else
            {
                Count(membership, ref anchorGone, ref deferred, binding.GroupId);
            }
        }

        foreach (var binding in teamBindings)
        {
            var membership = await CheckAnchorAsync(accounts, binding.GroupId, binding.AnchorAci, cancellationToken)
                .ConfigureAwait(false);
            if (membership == GroupMembership.Member)
            {
                if (_store.TryExtendTeamBinding(
                        binding.TeamId, binding.Alias, binding.GroupId, binding.AnchorAci, now, newExpiresAt))
                {
                    renewed++;
                    // Privacy: alias не логуємо (може нести id консьюмера) — лише команду й групу.
                    _logger.LogInformation(
                        "Командну прив'язку подовжено (команда {TeamId}, група {GroupId}) до {ExpiresAt:O}.",
                        binding.TeamId, binding.GroupId, newExpiresAt);
                }
            }
            else
            {
                Count(membership, ref anchorGone, ref deferred, binding.GroupId);
            }
        }

        return new BindingRenewalResult(renewed, anchorGone, deferred);
    }

    private void Count(GroupMembership membership, ref int anchorGone, ref int deferred, string groupId)
    {
        if (membership == GroupMembership.NotMember)
        {
            anchorGone++;
            _logger.LogInformation(
                "Прив'язку до групи {GroupId} не подовжено: anchor не член групи на жодному shared-bot акаунті.", groupId);
        }
        else
        {
            deferred++;
            _logger.LogInformation(
                "Подовження прив'язки до групи {GroupId} відкладено: членство anchor не вдалося визначити.", groupId);
        }
    }

    // Member на будь-якому shared-bot акаунті → Member. Інакше Unknown, якщо хоч один акаунт не відповів
    // (транзієнт не доводить відсутність), і NotMember лише коли всі відповіли «не член».
    private async Task<GroupMembership> CheckAnchorAsync(
        IReadOnlyList<string> accounts, string groupId, string anchorAci, CancellationToken cancellationToken)
    {
        if (accounts.Count == 0)
            return GroupMembership.Unknown;

        var sawUnknown = false;
        foreach (var account in accounts)
        {
            var membership = await _memberCache.CheckAsync(account, groupId, anchorAci, cancellationToken)
                .ConfigureAwait(false);
            if (membership == GroupMembership.Member)
                return GroupMembership.Member;
            if (membership == GroupMembership.Unknown)
                sawUnknown = true;
        }

        return sawUnknown ? GroupMembership.Unknown : GroupMembership.NotMember;
    }

    private async Task<IReadOnlyList<string>> GetSharedBotAccountsAsync(CancellationToken cancellationToken)
    {
        var response = await _signalAccounts.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        return response.Items
            .Select(a => a.Number)
            .Where(n => !string.IsNullOrWhiteSpace(n) && _sendGate.IsSharedBotAccount(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
