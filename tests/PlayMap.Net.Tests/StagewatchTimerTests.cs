using System.Text.Json;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Links;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Tests;

/// <summary>STATE's <c>links.stagewatch.timer</c> as the reading writes it: its keys in <see cref="StagewatchReading.StateKeys"/>'
/// order and bytes, the blackout and the watermark tenth and eleventh, a key added later after them; the blackout and the watermark
/// of the last frame held while the time is not a fact (a stuck show, a link lost), and only Stagewatch's live word releasing the
/// blackout (Show Bus 1.3 §13.17).</summary>
public class StagewatchTimerTests
{
    private const string Mark = "StageWatch by Ben Rohleder";

    /// <summary>Stagewatch's own frame, in the order its frame writer puts these keys (blackout among the message's, the watermark
    /// after the aux timers).</summary>
    private static string Frame(long seq, bool blackout, string watermark = Mark) =>
        "{\"type\":\"state\",\"v\":1,\"seq\":" + seq + ",\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":750,\"text\":\"12:30\","
        + "\"total\":1800,\"phase\":\"warning\",\"color\":\"#F59E0B\",\"title\":\"Keynote\",\"next\":\"Panel\",\"message\":\"Wrap up\","
        + "\"urgency\":\"warning\",\"flash\":\"slow\",\"flashTimer\":false,\"blackout\":" + (blackout ? "true" : "false")
        + ",\"holding\":\"\",\"holdingName\":\"\",\"holdingFill\":false,\"aux\":[],\"watermark\":\"" + watermark + "\","
        + "\"show\":{\"timer\":true,\"message\":true}}";

    private static StagewatchTimer Parsed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return StagewatchTimer.Parse(doc.RootElement)!;
    }

    private static List<string> Keys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    [Fact]
    public void TheTimerObjectsKeysAreFixedInOrderAndBytesAndEndWithTheBlackoutAndTheWatermark()
    {
        var t = Parsed(Frame(5, blackout: false));
        var json = new StagewatchReading(t.CountedOn(0), true, t.Text, "").ToStateJson();

        // The keys are fixed in StateKeys' order and these bytes: the time's nine first, the blackout and the watermark after them.
        Assert.Equal(
            "{\"state\":\"running\",\"text\":\"12:30\",\"seconds\":750,\"phase\":\"warning\",\"color\":\"#F59E0B\",\"title\":\"Keynote\","
            + "\"message\":\"Wrap up\",\"trusted\":true,\"why\":\"\",\"blackout\":false,\"watermark\":\"StageWatch by Ben Rohleder\"}",
            json);
        Assert.Equal(StagewatchReading.StateKeys, Keys(json));
        Assert.Equal(["blackout", "watermark"], StagewatchReading.StateKeys.TakeLast(2));
        Assert.Equal(9, StagewatchReading.StateKeys.IndexOf("blackout"));

        // A blackout Stagewatch says while live, with the mark gone (a licensed Stagewatch sends none).
        var dark = Parsed(Frame(6, blackout: true, watermark: ""));
        using var doc = JsonDocument.Parse(new StagewatchReading(dark, true, dark.Text, "").ToStateJson());
        Assert.True(doc.RootElement.GetProperty("blackout").GetBoolean());
        Assert.Equal("", doc.RootElement.GetProperty("watermark").GetString());
        Assert.Equal("12:30", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void ABlackoutAndTheWatermarkAreHeldWhileTheTimeIsNotAFactAndNoneIsSaidBeforeTheFirstFrame()
    {
        var dark = Parsed(Frame(5, blackout: true));
        foreach (var why in new[] { "Stagewatch has gone quiet", "Stagewatch is not answering", "Stagewatch says its show is stuck — the time is not shown" })
        {
            var held = new StagewatchReading(dark, false, "?", why);
            Assert.True(held.Blackout);
            Assert.Equal(Mark, held.Watermark);
            using var doc = JsonDocument.Parse(held.ToStateJson());
            var r = doc.RootElement;
            Assert.Equal(("unknown", "?", JsonValueKind.Null, false, why), (r.GetProperty("state").GetString(), r.GetProperty("text").GetString(),
                r.GetProperty("seconds").ValueKind, r.GetProperty("trusted").GetBoolean(), r.GetProperty("why").GetString()));
            Assert.True(r.GetProperty("blackout").GetBoolean());
            Assert.Equal(Mark, r.GetProperty("watermark").GetString());
        }

        var unknown = StagewatchReading.Unknown;
        Assert.Equal((false, ""), (unknown.Blackout, unknown.Watermark));
        using var none = JsonDocument.Parse(unknown.ToStateJson());
        Assert.Equal(StagewatchReading.StateKeys, Keys(unknown.ToStateJson()));
        Assert.False(none.RootElement.GetProperty("blackout").GetBoolean());
        Assert.Equal("", none.RootElement.GetProperty("watermark").GetString());
        Assert.Equal(JsonValueKind.Null, none.RootElement.GetProperty("seconds").ValueKind);
    }

    /// <summary>The link's own reading: a blackout Stagewatch says is followed, held through its show going stuck, released by its
    /// next live frame, held again through the link going down — the watermark kept all the while.</summary>
    [Fact]
    public async Task TheLinksReadingHoldsStagewatchsBlackoutThroughAStuckShowAndALostLinkUntilItsLiveWord()
    {
        await using var sw = await FakeStagewatch.StartAsync();
        await using var link = new StagewatchLink(new StagewatchLinkOptions
        {
            Host = "127.0.0.1",
            Port = sw.Port,
            Key = "swk_test0123456789",
            SelfId = "a1b2c3d4",
            SelfName = "Stage left",
            BeatEvery = TimeSpan.FromMilliseconds(200),
            ContextEvery = TimeSpan.FromMilliseconds(200),
            ApprovalsEvery = TimeSpan.FromMilliseconds(200),
        }, new FakeEndpoint(), TestSettings.Wall(), new PinStore(), new ManualClock(1_000_000_000));
        link.Start();
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live), link.Words);

        await sw.SendAsync(Frame(5, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => link.Reading is { Trusted: true, Blackout: true }), link.Words);
        Assert.Equal(Mark, link.Reading.Watermark);

        await sw.SendAsync("{\"type\":\"beat\",\"live\":false}");
        Assert.True(await Eventually.TrueAsync(() => link.Stuck));
        var stuck = link.Reading;
        Assert.Equal((false, true, Mark), (stuck.Trusted, stuck.Blackout, stuck.Watermark));
        Assert.Contains("\"blackout\":true,\"watermark\":\"StageWatch by Ben Rohleder\"}", stuck.ToStateJson(), StringComparison.Ordinal);

        await sw.SendAsync(Frame(6, blackout: false));
        Assert.True(await Eventually.TrueAsync(() => link.Reading is { Trusted: true, Blackout: false }), link.Words);
        Assert.Equal(Mark, link.Reading.Watermark);

        await sw.SendAsync(Frame(7, blackout: true));
        Assert.True(await Eventually.TrueAsync(() => link.Reading is { Trusted: true, Blackout: true }), link.Words);
        sw.RefuseSockets = true;
        sw.Drop();
        Assert.True(await Eventually.TrueAsync(() => link.Status != LinkStatus.Live), link.Words);
        var lost = link.Reading;
        Assert.Equal((false, "?", true, Mark), (lost.Trusted, lost.Text, lost.Blackout, lost.Watermark));
    }
}
