using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Appends and verifies the tamper-evident audit-trail of security events (add-invites-admin, task 3.2 /
/// M8): device-link, invite redeem/create, admin promote/revoke, admin listAccounts(all). Each entry is
/// hash-chained to its predecessor, so post-hoc editing or deletion of any record is detectable.
/// </summary>
/// <remarks>
/// <b>Hash-chain.</b> <c>entry_hash = SHA-256(prevHash ‖ seq ‖ eventType ‖ actorIdentity ‖ detailHash ‖
/// loggedAt)</c> (hex); genesis <c>prevHash</c> = 64 нулі. Видалення/підробка будь-якого запису рве
/// ланцюг (<see cref="VerifyChain"/> повертає <c>false</c>).
///
/// <b>Окремо від abuse-логу.</b> Це РІЗНИЙ домен: abuse-лог (<see cref="AbuseLogService"/>) — W24-HMAC
/// проти equality-oracle для обсяг-throttle; audit-лог — hash-chain для tamper-evidence security-подій.
/// Обидва лишаються; цей — новий, він закриває residual секції 1 (див. <c>docs/shared-bot.md</c>).
///
/// <b>Privacy.</b> Зберігаємо лише <c>SHA-256(деталей)</c>; у <paramref name="detail"/> МОЖНА передавати
/// лише ідентифікатори/типи (identity id, code_hash, лічильники) — НІКОЛИ токени/номери телефону.
///
/// <b>Серіалізація.</b> Єдиний записувач (singleton) під власним локом: read-tip → compute → append —
/// атомарно відносно інших appendʼів, тож ланцюг не рветься гонкою двох одночасних подій.
/// </remarks>
public sealed class AuditLogService
{
    // Genesis prev_hash — 64 нулі (довжина hex-репрезентації SHA-256, як entry_hash).
    private static readonly string GenesisHash = new('0', 64);

    // NUL канонічно розмежовує компоненти pre-image (жоден не містить NUL: hex-хеші, ISO-мітка,
    // opaque-identity, машиночитний тег) — тож (a‖b) не сплутати з (a'‖b').
    private const byte FieldSeparator = 0x00;

    private readonly IDurableStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditLogService> _logger;
    private readonly object _sync = new();

    /// <summary>Creates the audit-log service.</summary>
    /// <param name="store">The durable store (audit_log table).</param>
    /// <param name="timeProvider">System clock (tests substitute a fixed clock).</param>
    /// <param name="logger">Logger (never receives raw details).</param>
    public AuditLogService(IDurableStore store, TimeProvider timeProvider, ILogger<AuditLogService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Appends a security event to the audit-trail: reads the chain tip, computes the new entry's hashes
    /// and persists it (all under one lock so the chain stays consistent).
    /// </summary>
    /// <param name="eventType">Machine-readable event tag (e.g. <c>invite_created</c>) — no PII.</param>
    /// <param name="actorIdentity">The acting identity id (never a phone number).</param>
    /// <param name="detail">
    /// Event details — hashed, never stored raw. MUST contain only identifiers/types (identity ids,
    /// code hashes, counts) — NEVER tokens or phone numbers.
    /// </param>
    /// <returns>The sequence number assigned to the appended entry.</returns>
    public long Append(string eventType, string actorIdentity, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorIdentity);
        ArgumentNullException.ThrowIfNull(detail);

        lock (_sync)
        {
            var tip = _store.GetLastAuditEntry();
            var seq = (tip?.Seq ?? 0) + 1;
            var prevHash = tip?.EntryHash ?? GenesisHash;

            var detailHash = Sha256Hex(Encoding.UTF8.GetBytes(detail));
            var loggedAt = _timeProvider.GetUtcNow();
            var loggedAtIso = FormatTimestamp(loggedAt);
            var entryHash = ComputeEntryHash(prevHash, seq, eventType, actorIdentity, detailHash, loggedAtIso);

            _store.AppendAuditEntry(new AuditLogRecord(
                seq, eventType, actorIdentity, detailHash, prevHash, entryHash, loggedAt));

            // НЕ логуємо сирі деталі — лише тип події, актора (id, не номер) і seq.
            _logger.LogInformation(
                "Audit: подія {EventType} (актор {Actor}, seq {Seq}).", eventType, actorIdentity, seq);
            return seq;
        }
    }

    /// <summary>
    /// Walks the whole chain and verifies its integrity: every entry's <c>prev_hash</c> must equal the
    /// prior entry's <c>entry_hash</c>, and every <c>entry_hash</c> must recompute exactly. Any tampered or
    /// deleted record makes this return <c>false</c> (for a DoD / admin dump).
    /// </summary>
    /// <returns><c>true</c> if the chain is intact; otherwise <c>false</c>.</returns>
    public bool VerifyChain()
    {
        var entries = _store.GetAuditEntries();
        var prevHash = GenesisHash;
        long expectedSeq = 1;

        foreach (var entry in entries)
        {
            if (entry.Seq != expectedSeq)
                return false;
            if (!string.Equals(entry.PrevHash, prevHash, StringComparison.Ordinal))
                return false;

            var recomputed = ComputeEntryHash(
                prevHash, entry.Seq, entry.EventType, entry.ActorIdentity, entry.DetailHash,
                FormatTimestamp(entry.LoggedAt));
            if (!string.Equals(recomputed, entry.EntryHash, StringComparison.Ordinal))
                return false;

            prevHash = entry.EntryHash;
            expectedSeq++;
        }

        return true;
    }

    // entry_hash = SHA-256(prevHash ‖ seq ‖ eventType ‖ actorIdentity ‖ detailHash ‖ loggedAtIso), NUL-розд.
    private static string ComputeEntryHash(
        string prevHash, long seq, string eventType, string actorIdentity, string detailHash, string loggedAtIso)
    {
        using var buffer = new MemoryStream();
        AppendField(buffer, prevHash);
        AppendField(buffer, seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendField(buffer, eventType);
        AppendField(buffer, actorIdentity);
        AppendField(buffer, detailHash);
        AppendField(buffer, loggedAtIso);
        return Sha256Hex(buffer.ToArray());
    }

    private static void AppendField(MemoryStream buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        buffer.Write(bytes, 0, bytes.Length);
        buffer.WriteByte(FieldSeparator);
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
