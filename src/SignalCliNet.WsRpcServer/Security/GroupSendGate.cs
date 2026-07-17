using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>JSON-RPC error codes for the group-claim surface (implementation-defined band).</summary>
/// <remarks>
/// <c>-32007</c> — send у групу через shared-bot без валідного binding/anchor (task 2.3/2.5). <c>-32008</c>
/// — per-identity binding quota вичерпано (task 2.1). Сусіди зайняті: <c>-32004</c> link-session-invalid,
/// <c>-32005</c> rate-limited, <c>-32006</c> invite-invalid. Задокументовано у <c>docs/self-host.md</c>.
/// </remarks>
public static class GroupClaimErrorCodes
{
    /// <summary>Group send denied — no active claim binding, or anchor no longer a member (task 2.3/2.5).</summary>
    public const int SendDenied = -32007;

    /// <summary>Per-identity active-binding quota exceeded on <c>requestGroupClaim</c> (task 2.1).</summary>
    public const int QuotaExceeded = -32008;

    /// <summary>Generic PII-free message for a denied group send (anti-oracle: same for all deny reasons).</summary>
    public const string SendDeniedMessage = "Group send denied: no valid claim for this group.";
}

/// <summary>Why a group send was denied (server-side diagnostics only; the client sees one generic code).</summary>
public enum GroupSendDenyReason
{
    /// <summary>No active (non-revoked, non-expired) binding for the caller on this group.</summary>
    NoBinding,

    /// <summary>The anchor is no longer a member of the group — binding invalidated (T2 anchor-churn).</summary>
    AnchorNotMember,

    /// <summary>Membership could not be determined (transient) — send denied, binding preserved.</summary>
    MembershipUnknown,
}

/// <summary>The gate decision for a single group recipient.</summary>
/// <param name="Allowed">Whether the send to this group is authorized.</param>
/// <param name="Reason">The deny reason when <see cref="Allowed"/> is <c>false</c>.</param>
public readonly record struct GroupSendDecision(bool Allowed, GroupSendDenyReason Reason)
{
    /// <summary>An allow decision.</summary>
    public static GroupSendDecision Allow => new(true, default);

    /// <summary>A deny decision with <paramref name="reason"/>.</summary>
    public static GroupSendDecision Deny(GroupSendDenyReason reason) => new(false, reason);
}

/// <summary>
/// Recipient-level authorization for group sends through the shared bot (add-group-claim-receive, tasks
/// 2.3/2.5). A group-target send via a shared-bot account is gated by a valid <c>(identity, group)</c>
/// binding AND the per-send anchor check <c>anchorAci ∈ members(group)</c> — NOT by account ownership.
/// </summary>
/// <remarks>
/// Активний ЛИШЕ коли <see cref="GroupClaimOptions.Enabled"/> і акаунт — shared-bot
/// (<see cref="IsSharedBotAccount"/>): own-linked акаунти (приватний binding, R3.5) шлють у власні групи
/// без claim-гейту. T2 anchor-churn: anchor вийшов/вигнаний → send-відмова + <see cref="IDurableStore.RevokeGroupBinding"/>
/// (інвалідизація binding-у). Транзієнт member-check → відмова БЕЗ revoke. Singleton.
/// </remarks>
public sealed class GroupSendGate
{
    private readonly IDurableStore _store;
    private readonly GroupMemberCache _memberCache;
    private readonly GroupClaimOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GroupSendGate> _logger;

    /// <summary>Creates the send-gate.</summary>
    public GroupSendGate(
        IDurableStore store,
        GroupMemberCache memberCache,
        IOptions<GroupClaimOptions> options,
        TimeProvider timeProvider,
        ILogger<GroupSendGate> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _memberCache = memberCache ?? throw new ArgumentNullException(nameof(memberCache));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the group-claim feature is enforced (else the gate is inert — passthrough).</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Whether <paramref name="account"/> is a shared-bot account (no owner OR admin-owned communal,
    /// i.e. binding <c>IsPrivate == false</c>). Own-linked personal numbers (<c>IsPrivate == true</c>,
    /// R3.5) are NOT shared-bot and bypass the claim gate.
    /// </summary>
    public bool IsSharedBotAccount(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return false;

        var binding = _store.GetAccountBinding(account);
        return binding is null || !binding.IsPrivate;
    }

    /// <summary>
    /// Evaluates whether <paramref name="callerIdentity"/> may send to <paramref name="groupId"/> through
    /// the shared-bot <paramref name="account"/>: active binding + <c>anchorAci ∈ members(group)</c>.
    /// On a positively-not-a-member verdict the binding is revoked (T2).
    /// </summary>
    public async ValueTask<GroupSendDecision> EvaluateAsync(
        string callerIdentity, string account, string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var now = _timeProvider.GetUtcNow();

        // 1. Активний binding (не revoked, не expired) — інакше відмова (task 2.3, primary gate).
        var binding = _store.GetGroupBinding(callerIdentity, groupId);
        if (binding is null || binding.Revoked || binding.ExpiresAt <= now)
        {
            _logger.LogInformation(
                "Send-gate: немає активного binding (identity {IdentityId}, група {GroupId}) → відмова.",
                callerIdentity, groupId);
            return GroupSendDecision.Deny(GroupSendDenyReason.NoBinding);
        }

        // 2. Anchor-перевірка членства (T2, кеш ~60с). Not-member → відмова + інвалідизація binding-у.
        var membership = await _memberCache.CheckAsync(account, groupId, binding.AnchorAci, cancellationToken)
            .ConfigureAwait(false);
        switch (membership)
        {
            case GroupMembership.Member:
                return GroupSendDecision.Allow;

            case GroupMembership.NotMember:
                _store.RevokeGroupBinding(binding.BindingId);
                _memberCache.Invalidate(groupId);
                _logger.LogInformation(
                    "Send-gate: anchor не член групи {GroupId} — binding інвалідовано (identity {IdentityId}).",
                    groupId, callerIdentity);
                return GroupSendDecision.Deny(GroupSendDenyReason.AnchorNotMember);

            default:
                _logger.LogInformation(
                    "Send-gate: членство anchor у групі {GroupId} невизначене (транзієнт) → відмова без revoke.",
                    groupId);
                return GroupSendDecision.Deny(GroupSendDenyReason.MembershipUnknown);
        }
    }
}
