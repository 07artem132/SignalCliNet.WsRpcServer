using System.Text.Json.Serialization;

namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// Application-level JSON-RPC error codes for admission / rate-limit denials (task 4.1). These sit
/// OUTSIDE the JSON-RPC reserved band (-32768..-32000 is protocol-reserved; -32005 is the reserved
/// implementation-defined server-error slot we use consistently for "throttled — retry later").
/// </summary>
public static class AdmissionErrorCodes
{
    /// <summary>
    /// A send/dispatch was refused by a rate/admission limit. The error carries a
    /// <see cref="RateLimitErrorData"/> payload in <c>error.data</c> with the in-band
    /// <c>retry_after</c> hint (seconds), so a browser WebSocket client — which never sees the
    /// pre-upgrade HTTP <c>429</c> — still learns how long to wait (S9).
    /// </summary>
    public const int RateLimited = -32005;

    /// <summary>Human-safe, PII-free client message for a rate-limit denial.</summary>
    public const string RateLimitedMessage = "Rate limit exceeded";
}

/// <summary>
/// The <c>error.data</c> payload of a <see cref="AdmissionErrorCodes.RateLimited"/> (<c>-32005</c>)
/// response: the in-band retry hint (A1 — carries NO secrets, only a seconds count).
/// </summary>
/// <remarks>
/// Серіалізується source-gen-контекстом (зареєстрований у <c>SignalCliSerializerContext</c>, як
/// <c>ApiVersionRequirement</c>). Явний <c>[JsonPropertyName("retry_after")]</c> фіксує snake_case на
/// дроті незалежно від camelCase-політики форматтера — це стабільний машиночитний контракт для клієнта.
/// </remarks>
/// <param name="RetryAfter">Seconds the client should wait before retrying (≥ 1).</param>
public sealed record RateLimitErrorData(
    [property: JsonPropertyName("retry_after")] int RetryAfter);
