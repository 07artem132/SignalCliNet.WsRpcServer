namespace SignalCliNet.WsRpcServer.Events;

/// <summary>
/// Broadcasts an app-level JSON-RPC notification to every currently-registered WebSocket client
/// (fan-out over the notification infra). Used for server-initiated app notifications that are NOT tied
/// to a Signal event subscription: the server-heartbeat (task 3.2) and bot pause/resume state (task 2.2).
/// </summary>
/// <remarks>
/// Реалізується <c>EventProcessor</c> (той самий singleton, що й <see cref="IEventProcessor"/>) — одна
/// інстанція, дві ролі. Payload МУСИТЬ бути без PII (A1): лише timestamp/seq/pause-стан/reason. Доставка —
/// fire-and-forget через ту саму per-client чергу, що й події (з M1 авто-відпискою «зламаних» клієнтів).
/// </remarks>
public interface IAppNotifier
{
    /// <summary>
    /// Fan-outs a notification with <paramref name="method"/> and a single PII-free <paramref name="payload"/>
    /// object to all registered clients. No-op if there are no clients.
    /// </summary>
    /// <param name="method">The JSON-RPC notification method name (e.g. <c>heartbeat</c>, <c>bot.paused</c>).</param>
    /// <param name="payload">A PII-free payload object (serialized as the single positional param).</param>
    void Broadcast(string method, object payload);
}
