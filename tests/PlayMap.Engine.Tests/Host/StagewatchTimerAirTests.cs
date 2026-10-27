using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlayMap.Core.Time;
using PlayMap.Engine.Rendering;
using PlayMap.Net.Links;

namespace PlayMap.Engine.Tests.Host;

/// <summary>A node following a Stagewatch: STATE's <c>links.stagewatch.timer</c> carries Stagewatch's blackout and watermark after its
/// first nine keys, and the timer surface reads them from the very object the network edge hands it — blackout and watermark held
/// while the time is not a fact (Stagewatch's show stuck, the link lost), the hold said with its age on the link's detail (Show Bus 1.3
/// §13.17), the blackout released by Stagewatch's next live frame or by a person, the watermark kept throughout.</summary>
public sealed class StagewatchTimerAirTests
{
    private const string Mark = "StageWatch by Ben Rohleder";
    private const long Base = 1_000_000_000_000;

    private static string Frame(long seq, bool blackout) =>
        "{\"type\":\"state\",\"v\":1,\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"mode\":\"countdown\",\"state\":\"running\","
        + "\"seconds\":750,\"text\":\"12:30\",\"total\":1800,\"phase\":\"warning\",\"color\":\"#F59E0B\",\"title\":\"Keynote\","
        + "\"message\":\"Wrap up\",\"blackout\":" + (blackout ? "true" : "false") + ",\"watermark\":\"" + Mark + "\"}";

    private static JsonElement Link(HostRig rig) => rig.State().GetProperty("links").GetProperty("stagewatch");

    private static JsonElement? Timer(HostRig rig) => Link(rig).TryGetProperty("timer", out var t) ? t : null;

    private static string Detail(HostRig rig) => Link(rig).TryGetProperty("detail", out var d) ? d.GetString()! : "";

    /// <summary>The hold's words at the start of the link's detail: when it began and how long it has run (s), or null for none.</summary>
    private static (string Since, int Seconds)? Hold(HostRig rig, string why)
    {
        var m = Regex.Match(Detail(rig), @"^holding Stagewatch's blackout \(its timer drawn black\) since (\d\d:\d\d:\d\d) \((\d+) s\) — "
            + Regex.Escape(why) + "; Stagewatch's next frame releases it, or LINK STAGEWATCH OFF");
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    [Fact]
    public async Task StatesTimerCarriesStagewatchsBlackoutAndWatermarkAndTheSurfaceDrawsFromIt()
    {
        // The node's clock is the test's: the time counts on, the link's silence grows and the hold ages only when the test moves it.
        var clock = new ManualClock(Base);
        await using var sw = await ScreensStagewatch.StartAsync();
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        await using var rig = await HostRig.StartAsync("Stage left", mono: clock,
            settings: s => s with { StagewatchEnabled = true, StagewatchHost = address });
        Assert.True(await Eventually.TrueAsync(() => rig.Network.Stagewatch()?.State == "live"), rig.LogTail());

        await sw.SendAsync(Frame(5, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => Timer(rig) is { } t && t.GetProperty("trusted").GetBoolean() && t.GetProperty("blackout").GetBoolean()),
            rig.LogTail());
        var timer = Timer(rig)!.Value;
        Assert.Equal(StagewatchReading.StateKeys, timer.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("12:30", Mark), (timer.GetProperty("text").GetString(), timer.GetProperty("watermark").GetString()));
        var face = TimerView.Read(rig.Network.StagewatchTimerJson());
        Assert.Equal((true, Mark, "12:30"), (face.Blackout, face.Watermark, face.Text));
        // Stagewatch's live word stands behind it: no hold.
        Assert.DoesNotContain("holding", Detail(rig), StringComparison.Ordinal);

        // Stagewatch says its show is stuck: the time is not a fact, and the blackout and the mark are held, never let go by it; the
        // link's detail says the hold and its age, which runs on from its start.
        await sw.SendAsync("{\"type\":\"beat\",\"live\":false}");
        Assert.True(await Eventually.TrueAsync(() => Timer(rig) is { } t && !t.GetProperty("trusted").GetBoolean()), rig.LogTail());
        timer = Timer(rig)!.Value;
        Assert.Equal(("unknown", "?", true, Mark), (timer.GetProperty("state").GetString(), timer.GetProperty("text").GetString(),
            timer.GetProperty("blackout").GetBoolean(), timer.GetProperty("watermark").GetString()));
        face = TimerView.Read(rig.Network.StagewatchTimerJson());
        Assert.Equal((true, Mark), (face.Blackout, face.Watermark));
        Assert.True(await Eventually.TrueAsync(() => Hold(rig, "Stagewatch says its show is stuck") is not null), Detail(rig));
        var stuck = Hold(rig, "Stagewatch says its show is stuck")!.Value;
        clock.AdvanceMs(2_000);
        Assert.True(await Eventually.TrueAsync(() => Hold(rig, "Stagewatch says its show is stuck") is { Seconds: >= 2 }), Detail(rig));
        Assert.Equal(stuck.Since, Hold(rig, "Stagewatch says its show is stuck")!.Value.Since);
        Assert.Equal("live", Link(rig).GetProperty("state").GetString());

        // Its next live frame is its word: the blackout lifts, the mark stays, nothing is held.
        await sw.SendAsync(Frame(6, blackout: false));
        Assert.True(await Eventually.TrueAsync(() => Timer(rig) is { } t && t.GetProperty("trusted").GetBoolean() && !t.GetProperty("blackout").GetBoolean()),
            rig.LogTail());
        face = TimerView.Read(rig.Network.StagewatchTimerJson());
        Assert.Equal((false, Mark), (face.Blackout, face.Watermark));
        Assert.DoesNotContain("holding", Detail(rig), StringComparison.Ordinal);

        // Blacked out again, then the link is lost: the blackout is held, said so, and the surface stays black under its mark.
        await sw.SendAsync(Frame(7, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => Timer(rig) is { } t && t.GetProperty("trusted").GetBoolean() && t.GetProperty("blackout").GetBoolean()),
            rig.LogTail());
        // The connection goes, and with it the Stagewatch (its port refuses every dial after).
        sw.Drop();
        await sw.DisposeAsync();
        Assert.True(await Eventually.TrueAsync(() => Hold(rig, "link lost") is not null), Detail(rig));
        Assert.NotEqual("live", Link(rig).GetProperty("state").GetString());
        face = TimerView.Read(rig.Network.StagewatchTimerJson());
        Assert.Equal((true, Mark, "?"), (face.Blackout, face.Watermark, face.Text));

        // A person lets it go: followed no longer, nothing is held and the surface has no blackout to draw.
        await rig.OkAsync("LINK STAGEWATCH OFF");
        Assert.True(await Eventually.TrueAsync(() => Link(rig).GetProperty("state").GetString() == "off"), Link(rig).ToString());
        Assert.Null(rig.Network.StagewatchTimerJson());
        Assert.False(TimerView.Read(rig.Network.StagewatchTimerJson()).Blackout);
    }
}
