using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCli.Exceptions;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security.Admission;
using WsRpcServer.Exceptions;
// Однозначно беремо код помилки з StreamJsonRpc (SignalCli.Exceptions теж визначає JsonRpcErrorCode).
using JsonRpcErrorCode = StreamJsonRpc.Protocol.JsonRpcErrorCode;

namespace SignalCliNet.WsRpcServer.Security.Fail;

/// <summary>
/// The V10 send fail-path catch-matrix (tasks 2.1/2.2/2.3). Maps an exception thrown around the send
/// dispatch onto a SANITIZED client error (no upstream detail / PII leaks), triggers the global-pause
/// gate on rate-limit/captcha (-5/-6), and distinguishes a daemon-restart cancellation (W10) from a
/// genuine client cancellation.
/// </summary>
/// <remarks>
/// <para>Порядок розбору (derived перед base):</para>
/// <list type="number">
///   <item><see cref="RpcErrorException"/> — уже санітизований автором → пропускаємо як є.</item>
///   <item><see cref="RateLimitException"/> (-5) → global-pause + <c>-32005</c> з <c>retry_after</c>.</item>
///   <item><see cref="CaptchaRequiredException"/> (-6) → global-pause + <c>-32005</c> з <c>retry_after</c>.</item>
///   <item><see cref="UntrustedIdentityException"/> (-4) → <c>-32012</c> (per-recipient, БЕЗ паузи).</item>
///   <item><see cref="OperationCanceledException"/>: client-token скасовано → пропускаємо (client-cancel);
///     інакше (рестарт демона) → <c>-32009</c> «service restarting» (W10).</item>
///   <item><see cref="IOException"/>/<see cref="ObjectDisposedException"/>/<see cref="TimeoutException"/>
///     (розрив потоку від рестарту) → <c>-32009</c>.</item>
///   <item>базовий <see cref="JsonRpcException"/> (інші коди -1/-3 тощо) → <c>-32603</c> generic.</item>
///   <item>будь-що інше → generic <c>InvocationError</c> (як раніше — санітизовано, без PII).</item>
/// </list>
/// <para>
/// Obsolete-типи (<c>IdentityChangedException</c>, видалений в SignalCli.NET) НЕ ловимо — їх немає.
/// </para>
/// </remarks>
public sealed class UpstreamSendFailureMapper(
    IGlobalPauseGate pauseGate,
    IOptions<GlobalPauseOptions> pauseOptions,
    ILogger<UpstreamSendFailureMapper> logger)
{
    private readonly IGlobalPauseGate _pauseGate = pauseGate ?? throw new ArgumentNullException(nameof(pauseGate));
    private readonly TimeSpan _basePause =
        TimeSpan.FromSeconds((pauseOptions ?? throw new ArgumentNullException(nameof(pauseOptions))).Value.BasePauseSeconds);
    private readonly ILogger<UpstreamSendFailureMapper> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Generic fallback message for an unexpected send failure (no internal detail).</summary>
    public const string GenericSendErrorMessage = "Error sending text message";

    /// <summary>
    /// Maps <paramref name="ex"/> to the exception to (re)throw for <paramref name="method"/>. Never leaks
    /// upstream detail to the returned message; the full exception is logged server-side.
    /// </summary>
    /// <param name="ex">The exception thrown around dispatch.</param>
    /// <param name="clientToken">The client's cancellation token (distinguishes client-cancel from restart).</param>
    /// <param name="method">The RPC method name (diagnostics only).</param>
    /// <returns>The sanitized exception to throw.</returns>
    public Exception Map(Exception ex, CancellationToken clientToken, string method)
    {
        ArgumentNullException.ThrowIfNull(ex);

        switch (ex)
        {
            // Уже санітизований автором (валідація адаптера тощо) — пропускаємо без змін.
            case RpcErrorException:
                return ex;

            // -5 rate-limit → глобальна пауза бота + клієнту -32005 з in-band retry_after.
            case RateLimitException:
                return PauseAndRateLimit("rate-limit", method);

            // -6 captcha → так само глобальна пауза (наступні send отримають -32005/pause-стан).
            case CaptchaRequiredException:
                return PauseAndRateLimit("captcha", method);

            // -4 untrusted-identity → per-recipient, БЕЗ паузи; типізований -32012 (verify safety number).
            case UntrustedIdentityException:
                _logger.LogInformation("Send {Method}: untrusted-identity (-4) → -32012 (без паузи).", method);
                return new RpcErrorException(
                    (JsonRpcErrorCode)FailPathErrorCodes.UntrustedIdentity, FailPathErrorCodes.UntrustedIdentityMessage);

            // Скасування: client-token скасовано → це client-cancel, пропускаємо (StreamJsonRpc обробить).
            case OperationCanceledException when clientToken.IsCancellationRequested:
                return ex;

            // Скасування НЕ від клієнта, або розрив потоку — спричинено рестартом демона (W10) → -32009.
            case OperationCanceledException:
            case IOException:
            case ObjectDisposedException:
            case TimeoutException:
                _logger.LogWarning(
                    ex, "Send {Method}: in-flight скасовано/розірвано (ймовірно рестарт signal-cli) → -32009.", method);
                return new RpcErrorException(
                    (JsonRpcErrorCode)FailPathErrorCodes.ServiceRestarting, FailPathErrorCodes.ServiceRestartingMessage);

            // Базовий JSON-RPC виняток з іншим кодом (-1 user / -3 io тощо) → generic -32603 (код у лог).
            case JsonRpcException jsonRpc:
                _logger.LogError(
                    jsonRpc, "Send {Method}: upstream JSON-RPC помилка (код {Code}) → generic -32603.",
                    method, jsonRpc.Error.Code);
                return new RpcErrorException(
                    (JsonRpcErrorCode)RpcErrorSanitizer.InternalErrorCode, "Upstream service error.");

            // Будь-що несподіване → generic invocation-error (санітизовано; повний виняток у лог).
            default:
                _logger.LogError(ex, "Send {Method}: несподівана помилка → generic invocation-error.", method);
                return new RpcErrorException(JsonRpcErrorCode.InvocationError, GenericSendErrorMessage, ex);
        }
    }

    // Тригерить глобальну паузу з базовою тривалістю й повертає -32005 з актуальним retry_after (deadline).
    private RpcErrorException PauseAndRateLimit(string reason, string method)
    {
        _pauseGate.PauseFor(_basePause, reason);
        var retryAfter = _pauseGate.RetryAfterSeconds;
        _logger.LogWarning("Send {Method}: upstream {Reason} → global-pause + -32005 (retry_after={RetryAfter}s).",
            method, reason, retryAfter);
        return new RpcErrorException(
            (JsonRpcErrorCode)AdmissionErrorCodes.RateLimited,
            AdmissionErrorCodes.RateLimitedMessage,
            new RateLimitErrorData(Math.Max(1, retryAfter)));
    }
}
