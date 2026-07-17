using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Options for the server-heartbeat (config section <c>Server:Heartbeat</c>, task 3.2). An app-level
/// JSON-RPC <c>heartbeat</c> notification is fanned out to every active WS client on a fixed sub-25s
/// cadence so ephemeral clients (MV3 Service Worker) keep a persistent socket alive and can tell a live
/// socket from a hung one (C5 server half).
/// </summary>
/// <remarks>
/// Additive/gated: heartbeat — ЛИШЕ нотифікації (поведінка send незмінна). Дефолт <see cref="Enabled"/>=
/// <c>true</c>; вимкнути можна для connect-on-demand MVP, де persistent-WS немає. Це НЕ WS-ping
/// (той — фреймворковий транспортний рівень) — це app-level notification через notify-інфру.
/// </remarks>
public sealed class HeartbeatOptions
{
    /// <summary>Configuration section name (<c>Server:Heartbeat</c>).</summary>
    public const string SectionName = "Server:Heartbeat";

    /// <summary>Whether the server emits app-level heartbeats. Default <c>true</c> (additive — notifications only).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Heartbeat interval in seconds (1..24 — kept strictly under the 25s client budget, C5). Default 20.
    /// </summary>
    [Range(1, 24)]
    public int IntervalSeconds { get; set; } = 20;
}
