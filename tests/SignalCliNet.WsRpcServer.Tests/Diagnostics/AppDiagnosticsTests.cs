using System.Diagnostics.Metrics;
using SignalCliNet.WsRpcServer.Diagnostics;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Diagnostics;

/// <summary>
/// Guard для task 1.1: app-level інструменти <see cref="AppDiagnostics"/> емітять у Meter
/// "SignalCliNet.WsRpcServer" з тегом <c>result</c>, а набір тег-ключів НЕ виходить за privacy-allowlist
/// (лише <c>result</c> — НІКОЛИ account/номери/тіла/токени). Пінить дзеркало privacy-інваріанта фреймворку.
/// </summary>
[Collection("AppDiagnosticsMeter")]
public sealed class AppDiagnosticsTests
{
    private sealed record Captured(string Instrument, long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);

    private static List<Captured> Capture(Action act)
    {
        var captured = new List<Captured>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AppDiagnostics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (captured)
                captured.Add(new Captured(instrument.Name, value, tags.ToArray()));
        });
        listener.Start();

        act();

        listener.Dispose();
        return captured;
    }

    [Fact]
    public void Sends_TaggedByResult_AllFourVariants()
    {
        var captured = Capture(() =>
        {
            AppDiagnostics.Send(AppDiagnostics.SendResult.Ok);
            AppDiagnostics.Send(AppDiagnostics.SendResult.Denied);
            AppDiagnostics.Send(AppDiagnostics.SendResult.Paused);
            AppDiagnostics.Send(AppDiagnostics.SendResult.Deduped);
        });

        var sends = captured.Where(c => c.Instrument == "wsrpc.app.sends").ToList();
        Assert.Contains(sends, c => HasTag(c, "result", "ok"));
        Assert.Contains(sends, c => HasTag(c, "result", "denied"));
        Assert.Contains(sends, c => HasTag(c, "result", "paused"));
        Assert.Contains(sends, c => HasTag(c, "result", "deduped"));
    }

    [Fact]
    public void Dedup_TaggedByResult_AllThreeVariants()
    {
        var captured = Capture(() =>
        {
            AppDiagnostics.Dedup(AppDiagnostics.DedupResult.HitDone);
            AppDiagnostics.Dedup(AppDiagnostics.DedupResult.HitPending);
            AppDiagnostics.Dedup(AppDiagnostics.DedupResult.Miss);
        });

        var dedup = captured.Where(c => c.Instrument == "wsrpc.app.dedup").ToList();
        Assert.Contains(dedup, c => HasTag(c, "result", "hit_done"));
        Assert.Contains(dedup, c => HasTag(c, "result", "hit_pending"));
        Assert.Contains(dedup, c => HasTag(c, "result", "miss"));
    }

    [Fact]
    public void GlobalPause_RecordsPlusAndMinusOne()
    {
        var captured = Capture(() =>
        {
            AppDiagnostics.GlobalPauseActivated();
            AppDiagnostics.GlobalPauseDeactivated();
        });

        var gauge = captured.Where(c => c.Instrument == "wsrpc.app.global_pause").ToList();
        Assert.Contains(gauge, c => c.Value == 1);
        Assert.Contains(gauge, c => c.Value == -1);
    }

    [Fact]
    public void Heartbeat_IsCounted()
    {
        var captured = Capture(AppDiagnostics.Heartbeat);
        Assert.Contains(captured, c => c.Instrument == "wsrpc.app.heartbeats" && c.Value == 1);
    }

    [Fact]
    public void AllCapturedTagKeys_AreWithinPrivacyAllowlist()
    {
        var captured = Capture(() =>
        {
            AppDiagnostics.Send(AppDiagnostics.SendResult.Ok);
            AppDiagnostics.Send(AppDiagnostics.SendResult.Denied);
            AppDiagnostics.Send(AppDiagnostics.SendResult.Paused);
            AppDiagnostics.Send(AppDiagnostics.SendResult.Deduped);
            AppDiagnostics.Dedup(AppDiagnostics.DedupResult.HitDone);
            AppDiagnostics.Dedup(AppDiagnostics.DedupResult.HitPending);
            AppDiagnostics.Dedup(AppDiagnostics.DedupResult.Miss);
            AppDiagnostics.GlobalPauseActivated();
            AppDiagnostics.GlobalPauseDeactivated();
            AppDiagnostics.Heartbeat();
        });

        var allowed = AppDiagnostics.AllowedTagKeys;
        foreach (var c in captured)
        {
            foreach (var tag in c.Tags)
                Assert.Contains(tag.Key, allowed);
        }
    }

    private static bool HasTag(Captured c, string key, string value) =>
        c.Tags.Any(t => t.Key == key && (string?)t.Value == value);
}

/// <summary>Serialize meter-listener tests that emit to the app meter, so parallel captures don't cross-talk.</summary>
[CollectionDefinition("AppDiagnosticsMeter", DisableParallelization = true)]
public sealed class AppDiagnosticsMeterCollection;
