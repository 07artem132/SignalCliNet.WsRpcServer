using Microsoft.Extensions.Logging;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Turns a thrown exception into a client-safe JSON-RPC error detail: generic text for the client,
/// full detail only in the server log (privacy contract; task 3.1).
/// </summary>
/// <remarks>
/// Без цього StreamJsonRpc за замовчуванням загортає будь-який виняток у <c>-32000</c> з
/// <c>CommonErrorData</c> (тип, повідомлення, СТЕК) у <c>error.data</c> — тобто трейси течуть клієнту.
/// Політика санітизації:
/// <list type="bullet">
///   <item><see cref="RpcErrorException"/> — автор навмисно обрав код+повідомлення (generic за
///     конвенцією), тож пропускаємо їх та (за наявності) author-controlled <c>ErrorData</c>
///     (напр. «upgrade required»); внутрішній виняток — лише в лог.</item>
///   <item>будь-який інший виняток — клієнту generic <c>-32603</c> без деталей; повний виняток — у лог.</item>
/// </list>
/// </remarks>
public static class RpcErrorSanitizer
{
    /// <summary>JSON-RPC internal-error code used for unexpected (non-<see cref="RpcErrorException"/>) faults.</summary>
    public const int InternalErrorCode = -32603;

    /// <summary>
    /// Builds a sanitized <see cref="JsonRpcError.ErrorDetail"/> for <paramref name="exception"/> and logs
    /// server-side details.
    /// </summary>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="method">The RPC method name (diagnostics only).</param>
    /// <param name="logger">Optional logger for server-side detail.</param>
    /// <returns>A client-safe error detail.</returns>
    public static JsonRpcError.ErrorDetail CreateDetail(Exception exception, string? method, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is RpcErrorException known)
        {
            // Внутрішній виняток (причина) — тільки в серверний лог, ніколи клієнту.
            if (known.InnerException is not null)
            {
                logger?.LogError(known.InnerException,
                    "RPC {Method}: оброблена помилка (код {Code})", method, (int)known.ErrorCode);
            }

            return new JsonRpcError.ErrorDetail
            {
                Code = known.ErrorCode,
                Message = known.Message,   // author-controlled generic text
                Data = known.ErrorData,    // author-controlled safe payload (напр. ApiVersionRequirement)
            };
        }

        // Несподіваний виняток: клієнту — generic; повні деталі — лише в серверний лог.
        logger?.LogError(exception, "RPC {Method}: несподівана помилка — клієнту віддаємо generic", method);

        return new JsonRpcError.ErrorDetail
        {
            Code = (JsonRpcErrorCode)InternalErrorCode,
            Message = "Internal server error.",
        };
    }
}
