using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Events;

/// <summary>
/// The shared-bot group-claim code scanner (add-group-claim-receive, tasks 1.2/1.3/3.1). A non-blocking
/// consumer of the shared-bot receive-stream (scoped by <see cref="PerAccountReceiveRouter"/>): on each
/// incoming shared-bot GROUP text message it looks for a pending claim-code in the body and, on a match,
/// calls <see cref="IGroupClaimConfirmer.Confirm"/> (group = where the code appeared, anchor = sender ACI).
/// </summary>
/// <remarks>
/// <b>Head-of-line (Уточнення-3).</b> The scanner is a SEPARATE bounded-channel consumer
/// (<see cref="BoundedChannelFullMode.DropOldest"/>), off the hot RPC path: a slow scan never back-pressures
/// the daemon stdout-loop / RPC responses. Overflow silently drops the oldest queued message.
///
/// <b>Scope (R3.5).</b> Only shared-bot events reach it (the router never routes own-linked incoming here);
/// own-linked incoming stays private to its owner.
///
/// <b>Privacy (task 3.1 — scan-and-drop).</b> Bodies, numbers and attachments are NEVER logged or persisted.
/// The scanner reads the body in-memory, matches, and drops it — only the code_hash/group_id/anchor-aci that
/// a confirmed binding needs are handed to <see cref="IGroupClaimConfirmer.Confirm"/>. Logs carry no PII.
/// </remarks>
public sealed class GroupClaimScanner : IDisposable
{
    // Ємність черги — обмежена; переповнення дропає найстаріше (не блокує продюсера/демон-луп).
    private const int ChannelCapacity = 256;

    private readonly IGroupClaimConfirmer _confirmer;
    private readonly ILogger<GroupClaimScanner> _logger;
    private readonly Channel<TextMessageEventArgs> _channel;
    private readonly Task _consumer;
    private int _disposed;

    /// <summary>Creates the scanner and starts its background consumer.</summary>
    public GroupClaimScanner(IGroupClaimConfirmer confirmer, ILogger<GroupClaimScanner> logger)
    {
        _confirmer = confirmer ?? throw new ArgumentNullException(nameof(confirmer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _channel = Channel.CreateBounded<TextMessageEventArgs>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Non-blocking enqueue of a shared-bot event for scanning. Only text messages are queued (nothing else
    /// carries a scannable body); everything else is dropped here (scan-and-drop). Never blocks the caller.
    /// </summary>
    /// <param name="eventArgs">The shared-bot incoming event (routed here by <see cref="PerAccountReceiveRouter"/>).</param>
    public void Enqueue(BaseSignalEventArgs eventArgs)
    {
        // Лише текстові повідомлення несуть тіло з можливим claim-кодом; решту shared-bot трафіку дропаємо.
        if (eventArgs is TextMessageEventArgs text)
            _channel.Writer.TryWrite(text); // DropOldest: якщо повно — витисне найстаріше, не блокує
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
                Scan(message);
        }
        catch (OperationCanceledException)
        {
            // штатне завершення при Dispose (канал завершено)
        }
    }

    // Скан-і-викинь: шукаємо в тілі pending claim-код. Тіло/номери/вкладення НЕ логуються й НЕ зберігаються.
    private void Scan(TextMessageEventArgs message)
    {
        try
        {
            var text = message.DataMessage?.Message;
            var groupId = message.DataMessage?.GroupInfo?.GroupId;
            var anchorAci = message.SourceUuid;

            // Claim фіксується лише у ГРУПІ й потребує ACI вставника (anchor). Немає — нічого сканувати.
            if (string.IsNullOrWhiteSpace(text) ||
                string.IsNullOrWhiteSpace(groupId) ||
                string.IsNullOrWhiteSpace(anchorAci))
                return;

            foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!GroupClaimService.LooksLikeCode(token, out var normalized))
                    continue;

                var codeHash = GroupClaimService.ComputeCodeHash(normalized);
                if (_confirmer.Confirm(codeHash, groupId, anchorAci))
                {
                    // privacy: без тіла/номерів — лише факт підтвердження (group id — не PII-номер).
                    _logger.LogInformation("Сканер: підтверджено group-claim (група {GroupId}).", groupId);
                    return; // один код на повідомлення
                }
            }
        }
        catch (Exception ex)
        {
            // Межа довготривалого циклу: один поганий елемент не має вбивати consumer (CLAUDE конвенції).
            // privacy: логуємо лише тип винятку, без тіла/номерів.
            _logger.LogError(ex, "Сканер: помилка обробки вхідного shared-bot повідомлення.");
        }
    }

    /// <summary>Completes the channel and awaits the consumer drain (best-effort, bounded).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _channel.Writer.TryComplete();
        try
        {
            _consumer.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // best-effort дренаж — ігноруємо помилки завершення
        }
    }
}
