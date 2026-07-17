using System.Collections.Frozen;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The declarative, hand-authored authorization policy table for every RPC method this server
/// exposes. This is the security-relevant source of truth: adding a new RPC method WITHOUT adding a
/// row here means the method is denied at runtime (default-deny) and fails the startup coverage guard
/// (<see cref="RpcPolicyCoverageValidator"/>) — you cannot ship an unclassified method by accident (W8/W23).
/// </summary>
/// <remarks>
/// Class every method as exactly one of the four kinds. For account-bearing methods, record the
/// <c>account</c> (and, for send, <c>recipients</c>) argument name+index so the chokepoint can guard
/// them. For read queries that expose accounts/groups, set <see cref="RpcMethodPolicy.Output"/> so the
/// result is filtered fail-closed to the caller's visibility.
/// </remarks>
public sealed class SignalRpcPolicyRegistry : IRpcPolicyRegistry
{
    private static readonly RpcMethodPolicy[] Policies =
    [
        // --- Public: неавтентифіковано, без стану, без PII ---
        new() { Method = "ping", Kind = RpcPolicyKind.Public },
        // handshake — negotiation версії/можливостей контракту (A5); має бути доступним до auth.
        new() { Method = "handshake", Kind = RpcPolicyKind.Public },

        // --- IdentityOnboarding: виконується ДО існування акаунта (лінкування пристрою / інвайт) ---
        new() { Method = "startLink", Kind = RpcPolicyKind.IdentityOnboarding },
        new() { Method = "finishLink", Kind = RpcPolicyKind.IdentityOnboarding },
        // redeemInvite — Sybil-гейт онбордингу (add-invites-admin): без токена, rate-limited; створює нову
        // identity + перший токен. Account-аргумента немає (акаунта ще не існує).
        new() { Method = "redeemInvite", Kind = RpcPolicyKind.IdentityOnboarding },

        // --- AccountQuery: read-вихід фільтрується fail-closed до видимості caller'а ---
        // listAccounts не має account-аргумента; віддає перелік акаунтів демона → фільтр Accounts (R3.5/R3.6).
        new() { Method = "listAccounts", Kind = RpcPolicyKind.AccountQuery, Output = ReadOutputKind.Accounts },
        // listGroups(account) — inbound-guard по account + outbound-фільтр лише claim'нутих/власних груп (R3.2).
        new()
        {
            Method = "listGroups", Kind = RpcPolicyKind.AccountQuery,
            AccountArgName = "account", AccountArgIndex = 0, Output = ReadOutputKind.Groups,
        },

        // --- AccountCommand: команда, що змінює/діє від імені акаунта; inbound-guard по account ---
        // syncAccount не адресує конкретний акаунт аргументом — привілейована, але без per-account guard.
        new() { Method = "syncAccount", Kind = RpcPolicyKind.AccountCommand },
        // sendTextMessage(account, recipients, message) — guard account + валідація recipient +
        // розмір/кодування тексту (D11), усе до dispatch.
        new()
        {
            Method = "sendTextMessage", Kind = RpcPolicyKind.AccountCommand,
            AccountArgName = "account", AccountArgIndex = 0,
            RecipientsArgName = "recipients", RecipientsArgIndex = 1,
            TextArgName = "message", TextArgIndex = 2,
        },
        // subscribe(account, eventTypes) — guard account (підписка на події конкретного акаунта).
        new()
        {
            Method = "subscribe", Kind = RpcPolicyKind.AccountCommand,
            AccountArgName = "account", AccountArgIndex = 0,
        },
        // unsubscribe/updateSubscription адресують ВЛАСНИЙ subscriptionId клієнта (scoped по clientId
        // в адаптері), account-аргумента немає — привілейовані, без per-account guard.
        new() { Method = "unsubscribe", Kind = RpcPolicyKind.AccountCommand },
        new() { Method = "updateSubscription", Kind = RpcPolicyKind.AccountCommand },
    ];

    private readonly FrozenDictionary<string, RpcMethodPolicy> _byMethod =
        Policies.ToFrozenDictionary(static p => p.Method, StringComparer.Ordinal);

    /// <inheritdoc />
    public RpcMethodPolicy? Resolve(string method) =>
        !string.IsNullOrEmpty(method) && _byMethod.TryGetValue(method, out var policy) ? policy : null;

    /// <inheritdoc />
    public IReadOnlyCollection<RpcMethodPolicy> All => Policies;
}
