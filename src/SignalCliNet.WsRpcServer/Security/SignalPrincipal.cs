using System.Collections.Frozen;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Authorization principal bound to a single WebSocket session — the single source of truth for
/// which Signal accounts and groups the caller may operate on or see.
/// </summary>
/// <remarks>
/// Anti-IDOR (звірка №1 / R3.x): дозволені акаунти виводяться ВИКЛЮЧНО з principal, ніколи з
/// клієнтського аргумента. Це той об'єкт, який chokepoint читає per-invocation; адаптери його не
/// тримають (V8). Розрізняємо «оперувати» (команда) і «бачити» (read-вихід), бо admin має право
/// нагляду (visibility ≠ operation, R3.6).
///
/// Фаза 1: справжньої автентифікації ще немає (authn — окрема пізніша зміна), тому loopback-сесія
/// отримує <see cref="LoopbackFullAccess"/> — неявний повний доступ (bind лишається loopback-only,
/// це і є компенсація за відсутність auth). Коли authn з'явиться, сесія виводитиме реальний
/// principal з ідентичності, а <see cref="HasFullAccess"/> стане <c>false</c> для звичайних клієнтів.
/// </remarks>
public sealed class SignalPrincipal
{
    /// <summary>
    /// Phase-1 default principal for a loopback session: implicit full access to every account and
    /// group. Immutable and shared — safe because it grants the same (total) rights to all callers
    /// while the server is loopback-only and unauthenticated.
    /// </summary>
    public static SignalPrincipal LoopbackFullAccess { get; } = new(
        name: "loopback",
        isAdmin: false,
        hasFullAccess: true,
        allowedAccounts: [],
        allowedGroupIds: []);

    /// <summary>
    /// Creates a principal.
    /// </summary>
    /// <param name="name">Stable identity name (used for diagnostics only — never a phone number).</param>
    /// <param name="isAdmin">Whether the principal has admin oversight (sees all accounts; R3.6).</param>
    /// <param name="hasFullAccess">Whether the principal may operate on and see everything (Phase-1 loopback).</param>
    /// <param name="allowedAccounts">Accounts (E.164) the principal owns / may operate on.</param>
    /// <param name="allowedGroupIds">Group ids the principal has claimed / owns (R3.2 visibility).</param>
    public SignalPrincipal(
        string name,
        bool isAdmin,
        bool hasFullAccess,
        IEnumerable<string> allowedAccounts,
        IEnumerable<string> allowedGroupIds)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "(анонім)" : name;
        IsAdmin = isAdmin;
        HasFullAccess = hasFullAccess;
        AllowedAccounts = (allowedAccounts ?? [])
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Select(Normalize)
            .ToFrozenSet(StringComparer.Ordinal);
        AllowedGroupIds = (allowedGroupIds ?? [])
            .Where(static g => !string.IsNullOrWhiteSpace(g))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Stable identity name for diagnostics. Never contains a phone number.</summary>
    public string Name { get; }

    /// <summary>Whether the principal has admin oversight (read-only visibility of all accounts; R3.6).</summary>
    public bool IsAdmin { get; }

    /// <summary>Whether the principal may operate on and see every account/group (Phase-1 loopback default).</summary>
    public bool HasFullAccess { get; }

    /// <summary>Accounts (normalized E.164) the principal may operate on.</summary>
    public IReadOnlySet<string> AllowedAccounts { get; }

    /// <summary>Group ids the principal has claimed / owns (visibility scope for read output; R3.2).</summary>
    public IReadOnlySet<string> AllowedGroupIds { get; }

    /// <summary>
    /// Whether the principal may OPERATE on <paramref name="account"/> (send, subscribe, mutate).
    /// Admin oversight does NOT grant operation rights (visibility ≠ operation; R3.6).
    /// </summary>
    public bool CanOperateAccount(string? account) =>
        HasFullAccess || (!string.IsNullOrWhiteSpace(account) && AllowedAccounts.Contains(Normalize(account)));

    /// <summary>
    /// Whether the principal may SEE <paramref name="account"/> in read output. Own accounts are private
    /// to the owner (R3.5); admin additionally sees all accounts for oversight (R3.6).
    /// </summary>
    public bool CanSeeAccount(string? account) =>
        HasFullAccess || IsAdmin || (!string.IsNullOrWhiteSpace(account) && AllowedAccounts.Contains(Normalize(account)));

    /// <summary>
    /// Whether the principal may SEE <paramref name="groupId"/> in read output — exclusively claimed/own
    /// groups (R3.2, closes G9). Admin gets no special group visibility.
    /// </summary>
    public bool CanSeeGroup(string? groupId) =>
        HasFullAccess || (!string.IsNullOrWhiteSpace(groupId) && AllowedGroupIds.Contains(groupId));

    // Нормалізація акаунта: signal-cli адресує акаунти в E.164; тут лише зрізаємо пробіли, щоб
    // порівняння з набором principal було стабільним (без урахування випадкового whitespace).
    internal static string Normalize(string account) => account.Trim();
}
