using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// Per-identity new-recipient window counter (D2). Signal bans on first-contact fan-out (breadth), not
/// volume, so this throttles how many NEW (first-ever-contact) recipients an identity may reach per
/// window — independently of the aggregate send budget.
/// </summary>
/// <remarks>
/// <para>
/// A recipient is "known" once it appears in the durable <c>known_recipients</c> table for that identity
/// (first contact persists forever, until the identity is deleted). "New" = not yet known. The per-window
/// counter (in-memory, monotonic reset D15) limits first-ever contacts; already-known recipients do not
/// count and are not re-persisted.
/// </para>
/// <para>
/// <b>Privacy contract.</b> Raw recipient identifiers are NEVER logged or stored — only
/// <c>base64(HMAC-SHA256(recipient, pepper_abuse))</c>, keyed by the domain-separated abuse pepper
/// (<see cref="IAbusePepperProvider"/>, distinct from the token pepper).
/// </para>
/// </remarks>
public sealed class NewRecipientTracker
{
    private readonly IDurableStore _store;
    private readonly IAbusePepperProvider _pepperProvider;
    private readonly int _maxNewPerWindow;
    private readonly TimeSpan _windowDuration;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<string, CountWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>Creates the tracker from validated <see cref="AdmissionOptions"/>.</summary>
    /// <param name="store">The durable known-recipient store.</param>
    /// <param name="pepperProvider">Supplies the domain-separated abuse pepper (HMAC key).</param>
    /// <param name="options">Admission options (new-recipient cap, window length).</param>
    /// <param name="timeProvider">System clock (monotonic windowing, D15).</param>
    public NewRecipientTracker(
        IDurableStore store,
        IAbusePepperProvider pepperProvider,
        IOptions<AdmissionOptions> options,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pepperProvider = pepperProvider ?? throw new ArgumentNullException(nameof(pepperProvider));
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        var value = options.Value;
        _maxNewPerWindow = value.NewRecipientsPerWindow;
        _windowDuration = TimeSpan.FromMinutes(value.BudgetWindowMinutes);
    }

    /// <summary>
    /// Tries to admit the NEW recipients in <paramref name="recipients"/> for <paramref name="identityId"/>
    /// within its per-window cap. On success the new recipients are persisted as known; on cap-exceed
    /// nothing is persisted and the whole send is denied.
    /// </summary>
    public bool TryAdmitRecipients(string identityId, IReadOnlyList<string> recipients)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentNullException.ThrowIfNull(recipients);

        if (recipients.Count == 0)
            return true;

        var pepper = _pepperProvider.GetPepper();

        lock (_sync)
        {
            ExpireWindows();
            var window = GetOrCreate(identityId);

            // Рахуємо лише НОВІ (ще не відомі) отримувачі; дедуп у межах батча за хешем.
            var newHashes = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipient in recipients)
            {
                if (string.IsNullOrWhiteSpace(recipient))
                    continue;

                var hash = ComputeHash(recipient, pepper);
                if (!seen.Add(hash))
                    continue;

                if (!_store.IsKnownRecipient(identityId, hash))
                    newHashes.Add(hash);
            }

            // Усі відомі — обсяг тут не лімітуємо (це робить бюджет).
            if (newHashes.Count == 0)
                return true;

            // Перевищення вікна нових контактів — нічого не персистимо (privacy: жодного запису).
            if (window.Count + newHashes.Count > _maxNewPerWindow)
                return false;

            window.Count += newHashes.Count;
            var now = _timeProvider.GetUtcNow();
            foreach (var hash in newHashes)
                _store.UpsertKnownRecipient(identityId, hash, now);

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
            var remaining = _windows.TryGetValue(identityId, out var window)
                ? _windowDuration - _timeProvider.GetElapsedTime(window.StartedMonotonic)
                : _windowDuration;
            return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        }
    }

    // Хеш отримувача: HMAC-SHA256(recipient, pepper_abuse) → base64. Ключ = домен-розділений пеппер,
    // повідомлення = UTF-8 номера. Сирий номер тут не зберігається (лише результат).
    private static string ComputeHash(string recipient, byte[] pepper) =>
        Convert.ToBase64String(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(recipient)));

    private CountWindow GetOrCreate(string identityId)
    {
        if (!_windows.TryGetValue(identityId, out var window))
        {
            window = new CountWindow { StartedMonotonic = _timeProvider.GetTimestamp() };
            _windows[identityId] = window;
        }

        return window;
    }

    private void ExpireWindows()
    {
        List<string>? expired = null;
        foreach (var (id, window) in _windows)
        {
            if (_timeProvider.GetElapsedTime(window.StartedMonotonic) >= _windowDuration)
                (expired ??= []).Add(id);
        }

        if (expired is null)
            return;

        foreach (var id in expired)
            _windows.Remove(id);
    }

    private sealed class CountWindow
    {
        public long StartedMonotonic { get; init; }
        public int Count { get; set; }
    }
}
