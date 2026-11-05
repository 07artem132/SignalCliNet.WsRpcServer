using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Message;
using SignalCliNet.WsRpcServer.Deployment;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>Outcome of <see cref="ChatCoalescer.TryCoalesce"/>.</summary>
public enum CoalesceDecision
{
    /// <summary>Send now (first message in the chat's window, coalescing off, or the batch is full).</summary>
    SendNow,

    /// <summary>Queued: will be joined with the others and sent at the end of the chat's window.</summary>
    Queued,
}

/// <summary>
/// Per-chat coalescing of notifications (add-team-notify): the first message to a chat opens a window and
/// goes out immediately; later messages to the SAME chat inside the window are queued and sent as one joined
/// message when the window ends. Chats never mix.
/// </summary>
/// <remarks>
/// <para>
/// Після flush вікно перезапускається (наступне повідомлення протягом вікна знову йде в чергу), тож чат
/// отримує не більше одного повідомлення на вікно після першого. Вікно без черги на flush — закривається,
/// і наступне повідомлення знову йде одразу.
/// </para>
/// <para>
/// Стан у пам'яті під одним <see cref="Lock"/>; надсилання — поза lock'ом. Час — лише через
/// <see cref="TimeProvider"/>; flush викликає <see cref="ChatCoalescerHostedService"/> (тести — напряму
/// <see cref="FlushDueAsync"/>). Privacy: текст і group-id не логуються.
/// </para>
/// </remarks>
public sealed class ChatCoalescer
{
    /// <summary>Separator between joined messages.</summary>
    public const string Separator = "\n";

    // Та сама стеля тексту, що й у chokepoint (MaxMessageTextChars); переповнена пачка → SendNow.
    internal const int MaxJoinedChars = 64_000;

    private readonly Func<string, string, string, CancellationToken, Task> _send;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatCoalescer> _logger;
    private readonly TimeSpan _window;
    private readonly Lock _sync = new();
    private readonly Dictionary<(string Account, string GroupId), ChatWindow> _windows = [];

    /// <summary>Production constructor: flushes through the SignalCli.NET message facade.</summary>
    public ChatCoalescer(
        ISignalMessage signalMessage,
        IOptions<NotifyOptions> options,
        TimeProvider timeProvider,
        ILogger<ChatCoalescer> logger)
        : this(CreateSender(signalMessage ?? throw new ArgumentNullException(nameof(signalMessage))),
            options, timeProvider, logger)
    {
    }

    internal ChatCoalescer(
        Func<string, string, string, CancellationToken, Task> send,
        IOptions<NotifyOptions> options,
        TimeProvider timeProvider,
        ILogger<ChatCoalescer> logger)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _window = TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.CoalesceWindowSeconds);
    }

    /// <summary>Whether coalescing is on (<c>Notify:CoalesceWindowSeconds</c> &gt; 0).</summary>
    public bool Enabled => _window > TimeSpan.Zero;

    /// <summary>Number of chats with an open window (diagnostics/tests).</summary>
    public int OpenWindows
    {
        get
        {
            lock (_sync)
                return _windows.Count;
        }
    }

    /// <summary>
    /// Decides a send to one chat: opens the window and returns <see cref="CoalesceDecision.SendNow"/> for the
    /// first message, otherwise queues <paramref name="text"/> and returns <see cref="CoalesceDecision.Queued"/>.
    /// </summary>
    /// <param name="account">Shared-bot account the chat is reached through.</param>
    /// <param name="groupId">Destination group id.</param>
    /// <param name="text">Message text.</param>
    /// <returns>The decision.</returns>
    public CoalesceDecision TryCoalesce(string account, string groupId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(text);

        if (!Enabled)
            return CoalesceDecision.SendNow;

        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            var key = (account, groupId);
            if (!_windows.TryGetValue(key, out var window) || (window.EndsAt <= now && window.Pending.Count == 0))
            {
                _windows[key] = new ChatWindow(now + _window);
                return CoalesceDecision.SendNow;
            }

            // Пачка не влазить у ліміт тексту → не губимо, шлемо це повідомлення окремо одразу.
            var joinedLength = window.PendingChars + (window.Pending.Count > 0 ? Separator.Length : 0) + text.Length;
            if (joinedLength > MaxJoinedChars)
                return CoalesceDecision.SendNow;

            window.Pending.Add(text);
            window.PendingChars = joinedLength;
            return CoalesceDecision.Queued;
        }
    }

    /// <summary>
    /// Sends every chat whose window has ended: a joined message when something is queued (and the window
    /// restarts), otherwise the window just closes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completing once the due batches are sent.</returns>
    public Task FlushDueAsync(CancellationToken cancellationToken = default) => FlushAsync(all: false, cancellationToken);

    /// <summary>Sends every queued batch now regardless of the window (graceful stop).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completing once all batches are sent.</returns>
    public Task FlushAllAsync(CancellationToken cancellationToken = default) => FlushAsync(all: true, cancellationToken);

    private async Task FlushAsync(bool all, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        List<(string Account, string GroupId, string Text)> batches = [];
        lock (_sync)
        {
            foreach (var (key, window) in _windows.ToList())
            {
                if (!all && window.EndsAt > now)
                    continue;

                if (window.Pending.Count == 0)
                {
                    _windows.Remove(key);
                    continue;
                }

                batches.Add((key.Account, key.GroupId, string.Join(Separator, window.Pending)));
                _windows[key] = new ChatWindow(now + _window);
            }
        }

        foreach (var (account, groupId, text) in batches)
        {
            try
            {
                await _send(account, groupId, text, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Пачку вже віддано клієнтам як «queued»; повтору немає (at-most-once). Лог без тексту/group-id.
                _logger.LogWarning("Coalescer: не вдалося надіслати об'єднане повідомлення ({ErrorType}).", ex.GetType().Name);
            }
        }
    }

    private static Func<string, string, string, CancellationToken, Task> CreateSender(ISignalMessage signalMessage) =>
        (account, groupId, text, ct) => signalMessage.SendTextMessageAsync(
            new TextMessageOptions.Builder(account, [new GroupRecipient(groupId)], text).Build(), ct);

    private sealed class ChatWindow(DateTimeOffset endsAt)
    {
        public DateTimeOffset EndsAt { get; } = endsAt;

        public List<string> Pending { get; } = [];

        public int PendingChars { get; set; }
    }
}
