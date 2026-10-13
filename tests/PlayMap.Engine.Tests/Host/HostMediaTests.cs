using System.Text.Json;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Tests.Host;

/// <summary>Media on a real node: a clip imported through the wire is hashed and probed by PlayMap.Media, STATE and
/// MEDIA LIST say it is ready, the node's hash index answers for it and the web port serves it to peers; FFmpeg's
/// absence is said in words.</summary>
public sealed class HostMediaTests
{
    [FfmpegFact]
    public async Task AnImportedClipIsHashedProbedAndReady()
    {
        await using var rig = await HostRig.StartAsync();
        var clip = await FfmpegFactAttribute.MakeClipAsync(rig.Dir.FullName);

        var reply = await rig.RunAsync("MEDIA IMPORT " + Core.Wire.WireGrammar.Quote(clip) + " Clip");
        Assert.True(reply.Ok, reply.Payload);
        string id;
        using (var accepted = JsonDocument.Parse(reply.Payload!))
        {
            Assert.Equal("accepted", accepted.RootElement.GetProperty("stage").GetString());
            id = accepted.RootElement.GetProperty("id").GetString()!;
        }

        async Task<JsonElement> RowAsync()
        {
            using var list = JsonDocument.Parse(await rig.OkAsync("MEDIA LIST"));
            return list.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id).Clone();
        }

        Assert.True(await Eventually.TrueAsync(async () => (await RowAsync()).GetProperty("state").GetString() == "ready", 20000), rig.Journal());
        var row = await RowAsync();
        Assert.Equal(1, row.GetProperty("progress").GetDouble());
        Assert.Equal("video", row.GetProperty("kind").GetString());
        Assert.InRange(row.GetProperty("durationMs").GetInt64(), 1900, 2100);
        Assert.Equal(320, row.GetProperty("width").GetInt32());
        Assert.Equal("ready", row.GetProperty("words").GetString());
        var hash = row.GetProperty("hash").GetString()!;
        Assert.StartsWith("sha256:", hash, StringComparison.Ordinal);

        var media = rig.State().GetProperty("media");
        Assert.Equal(1, media.GetProperty("items").GetInt32());
        Assert.Equal(0, media.GetProperty("missing").GetInt32());
        Assert.True(media.GetProperty("ready").GetBoolean());
        Assert.Equal("yes", await rig.OkAsync("MEDIA HAS " + hash));
        using var http = rig.Http();
        Assert.Contains(hash["sha256:".Length..], await http.GetStringAsync("/api/media"), StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegsAbsenceIsSaidWithEveryFolderSearched()
    {
        var words = MediaBackend.FfmpegWords("FFmpeg 63.x shared libraries (avcodec 63, avformat 63, avutil 63) were not found. Searched: /x: no such folder");
        Assert.StartsWith("FFmpeg 9 libraries not found: searched ", words, StringComparison.Ordinal);
        Assert.Contains("PLAYMAP_FFMPEG_DIR", words, StringComparison.Ordinal);
        Assert.StartsWith("FFmpeg 9 libraries could not be used: ", MediaBackend.FfmpegWords("libavcodec.so.63 exists but did not load"), StringComparison.Ordinal);
    }
}

/// <summary>Whether the node's show thread turns over (what a WebSocket screen hears as live): a control lane held
/// for longer than two seconds reads as stuck, and as live again once it runs.</summary>
public sealed class EngineActivityTests
{
    [Fact]
    public async Task AStuckControlLaneReadsAsNotLiveAndALaneThatRunsAgainAsLive()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var engine = new EngineCore(EngineServices.ForTests(clock));
        Assert.True(engine.IsLive);
        await engine.Lane.RunAsync(() => { });   // the first probe has come back

        using var held = new ManualResetEventSlim(false);
        engine.Lane.Post(() => held.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(engine.IsLive);              // a probe goes out behind the held work
        clock.AdvanceMs(1500);
        Assert.True(engine.IsLive);              // out, but younger than two seconds
        clock.AdvanceMs(1000);
        Assert.False(engine.IsLive);             // out for two and a half: stuck

        held.Set();
        await engine.Lane.RunAsync(() => { });   // everything before this has run, the probe included
        Assert.True(engine.IsLive);
    }
}
