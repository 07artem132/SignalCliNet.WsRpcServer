using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Options for the reactive global-pause gate (config section <c>Server:Pause</c>, task 2.2). When
/// signal-cli reports a rate-limit (<c>-5</c>) or captcha-required (<c>-6</c>) on the send path, the bot
/// pauses admission for a jittered, escalating backoff so retries can't hammer Signal into a ban.
/// </summary>
/// <remarks>
/// Пауза — реактивна й additive: вона лише деніїть нові send через admission-gate (перший крок W16) і
/// шле <c>bot.paused</c>/<c>bot.resumed</c>. Поведінка send БЕЗ активної паузи незмінна. Auto-resume
/// детектується монотонним годинником (як вікна бюджету, D15) — імунний до стрибків wall-clock.
/// </remarks>
public sealed class GlobalPauseOptions
{
    /// <summary>Configuration section name (<c>Server:Pause</c>).</summary>
    public const string SectionName = "Server:Pause";

    /// <summary>
    /// Base pause duration in seconds applied on the first rate-limit/captcha in a burst (5..3600).
    /// Default 60. Consecutive triggers within a burst escalate this (×2 per step) up to
    /// <see cref="MaxPauseSeconds"/>, plus up to +25% jitter.
    /// </summary>
    [Range(5, 3600)]
    public int BasePauseSeconds { get; set; } = 60;

    /// <summary>
    /// Cap on the (escalated + jittered) pause duration in seconds (5..21600). Default 900 (15 min).
    /// MUST be ≥ <see cref="BasePauseSeconds"/> (cross-rule).
    /// </summary>
    [Range(5, 21600)]
    public int MaxPauseSeconds { get; set; } = 900;
}
