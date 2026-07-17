using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// A freshly created invite: the plaintext <see cref="Code"/> (returned to the issuing admin exactly
/// once, for manual delivery) and its persisted <see cref="Record"/>.
/// </summary>
/// <param name="Code">The high-entropy plaintext invite code in visual blocks (never logged/persisted).</param>
/// <param name="Record">The durable invite record (its at-rest <c>CodeHash</c> is persisted, not the code).</param>
public sealed record InviteCreation(string Code, InviteRecord Record);

/// <summary>
/// A successful invite redemption: the issuing admin's identity and the invite's at-rest code hash —
/// enough for the caller to bind/audit the onboarding. Carries NO reason on failure (anti-oracle).
/// </summary>
/// <param name="CreatedBy">Identity id of the admin that issued the redeemed invite.</param>
/// <param name="CodeHash">The consumed invite's at-rest code hash (audit subject).</param>
public sealed record InviteRedemption(string CreatedBy, string CodeHash);

/// <summary>
/// Mints and redeems one-time invite codes — the Sybil-gate for identity onboarding (add-invites-admin,
/// tasks 1.1/1.3). A code is a ≥96-bit CSPRNG secret rendered as human-typable Crockford-base32 blocks;
/// only its at-rest <c>SHA-256</c> hash is persisted (the code itself is the secret — see remarks).
/// </summary>
/// <remarks>
/// <b>code_hash = SHA-256, не HMAC (рішення).</b> Окремий invite-пеппер у деплої не заведено, а
/// повторне використання <c>pepper_abuse</c> сплутало б домени. Оскільки сам код — високоентропійний
/// (128-bit) секрет, at-rest хеш потрібен ЛИШЕ для lookup (щоб плейнтекст-код не лежав у БД), а не для
/// секретності: щоб «вгадати» рядок за хешем, треба вгадати 128-bit код. SHA-256 тут достатньо.
///
/// <b>Формат коду.</b> 16 CSPRNG-байтів (128-bit ≥ 96-bit) → Crockford-base32 (алфавіт без I/L/O/U) →
/// 26 символів, розбитих дефісами на блоки по 4 для ручного вводу (напр.
/// <c>K7QT-9F2M-…-ZC</c>). Нормалізація (перед хешем) прибирає роздільники/пробіли, робить
/// upper-invariant і фолдить сплутувані символи (O→0, I/L→1) — тож дрібні помилки ручного вводу не
/// ламають lookup, а той самий код у різному оформленні дає той самий хеш.
///
/// <b>Анти-oracle.</b> <see cref="TryRedeem"/> не розкриває причину невдачі: unknown / expired /
/// consumed / over-cap повертають ІДЕНТИЧНИЙ <c>null</c>. Rate-limit СПРОБ — окремо
/// (<see cref="InviteRedemptionRateLimiter"/>). Атомарність consume+attempt-cap — у сторі (W20/G12).
///
/// Singleton; <see cref="TimeProvider"/> інжектимо для тестабельності.
/// </remarks>
public sealed class InviteService
{
    // Ентропія коду — 128 біт (16 CSPRNG-байтів), що ≥ 96-bit вимоги (task 1.1).
    private const int CodeBytes = 16;

    // Crockford base32 (без I, L, O, U — щоб не плутати з 1/0). 32 символи.
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // Розмір візуального блоку при ручному оформленні коду.
    private const int BlockSize = 4;

    private readonly IDurableStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InviteService> _logger;

    /// <summary>Creates the invite service.</summary>
    /// <param name="store">The durable store (invites table).</param>
    /// <param name="timeProvider">System clock (tests substitute a fixed clock).</param>
    /// <param name="logger">Logger (never receives a plaintext invite code).</param>
    public InviteService(IDurableStore store, TimeProvider timeProvider, ILogger<InviteService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Mints a fresh one-time invite issued by <paramref name="createdByIdentityId"/>, valid for
    /// <paramref name="ttl"/>, with a per-code attempt cap of <paramref name="maxAttempts"/>.
    /// </summary>
    /// <param name="createdByIdentityId">The issuing admin's identity id (opaque; never a phone number).</param>
    /// <param name="ttl">Time-to-live from now until the invite expires (&gt; 0).</param>
    /// <param name="maxAttempts">Per-code redeem-attempt cap (≥ 1); each attempt (even a failed one) counts (G12).</param>
    /// <returns>The plaintext code (returned once) and its persisted record.</returns>
    public InviteCreation Create(string createdByIdentityId, TimeSpan ttl, int maxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createdByIdentityId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var code = GenerateCode();
        var codeHash = ComputeCodeHash(code);
        var now = _timeProvider.GetUtcNow();
        var record = new InviteRecord(
            codeHash, createdByIdentityId, Consumed: false, AttemptCount: 0, maxAttempts,
            ExpiresAt: now + ttl, ConsumedBy: null, CreatedAt: now);

        // Прямий insert; дубль code_hash (астрономічно малоймовірний за 128-bit) спливе SqliteException'ом.
        _store.InsertInvite(record);

        // НЕ логуємо код — лише факт видачі, емітента й термін.
        _logger.LogInformation(
            "Видано інвайт (емітент {CreatedBy}, діє до {ExpiresAt:O}, cap спроб {MaxAttempts}).",
            createdByIdentityId, record.ExpiresAt, maxAttempts);
        return new InviteCreation(code, record);
    }

    /// <summary>
    /// Atomically redeems <paramref name="code"/> for <paramref name="consumedByIdentityId"/> at
    /// <paramref name="now"/>: normalizes → hashes → consumes-and-binds via the store (W20/G12).
    /// </summary>
    /// <remarks>
    /// Анти-oracle: будь-яка невдача (невідомий / прострочений / уже погашений / понад cap) повертає
    /// ІДЕНТИЧНИЙ <c>null</c> — клієнт не дізнається причину. Кожна спроба (зокрема невдала) інкрементує
    /// per-code attempt-cap у сторі (G12), тож brute-force відомого коду обмежений.
    /// </remarks>
    /// <param name="code">The client-supplied plaintext code (any block formatting; normalized here).</param>
    /// <param name="consumedByIdentityId">The identity that redeems the invite (recorded as consumed_by).</param>
    /// <param name="now">Current time (expiry check).</param>
    /// <returns>The redemption on success; otherwise <c>null</c> (reason deliberately hidden).</returns>
    public InviteRedemption? TryRedeem(string code, string consumedByIdentityId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumedByIdentityId);

        // Порожній/пробільний код → та сама нерозрізнена відмова (anti-oracle), без винятку.
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var codeHash = ComputeCodeHash(code);

        // Атомарний consume+attempt-cap (W20/G12) — уся логіка під транзакцією стору.
        if (!_store.TryConsumeInvite(codeHash, consumedByIdentityId, now))
            return null;

        var invite = _store.GetInvite(codeHash);
        // Після успішного consume запис існує; захисний null-guard на випадок гонки з зовнішнім видаленням.
        if (invite is null)
            return null;

        _logger.LogInformation("Інвайт погашено (емітент {CreatedBy}).", invite.CreatedBy);
        return new InviteRedemption(invite.CreatedBy, codeHash);
    }

    /// <summary>
    /// Computes the at-rest code hash of <paramref name="code"/> (normalize → <c>base64(SHA-256)</c>).
    /// Exposed so an admin lookup path (section 2) can find an invite by its plaintext code without
    /// re-deriving the normalization rules.
    /// </summary>
    /// <param name="code">The plaintext code (any block formatting).</param>
    /// <returns>The base64 SHA-256 of the normalized code.</returns>
    public static string ComputeCodeHash(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = Normalize(code);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToBase64String(hash);
    }

    // Генерує 128-bit CSPRNG-код у Crockford-base32, розбитий дефісами на блоки по 4 для ручного вводу.
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

        // Залишкові біти (для 128-bit — 3 біти) добиваємо нулями праворуч до повного символу.
        if (bits > 0)
            sb.Append(CrockfordAlphabet[(buffer << (5 - bits)) & 0x1F]);

        return sb.ToString();
    }

    // Нормалізація: upper-invariant, прибрати роздільники/пробіли, фолд сплутуваних символів (O→0, I/L→1)
    // — щоб той самий код у різному оформленні / з типовими помилками ручного вводу дав той самий хеш.
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
                    // роздільники/пробіли/пунктуація — пропускаємо
                    break;
            }
        }

        return sb.ToString();
    }
}
