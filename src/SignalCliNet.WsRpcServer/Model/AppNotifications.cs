using System.Text.Json.Serialization;

namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// Payload of the server-heartbeat notification (<c>heartbeat</c>, task 3.2). Carries NO PII — only a
/// monotonic sequence number, a server timestamp (Unix ms) and the current global-pause state, so an
/// ephemeral client (MV3 Service Worker) can tell a live socket from a hung one (C5).
/// </summary>
/// <param name="Seq">Monotonic heartbeat sequence number (per server run).</param>
/// <param name="Timestamp">Server wall-clock at emission (Unix epoch milliseconds).</param>
/// <param name="Paused">Whether the bot is globally paused right now (surfaced state, task 2.2c).</param>
public sealed record HeartbeatNotification(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("paused")] bool Paused);

/// <summary>
/// Payload of the bot pause/resume notifications (<c>bot.paused</c> / <c>bot.resumed</c>, task 2.2a).
/// Carries NO PII — only a machine-readable reason token and the in-band <c>retryAfter</c> hint.
/// </summary>
/// <param name="Reason">Machine-readable, PII-free reason (e.g. <c>rate-limit</c>, <c>captcha</c>); empty on resume.</param>
/// <param name="RetryAfterSeconds">Seconds until the auto-resume deadline (0 on resume).</param>
public sealed record PauseStateNotification(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("retryAfter")] int RetryAfterSeconds);
