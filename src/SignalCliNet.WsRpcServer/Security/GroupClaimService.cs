using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>Outcome kind of <see cref="GroupClaimService.RequestClaim"/>.</summary>
public enum GroupClaimStatus
{
    /// <summary>A fresh claim-code was minted (<see cref="GroupClaimResult.Code"/> is the plaintext secret).</summary>
    Created,

    /// <summary>The per-identity request rate cap was exceeded (retry later; not a ban).</summary>
    RateLimited,

    /// <summary>The per-identity active-binding quota was exceeded (revoke some bindings first).</summary>
    QuotaExceeded,
}

/// <summary>
/// The result of a <see cref="GroupClaimService.RequestClaim"/> call: the outcome and — on
/// <see cref="GroupClaimStatus.Created"/> — the plaintext <see cref="Code"/> (secret, returned once) and
/// its absolute <see cref="ExpiresAt"/>.
/// </summary>
/// <param name="Status">The outcome kind.</param>
/// <param name="Code">The plaintext claim code on success (secret — never logged); otherwise <c>null</c>.</param>
/// <param name="ExpiresAt">The claim's absolute expiry on success; otherwise <c>null</c>.</param>
public sealed record GroupClaimResult(GroupClaimStatus Status, string? Code, DateTimeOffset? ExpiresAt);

/// <summary>
/// Mints and confirms one-time group-claim codes — the (identity, group) authorization gate
/// (add-group-claim-receive, tasks 2.1/2.2). A code is a ≥96-bit CSPRNG secret rendered as human-typable
/// Crockford-base32 blocks (mirror of <see cref="InviteService"/>); only its at-rest <c>SHA-256</c> is
/// persisted (the code itself is the secret). Implements <see cref="IGroupClaimConfirmer"/> — the
/// scanner-facing confirm-seam.
/// </summary>
/// <remarks>
/// <b>T3 (group-agnostic при видачі).</b> <see cref="RequestClaim"/> НЕ приймає групу — код bound лише до
/// identity U. Групу та anchor фіксує сканер при <see cref="Confirm"/> (де з'явився код / хто вставив).
///
/// <b>W20 (атомарний consume-and-bind).</b> <see cref="Confirm"/> робить
/// <see cref="IDurableStore.TryConsumeGroupClaim"/> (умовний <c>UPDATE … WHERE consumed=0</c>), і лише
/// переможець гонки вставляє binding. One-time: повторний/перехоплений код після consume → відмова.
///
/// <b>Footprint (D2).</b> <see cref="RequestClaim"/> гейтить per-identity quota активних bindings (cap) +
/// per-identity request rate-limit — обидва повертаються у <see cref="GroupClaimResult.Status"/> (адаптер
/// мапить у RPC-помилки), тож сервіс лишається RPC-агностичним.
///
/// Singleton; <see cref="TimeProvider"/> інжектимо для тестабельності.
/// </remarks>
public sealed class GroupClaimService : IGroupClaimConfirmer
{
    // Ентропія коду — 128 біт (16 CSPRNG-байтів), що ≥ 96-bit вимоги (task 0.1); 26 Crockford-символів.
    private const int CodeBytes = 16;
    private const int NormalizedCodeLength = 26;
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int BlockSize = 4;

    private readonly IDurableStore _store;
    private readonly GroupClaimRateLimiter _rateLimiter;
    private readonly GroupClaimOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GroupClaimService> _logger;

    /// <summary>Creates the group-claim service.</summary>
    public GroupClaimService(
        IDurableStore store,
        GroupClaimRateLimiter rateLimiter,
        IOptions<GroupClaimOptions> options,
        TimeProvider timeProvider,
        ILogger<GroupClaimService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Requests a fresh one-time claim for <paramref name="identityId"/> (T3: no group argument). Gated by
    /// the per-identity request rate-limit and the active-binding quota.
    /// </summary>
    /// <param name="identityId">The requesting identity (opaque; never a phone number).</param>
    /// <returns>The outcome and, on success, the plaintext code + expiry.</returns>
    public GroupClaimResult RequestClaim(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        // 1. Per-identity request rate-limit (griefing-burn budget) — окремо від quota.
        if (!_rateLimiter.TryAcquire(identityId))
        {
            _logger.LogInformation("requestGroupClaim: rate-limit для identity {IdentityId}.", identityId);
            return new GroupClaimResult(GroupClaimStatus.RateLimited, null, null);
        }

        // 2. Per-identity quota активних (не revoked, не expired) bindings (footprint-cap, D2).
        var now = _timeProvider.GetUtcNow();
        var activeBindings = _store.GetGroupBindingsForIdentity(identityId)
            .Count(b => !b.Revoked && b.ExpiresAt > now);
        if (activeBindings >= _options.MaxActiveBindingsPerIdentity)
        {
            _logger.LogInformation(
                "requestGroupClaim: quota активних bindings ({Cap}) вичерпано для identity {IdentityId}.",
                _options.MaxActiveBindingsPerIdentity, identityId);
            return new GroupClaimResult(GroupClaimStatus.QuotaExceeded, null, null);
        }

        var code = GenerateCode();
        var codeHash = ComputeCodeHash(code);
        var expiresAt = now + TimeSpan.FromMinutes(_options.ClaimTtlMinutes);

        // Прямий insert; дубль code_hash (астрономічно малоймовірний за 128-bit) спливе SqliteException'ом.
        _store.InsertGroupClaim(new GroupClaimRecord(codeHash, identityId, Consumed: false, expiresAt, now));

        // НЕ логуємо код — лише факт видачі, identity й термін.
        _logger.LogInformation(
            "Видано group-claim (identity {IdentityId}, діє до {ExpiresAt:O}).", identityId, expiresAt);
        return new GroupClaimResult(GroupClaimStatus.Created, code, expiresAt);
    }

    /// <inheritdoc />
    public bool Confirm(string codeHash, string groupId, string anchorAci)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorAci);

        var now = _timeProvider.GetUtcNow();

        // W20: атомарний consume — лише переможець гонки продовжує до bind.
        if (!_store.TryConsumeGroupClaim(codeHash, now))
            return false;

        // Після consume claim існує; читаємо created_by (U) для binding-у.
        var claim = _store.GetGroupClaim(codeHash);
        if (claim is null)
            return false; // захисний null-guard на випадок зовнішнього видалення

        var binding = new GroupBindingRecord(
            BindingId: NewBindingId(),
            IdentityId: claim.CreatedBy,
            GroupId: groupId,
            AnchorAci: anchorAci,
            ExpiresAt: now + TimeSpan.FromHours(_options.BindingTtlHours),
            Revoked: false,
            CreatedAt: now);
        _store.InsertGroupBinding(binding);

        // Privacy: логуємо лише identity й групу (не тіло/номери; anchor — ACI, id, не PII-номер).
        _logger.LogInformation(
            "group-claim підтверджено: binding (identity {IdentityId}, група {GroupId}).",
            claim.CreatedBy, groupId);
        return true;
    }

    /// <summary>
    /// Computes the at-rest code hash of <paramref name="code"/> (normalize → <c>base64(SHA-256)</c>).
    /// Shared by the mint path and the scanner (so the scanner hashes a candidate exactly as issued).
    /// </summary>
    public static string ComputeCodeHash(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = Normalize(code);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Cheap shape pre-filter for the scanner: normalizes <paramref name="candidate"/> and returns whether
    /// it has the exact length + alphabet of a claim code (so the scanner only hashes/consults plausible
    /// tokens, not every word of every message).
    /// </summary>
    /// <param name="candidate">A whitespace-delimited token from an incoming message.</param>
    /// <param name="normalized">The normalized candidate on success; empty otherwise.</param>
    /// <returns><c>true</c> if the token has claim-code shape.</returns>
    public static bool LooksLikeCode(string? candidate, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var n = Normalize(candidate);
        if (n.Length != NormalizedCodeLength)
            return false;

        foreach (var ch in n)
        {
            if (!CrockfordAlphabet.Contains(ch))
                return false;
        }

        normalized = n;
        return true;
    }

    // Новий binding id — 128-bit CSPRNG у hex (opaque).
    private static string NewBindingId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // Генерує 128-bit CSPRNG-код у Crockford-base32, розбитий дефісами на блоки по 4 (як інвайти).
    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[CodeBytes];
        RandomNumberGenerator.Fill(bytes);
        var raw = EncodeCrockford(bytes);

        var sb = new StringBuilder(raw.Length + raw.Length / BlockSize);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % BlockSize == 0)
                sb.Append('-');
            sb.Append(raw[i]);
        }

        return sb.ToString();
    }

    // Crockford-base32 кодування (big-endian по бітах): 16 байтів → 26 символів.
    private static string EncodeCrockford(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(CrockfordAlphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
            sb.Append(CrockfordAlphabet[(buffer << (5 - bits)) & 0x1F]);

        return sb.ToString();
    }

    // Нормалізація: upper-invariant, прибрати роздільники/пробіли, фолд сплутуваних символів (O→0, I/L→1).
    private static string Normalize(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var ch in code)
        {
            var c = char.ToUpperInvariant(ch);
            switch (c)
            {
                case 'O':
                    sb.Append('0');
                    break;
                case 'I':
                case 'L':
                    sb.Append('1');
                    break;
                default:
                    if (c is (>= '0' and <= '9') or (>= 'A' and <= 'Z'))
                        sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
