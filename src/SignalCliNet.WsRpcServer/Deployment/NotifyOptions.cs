using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Options for team notifications (config section <c>Notify</c>, add-team-notify): per-chat coalescing
/// window and the retention of chat-scoped send-dedup rows.
/// </summary>
/// <remarks>
/// Coalescing діє лише на gated shared-bot send у рівно одну групу (той самий шлях, що й group-claim).
/// Черга вікна живе в пам'яті: graceful stop її відправляє, краш процесу — чесно губить (задокументовано).
/// </remarks>
public sealed class NotifyOptions
{
    /// <summary>Configuration section name (<c>Notify</c>).</summary>
    public const string SectionName = "Notify";

    /// <summary>
    /// Coalescing window per chat in seconds (0..3600). The first message to a chat goes out immediately;
    /// the following ones within the window are joined into one message at its end. <c>0</c> disables it.
    /// Default 300.
    /// </summary>
    [Range(0, 3600)]
    public int CoalesceWindowSeconds { get; set; } = 300;

    /// <summary>Retention of chat-scoped dedup rows <c>(groupId, messageId)</c> in hours (1..720). Default 72.</summary>
    [Range(1, 720)]
    public int DedupTtlHours { get; set; } = 72;
}
