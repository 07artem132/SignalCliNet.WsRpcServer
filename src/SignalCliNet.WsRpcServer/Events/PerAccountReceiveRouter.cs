using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Events;

/// <summary>The routing scope of an incoming daemon event, keyed on its recipient account.</summary>
public enum ReceiveScope
{
    /// <summary>
    /// Feature disabled — deliver to subscribed clients exactly as before (current behaviour). MUST be the
    /// default enum value so an absent router (<c>null</c>) yields passthrough via <c>default</c>.
    /// </summary>
    Passthrough,

    /// <summary>Own-linked personal account (private binding, R3.5) — deliver only to its owner's clients.</summary>
    OwnLinked,

    /// <summary>Shared-bot stream (no owner OR admin-owned communal) — deliver ONLY to the scanner.</summary>
    SharedBot,
}

/// <summary>The routing verdict for an account.</summary>
/// <param name="Scope">The routing scope.</param>
/// <param name="OwningIdentityId">The owner identity for <see cref="ReceiveScope.OwnLinked"/>; otherwise <c>null</c>.</param>
public readonly record struct ReceiveRoute(ReceiveScope Scope, string? OwningIdentityId);

/// <summary>
/// The single per-account receive-router (S11) — the ONE node the raw daemon notification stream passes
/// through, fanning out by <c>account → owning-identity</c> (add-group-claim-receive, task 1.4). This is
/// the load-bearing seam that makes the R3.5/W25-inbound isolation a MECHANISM, not an artifact of "there
/// is no surface yet": every present and future receive/event consumer classifies an account here first.
/// </summary>
/// <remarks>
/// Класифікація (семантика <see cref="AccountBinding.IsPrivate"/> у цьому кодбейсі): own-linked персональний
/// номер прив'язується приватно (<c>IsPrivate=true</c>, R3.5, <c>finishLink</c>); shared-bot / legacy —
/// admin-owned комунальний (<c>IsPrivate=false</c>) або без binding. Тож:
/// <list type="bullet">
///   <item>binding відсутній → <see cref="ReceiveScope.SharedBot"/> (немає власника);</item>
///   <item><c>IsPrivate=true</c> → <see cref="ReceiveScope.OwnLinked"/> (доставка лише власнику);</item>
///   <item><c>IsPrivate=false</c> → <see cref="ReceiveScope.SharedBot"/> (лише сканеру).</item>
/// </list>
/// Прапорець <see cref="GroupClaimOptions.Enabled"/> вимкнено → завжди
/// <see cref="ReceiveScope.Passthrough"/> (поточна поведінка). Forward-правило T7 (Phase-3 events-consumer
/// отримує з shared-bot-потоку ЛИШЕ envelope'и груп, на які має активний binding) РОЗШИРЮЄ цей роутер,
/// не ревізуючи інваріант. Singleton.
/// </remarks>
public sealed class PerAccountReceiveRouter(IDurableStore store, IOptions<GroupClaimOptions> options)
{
    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly GroupClaimOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <summary>Whether routing is active (else every account is <see cref="ReceiveScope.Passthrough"/>).</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>Classifies <paramref name="account"/> into a receive-scope (passthrough when disabled).</summary>
    /// <param name="account">The recipient account of the incoming event (E.164).</param>
    /// <returns>The routing verdict.</returns>
    public ReceiveRoute Route(string? account)
    {
        // Вимкнено → passthrough (поточна поведінка): доставка підписаним клієнтам, без сканера.
        if (!_options.Enabled || string.IsNullOrWhiteSpace(account))
            return new ReceiveRoute(ReceiveScope.Passthrough, null);

        var binding = _store.GetAccountBinding(account);
        if (binding is null)
            return new ReceiveRoute(ReceiveScope.SharedBot, null);       // немає власника → shared-bot

        return binding.IsPrivate
            ? new ReceiveRoute(ReceiveScope.OwnLinked, binding.OwningIdentityId) // R3.5 own-linked → власнику
            : new ReceiveRoute(ReceiveScope.SharedBot, null);           // admin-owned комунальний → сканеру
    }
}
