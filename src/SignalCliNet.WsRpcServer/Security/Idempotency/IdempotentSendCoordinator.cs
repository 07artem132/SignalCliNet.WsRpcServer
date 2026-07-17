using SignalCliNet.WsRpcServer.Diagnostics;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security.Admission;

namespace SignalCliNet.WsRpcServer.Security.Idempotency;

/// <summary>The kind of decision an <see cref="IdempotentSendCoordinator"/> returned for a send.</summary>
public enum IdempotentSendKind
{
    /// <summary>Admitted, no idempotency key — current behaviour, nothing to complete afterwards.</summary>
    AdmittedFresh,

    /// <summary>Admitted with an idempotency key — a <c>pending</c> row was claimed; complete after dispatch.</summary>
    AdmittedPending,

    /// <summary>Denied by admission (<see cref="IdempotentSendOutcome.Decision"/> carries reason + retry).</summary>
    Denied,

    /// <summary>Dedup hit <c>done</c> — return <see cref="IdempotentSendOutcome.SavedResultJson"/>, no admission.</summary>
    DedupDone,

    /// <summary>Dedup hit <c>pending</c> — outcome unknown (at-most-once); do not resend.</summary>
    OutcomeUnknown,
}

/// <summary>The outcome of <see cref="IdempotentSendCoordinator.Begin"/>.</summary>
/// <param name="Kind">The decision kind.</param>
/// <param name="Decision">The admission decision when <see cref="Kind"/> is <see cref="IdempotentSendKind.Denied"/>.</param>
/// <param name="SavedResultJson">The stored result JSON when <see cref="Kind"/> is <see cref="IdempotentSendKind.DedupDone"/>.</param>
public sealed record IdempotentSendOutcome(
    IdempotentSendKind Kind,
    AdmissionDecision? Decision = null,
    string? SavedResultJson = null)
{
    /// <summary>Admitted (no idempotency key).</summary>
    public static IdempotentSendOutcome AdmittedFresh { get; } = new(IdempotentSendKind.AdmittedFresh);

    /// <summary>Admitted with a claimed <c>pending</c> dedup row.</summary>
    public static IdempotentSendOutcome AdmittedPending { get; } = new(IdempotentSendKind.AdmittedPending);

    /// <summary>Dedup hit <c>pending</c> — outcome unknown.</summary>
    public static IdempotentSendOutcome OutcomeUnknown { get; } = new(IdempotentSendKind.OutcomeUnknown);

    /// <summary>Denied by admission.</summary>
    public static IdempotentSendOutcome FromDenied(AdmissionDecision decision) =>
        new(IdempotentSendKind.Denied, Decision: decision);

    /// <summary>Dedup hit <c>done</c> with the stored result.</summary>
    public static IdempotentSendOutcome FromDone(string? resultJson) =>
        new(IdempotentSendKind.DedupDone, SavedResultJson: resultJson);
}

/// <summary>
/// Coordinates send admission with durable idempotency (task 3.3, T6/C2/S8). Sits at the send chokepoint:
/// the dedup-lookup runs BEFORE the admission function, and on a miss the aggregate-budget reservation
/// (via <see cref="AdmissionControl"/>) is followed by an atomic <c>pending</c> claim so a crash between
/// the claim and the actual send resolves to "outcome unknown" (at-most-once, W13 forfeit).
/// </summary>
/// <remarks>
/// <para>
/// <b>Точна dedup+budget послідовність (ordering-i, атомарність, що має значення).</b> Для keyed-send:
/// </para>
/// <list type="number">
///   <item><see cref="IDurableStore.GetSendDedup"/> ПЕРЕД admission. <c>done</c> → повертаємо збережений
///     результат БЕЗ admission і БЕЗ декременту бюджету (metric hit_done). <c>pending</c> → outcome
///     unknown (metric hit_pending). miss → далі (metric miss).</item>
///   <item>miss: <see cref="AdmissionControl.Admit"/> — усі W16-гейти, включно з DURABLE
///     reserve-then-send блок-резервацією бюджету (crash-safe форфейт). Deny → повертаємо denial БЕЗ
///     запису dedup-рядка (жодних «залиплих» pending).</item>
///   <item>Admitted (одиницю бюджету durably зарезервовано) → <see cref="IDurableStore.TryBeginSend"/>:
///     атомарний <c>INSERT OR IGNORE</c> pending-рядка (budget_reserved=1). Виграв claim → AdmittedPending
///     (chokepoint диспетчить, далі <see cref="Complete"/>). Гонка програна (уже pending) → форфейт нашої
///     одиниці, outcome unknown.</item>
/// </list>
/// <para>
/// <b>Чому саме так (а не одна SQL-транзакція).</b> Reserve-then-send бюджет — durable-at-block +
/// crash-forfeit; його <c>InsertBudgetBlock</c> виконується власною транзакцією й НЕ може безпечно
/// вкластися у зовнішню транзакцію Microsoft.Data.Sqlite (pending-transaction enrollment). Тож бюджет
/// резервуємо ДО claim-у (durable-first), а claim — атомарний одиничний <c>INSERT OR IGNORE</c>.
/// Інваріанти зберігаються: (a) budget-exhaust → deny ДО claim → жодного залиплого pending; (b) краш після
/// claim, до/після send → ретрай бачить pending → outcome-unknown → БЕЗ повторного send і БЕЗ повторного
/// декременту (блок durable, in-memory-одиниця форфейтиться рівно як W13); (c) over-send неможливий. Єдиний
/// «коштовний» край — конкурентний дубль того самого messageId: обидва резервують одиницю, програвший
/// claim форфейтить свою (benign under-count, НІКОЛИ не over-send).
/// </para>
/// </remarks>
public sealed class IdempotentSendCoordinator(
    AdmissionControl admission,
    IDurableStore store,
    TimeProvider timeProvider)
{
    private readonly AdmissionControl _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Decides a send: dedup-lookup (when <paramref name="messageId"/> is set) BEFORE admission, then, on a
    /// miss, admission + an atomic <c>pending</c> claim.
    /// </summary>
    /// <param name="identityId">The sending identity (the dedup/budget key).</param>
    /// <param name="messageId">The client idempotency key, or <c>null</c>/empty for the legacy no-dedup path.</param>
    /// <param name="recipients">The send's recipients (never logged/stored here).</param>
    /// <returns>The coordinated outcome the chokepoint acts on.</returns>
    public IdempotentSendOutcome Begin(string identityId, string? messageId, IReadOnlyList<string> recipients)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentNullException.ThrowIfNull(recipients);

        // Немає messageId → поточна поведінка admission (без dedup) — зворотна сумісність.
        if (string.IsNullOrEmpty(messageId))
        {
            var plain = _admission.Admit(identityId, recipients);
            return plain.Admitted ? IdempotentSendOutcome.AdmittedFresh : IdempotentSendOutcome.FromDenied(plain);
        }

        // 1. Dedup-lookup ПЕРЕД admission (T6/C2).
        var existing = _store.GetSendDedup(identityId, messageId);
        if (existing is not null)
        {
            if (string.Equals(existing.Status, SendDedupRecord.StatusDone, StringComparison.Ordinal))
            {
                AppDiagnostics.Dedup(AppDiagnostics.DedupResult.HitDone);
                return IdempotentSendOutcome.FromDone(existing.ResultJson);
            }

            AppDiagnostics.Dedup(AppDiagnostics.DedupResult.HitPending);
            return IdempotentSendOutcome.OutcomeUnknown;
        }

        AppDiagnostics.Dedup(AppDiagnostics.DedupResult.Miss);

        // 2. Miss → admission (усі гейти, вкл. durable budget-резервацію) ДО claim-у.
        var decision = _admission.Admit(identityId, recipients);
        if (!decision.Admitted)
            return IdempotentSendOutcome.FromDenied(decision);

        // 3. Бюджет зарезервовано → атомарний claim pending-рядка.
        if (!_store.TryBeginSend(identityId, messageId, _timeProvider.GetUtcNow()))
        {
            // Програна гонка claim-у (уже pending): форфейт нашої одиниці, outcome unknown (at-most-once).
            AppDiagnostics.Dedup(AppDiagnostics.DedupResult.HitPending);
            return IdempotentSendOutcome.OutcomeUnknown;
        }

        return IdempotentSendOutcome.AdmittedPending;
    }

    /// <summary>
    /// Marks a keyed send <c>done</c> with its serialized <paramref name="resultJson"/> after a successful
    /// dispatch, so future retries of the same <paramref name="messageId"/> return it verbatim.
    /// </summary>
    /// <param name="identityId">The sending identity.</param>
    /// <param name="messageId">The client idempotency key.</param>
    /// <param name="resultJson">The serialized send result to persist.</param>
    public void Complete(string identityId, string messageId, string resultJson) =>
        _store.CompleteSend(identityId, messageId, resultJson);
}
