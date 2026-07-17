using Microsoft.Extensions.Logging;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Devices;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Adapter for the device-linking RPC surface. Wraps the upstream <see cref="ISignalDevices"/> facade
/// with the link-session state machine (add-link-sessions): <c>startLink</c> mints a one-time session and
/// returns <c>{sessionId, deviceLinkUri}</c>; <c>finishLink</c> consumes the session by id and binds the
/// linked account to the caller identity.
/// </summary>
/// <remarks>
/// Автентифікація/authz per-invocation читаються з <see cref="SignalCallContext"/> (V8), не з конструктора.
/// Loopback (auth вимкнено) → <see cref="SignalPrincipal.LoopbackFullAccess"/> (identityId = "loopback").
///
/// Privacy (task 6): сирий device-link URI / QR — ЧУТЛИВЕ; жоден лог-виклик тут його не друкує. Клієнт
/// оперує лише <c>sessionId</c>; справжній URI живе у сторі й іде в upstream напряму.
///
/// Захист бот-акаунта від takeover (task 3): <c>startLink</c> з target-hint, що вже є прив'язаним акаунтом
/// у сторі (спільний бот), дозволений лише привілейованому principal; новий власний номер після
/// <c>finishLink</c> приватно прив'язується до caller (R3.5) за ФАКТИЧНИМ номером із
/// <see cref="FinishLinkResponse"/> (не за target-hint).
/// </remarks>
public sealed class SignalDevicesRpcAdapter(
    ISignalDevices signalDevices,
    LinkSessionStore linkSessions,
    IDurableStore store,
    TimeProvider timeProvider,
    ILogger<SignalDevicesRpcAdapter> logger)
    : ISignalDevicesRpc
{
    /// <summary>
    /// JSON-RPC error code for an invalid <c>finishLink</c> session (unknown / foreign / expired). Одна
    /// помилка на всі три випадки — anti-oracle: клієнт не дізнається, чи сесія існує, чужа чи прострочена.
    /// <c>-32004</c> — вільний слот у implementation-defined band (сусіди: <c>-32001</c> unauthorized,
    /// <c>-32005</c> rate-limited). Задокументовано у <c>docs/self-host.md</c>.
    /// </summary>
    public const int LinkSessionInvalidCode = -32004;

    /// <summary>
    /// Per-call таймаут (секунди) для <c>finishLink</c> — дефолт 130 (TTL link-сесії 120с + 10с запас).
    /// Прокидається у <see cref="ISignalDevices.FinishLinkAsync"/> як <c>timeout</c>, щоб ручний скан QR
    /// ≤120с не був убитий глобальним 30с RPC-таймаутом send-шляху (W9: per-call ≥130 замість глобального
    /// bump). Глобальний <c>SignalCli:RequestTimeoutSeconds</c> лишається дефолтним і не чіпається.
    /// </summary>
    public const int FinishLinkTimeoutSeconds = 130;

    // Generic, PII-free повідомлення для невалідної сесії (однакове для unknown/foreign/expired).
    private const string LinkSessionInvalidMessage = "Link session is invalid or has expired.";

    private readonly ISignalDevices _signalDevices =
        signalDevices ?? throw new ArgumentNullException(nameof(signalDevices));

    private readonly LinkSessionStore _linkSessions =
        linkSessions ?? throw new ArgumentNullException(nameof(linkSessions));

    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<SignalDevicesRpcAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<StartLinkSessionResponse> StartLink(
        string? targetAccount = null, CancellationToken cancellationToken = default)
    {
        var principal = SignalCallContext.Principal ?? SignalPrincipal.LoopbackFullAccess;
        var identityId = principal.Name;

        // Захист від takeover: якщо target-hint = вже відомий (прив'язаний, «спільний») акаунт у сторі,
        // старт лінкування дозволений лише привілейованому principal (admin / loopback-full). Не-admin →
        // відмова + alert (БЕЗ розкриття власника). Перевірка ДО будь-якого upstream-виклику.
        if (!string.IsNullOrWhiteSpace(targetAccount)
            && _store.GetAccountBinding(targetAccount) is not null
            && !IsPrivileged(principal))
        {
            _logger.LogWarning(
                "ALERT: не-admin identity {Identity} намагається лінкувати вже-прив'язаний (спільний) акаунт — відмова.",
                identityId);
            throw new RpcErrorException(
                (JsonRpcErrorCode)AccountIsolationGuard.UnauthorizedErrorCode, "Access denied.");
        }

        _logger.LogInformation("RPC: запит startLink (identity {Identity})", identityId);

        StartLinkResponse upstream;
        try
        {
            upstream = await _signalDevices.StartLinkAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка старту лінкування пристрою");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error starting device linking", ex);
        }

        LinkSession session;
        try
        {
            // Redact (task 6): URI віддаємо в стор, але НІКОЛИ не логуємо.
            session = _linkSessions.Create(identityId, targetAccount, upstream.DeviceLinkUri);
        }
        catch (InvalidOperationException)
        {
            // Rate-limit стартів (3/хв/identity) → клієнту детермінований -32005 (retry_after ≈ вікно),
            // без деталей. Не InvocationError (це не збій upstream, а свідомий throttle).
            _logger.LogInformation("startLink відхилено rate-limit'ом (identity {Identity})", identityId);
            throw new RpcErrorException(
                (JsonRpcErrorCode)AdmissionErrorCodes.RateLimited,
                AdmissionErrorCodes.RateLimitedMessage,
                new RateLimitErrorData((int)LinkSessionStore.RateWindow.TotalSeconds));
        }

        return new StartLinkSessionResponse(session.SessionId, session.DeviceLinkUri);
    }

    /// <inheritdoc />
    public async Task<FinishLinkResponse> FinishLink(
        string sessionId, string deviceName, CancellationToken cancellationToken = default)
    {
        // Валідація на межі RPC: порожнє ім'я → InvalidParams, не InvocationError від демона.
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "deviceName cannot be empty");

        var principal = SignalCallContext.Principal ?? SignalPrincipal.LoopbackFullAccess;
        var callerIdentity = principal.Name;

        // Атомарне умовне гашення (task 2): сесія існує + не прострочена + ініціатор == caller → видалити
        // й повернути. Невідома / чужа / прострочена → null (anti-oracle: клієнту ІДЕНТИЧНА -32004).
        var session = _linkSessions.TryConsume(sessionId, callerIdentity);
        if (session is null)
            throw new RpcErrorException((JsonRpcErrorCode)LinkSessionInvalidCode, LinkSessionInvalidMessage);

        _logger.LogInformation("RPC: запит finishLink (identity {Identity})", callerIdentity);

        FinishLinkResponse response;
        try
        {
            // Клієнт передав лише sessionId; справжній URI беремо із сесії. Redact: URI не логуємо.
            // W9: per-call timeout ≥130с (TTL сесії 120с + запас) — SignalCli.NET 4.10.2 додав per-call
            // seam, тож ручний скан ≤120с більше не вбивається глобальним 30с RPC-таймаутом send-шляху.
            response = await _signalDevices.FinishLinkAsync(
                    session.DeviceLinkUri, deviceName, cancellationToken,
                    TimeSpan.FromSeconds(FinishLinkTimeoutSeconds))
                .ConfigureAwait(false);
        }
        catch (RpcErrorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка завершення лінкування пристрою");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error finishing device linking", ex);
        }

        // Прив'язка за ФАКТИЧНИМ акаунтом із FinishLinkResponse (не за target-hint).
        BindLinkedAccount(response.Number, callerIdentity, principal);

        return response;
    }

    // Прив'язка щойно-злінкованого акаунта (task 3): новий номер → приватний bind до caller (R3.5);
    // вже-прив'язаний — залишаємо власника як є (idempotent), не переписуємо на чужого caller (анти-takeover
    // на рівні стору) + alert, якщо caller не власник і не привілейований.
    private void BindLinkedAccount(string? account, string callerIdentity, SignalPrincipal principal)
    {
        if (string.IsNullOrWhiteSpace(account))
            return; // upstream не повернув номер — нема що прив'язувати (best-effort)

        var existing = _store.GetAccountBinding(account);
        if (existing is null)
        {
            // Новий власний номер → приватний bind до caller (R3.5: приватний, не видимий іншим identity).
            EnsureIdentityExists(callerIdentity, principal);
            _store.UpsertAccountBinding(new AccountBinding(
                account, callerIdentity, IsPrivate: true, _timeProvider.GetUtcNow()));
            _logger.LogInformation(
                "finishLink: новий номер приватно прив'язано до identity {Identity}.", callerIdentity);
            return;
        }

        if (string.Equals(existing.OwningIdentityId, callerIdentity, StringComparison.Ordinal)
            || IsPrivileged(principal))
        {
            // Власний повторний лінк або привілейований principal — наявний bind лишаємо незмінним.
            return;
        }

        // Вже-прив'язаний (спільний/чужий) акаунт, не-admin caller: НЕ переписуємо власника (анти-takeover)
        // + alert. Пристрій уже фізично злінковано upstream'ом, але store-власник у нашій моделі не зміщується.
        _logger.LogWarning(
            "ALERT: не-admin identity {Identity} завершила лінк уже-прив'язаного акаунта — bind збережено за наявним власником.",
            callerIdentity);
    }

    // FK-передумова: account_bindings.owning_identity_id → identities(id). У authn-режимі identity вже
    // існує (створена при enrollment); для loopback / першого лінку створюємо мінімальний identity-запис,
    // не чіпаючи наявний (create-if-absent). Роль — з principal (admin/user); бінд лишається приватним.
    private void EnsureIdentityExists(string identityId, SignalPrincipal principal)
    {
        if (_store.GetIdentity(identityId) is not null)
            return;

        var role = principal.IsAdmin ? "admin" : "user";
        _store.UpsertIdentity(new IdentityRecord(identityId, role, [], _timeProvider.GetUtcNow()));
    }

    // Привілейований = admin-нагляд або loopback-full (Phase-1 повний доступ). Спільний бот доступний
    // для лінкування/переприв'язки лише таким principal.
    private static bool IsPrivileged(SignalPrincipal principal) => principal.IsAdmin || principal.HasFullAccess;
}
