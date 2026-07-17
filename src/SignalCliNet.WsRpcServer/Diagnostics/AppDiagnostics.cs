using System.Diagnostics.Metrics;

namespace SignalCliNet.WsRpcServer.Diagnostics;

/// <summary>
/// Application-level telemetry source (task 1.1): a <see cref="Meter"/> named
/// <c>"SignalCliNet.WsRpcServer"</c>, SEPARATE from the framework meter <c>"WsRpcServer"</c>
/// (<c>WsRpcServerDiagnostics</c>). It carries the app-specific send / pause / dedup / heartbeat
/// instruments this bridge owns.
/// </summary>
/// <remarks>
/// <para>
/// Privacy — інваріант (дзеркало SignalCli.NET rule #1 / фреймворкового <c>WsRpcServerDiagnostics</c>):
/// теги вимірів походять ЛИШЕ з <see cref="AllowedTagKeys"/> (єдиний ключ <c>result</c> зі статус-літералів),
/// НІКОЛИ не несуть account/номерів/тіл/токенів. Guard-тест з <see cref="MeterListener"/> пінить allowlist.
/// </para>
/// <para>
/// Інструментація інертна без підписників (opt-in/pull) — нульовий вплив на поведінку/продуктивність,
/// якщо ніхто не слухає. Імена інструментів крапко-розділені (<c>wsrpc.app.*</c>, OTel-конвенція).
/// </para>
/// </remarks>
public static class AppDiagnostics
{
    /// <summary>The app-level meter name (distinct from the framework meter <c>"WsRpcServer"</c>).</summary>
    public const string MeterName = "SignalCliNet.WsRpcServer";

    /// <summary>The single allowed measurement tag key (the privacy allowlist).</summary>
    public const string ResultTagKey = "result";

    /// <summary>Fixed allowlist of tag keys — the privacy guard asserts every captured tag key is in here.</summary>
    public static IReadOnlyCollection<string> AllowedTagKeys { get; } = [ResultTagKey];

    private static readonly Meter Meter = new(MeterName);

    // wsrpc.app.sends{result=ok|denied|paused|deduped} — підсумок рішень по send-шляху.
    private static readonly Counter<long> Sends =
        Meter.CreateCounter<long>("wsrpc.app.sends", unit: "{send}",
            description: "Рішення send-шляху за результатом (ok/denied/paused/deduped).");

    // wsrpc.app.global_pause — UpDownCounter 0/1: +1 на паузу, -1 на resume (кумулятив = стан).
    private static readonly UpDownCounter<long> GlobalPause =
        Meter.CreateUpDownCounter<long>("wsrpc.app.global_pause", unit: "{state}",
            description: "Стан глобальної паузи бота (1 — паузнуто, 0 — активно).");

    // wsrpc.app.dedup{result=hit_done|hit_pending|miss} — результат idempotency-lookup.
    private static readonly Counter<long> Dedups =
        Meter.CreateCounter<long>("wsrpc.app.dedup", unit: "{lookup}",
            description: "Результат idempotency-дедуплікації (hit_done/hit_pending/miss).");

    // wsrpc.app.heartbeats — лічильник відправлених heartbeat-нотифікацій.
    private static readonly Counter<long> Heartbeats =
        Meter.CreateCounter<long>("wsrpc.app.heartbeats", unit: "{heartbeat}",
            description: "App-level heartbeat-нотифікації, розіслані активним WS-клієнтам.");

    /// <summary>Send-шлях: результат рішення.</summary>
    public enum SendResult
    {
        /// <summary>Допущено й відправлено.</summary>
        Ok,

        /// <summary>Відхилено admission (не пауза).</summary>
        Denied,

        /// <summary>Відхилено через глобальну паузу бота.</summary>
        Paused,

        /// <summary>Дедупльовано (idempotency hit done) — повернуто збережений результат.</summary>
        Deduped,
    }

    /// <summary>Idempotency-lookup: результат.</summary>
    public enum DedupResult
    {
        /// <summary>Знайдено завершений запис — повертаємо збережений результат.</summary>
        HitDone,

        /// <summary>Знайдено pending — outcome unknown (at-most-once).</summary>
        HitPending,

        /// <summary>Запису немає — свіжий send.</summary>
        Miss,
    }

    /// <summary>Лічить рішення send-шляху з тегом <c>result</c>.</summary>
    public static void Send(SendResult result) =>
        Sends.Add(1, new KeyValuePair<string, object?>(ResultTagKey, result switch
        {
            SendResult.Ok => "ok",
            SendResult.Denied => "denied",
            SendResult.Paused => "paused",
            SendResult.Deduped => "deduped",
            _ => "denied",
        }));

    /// <summary>Позначає активацію глобальної паузи (гейдж → 1).</summary>
    public static void GlobalPauseActivated() => GlobalPause.Add(1);

    /// <summary>Позначає зняття глобальної паузи (гейдж → 0).</summary>
    public static void GlobalPauseDeactivated() => GlobalPause.Add(-1);

    /// <summary>Лічить idempotency-lookup з тегом <c>result</c>.</summary>
    public static void Dedup(DedupResult result) =>
        Dedups.Add(1, new KeyValuePair<string, object?>(ResultTagKey, result switch
        {
            DedupResult.HitDone => "hit_done",
            DedupResult.HitPending => "hit_pending",
            DedupResult.Miss => "miss",
            _ => "miss",
        }));

    /// <summary>Лічить одну розіслану heartbeat-нотифікацію.</summary>
    public static void Heartbeat() => Heartbeats.Add(1);
}
