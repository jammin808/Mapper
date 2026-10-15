using System.Diagnostics;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.Content;

/// <summary>Content through a real render host, end to end: a generator a playing playlist routes draws on its item's time (not its
/// origin), observed in STATE; a sting fired on the wire reaches the frame and STATE says a generator was drawn with it; the draw's GPU
/// time reaches STATE; a node with no renderer accepts a sting without claiming it applied.</summary>
[Collection("render host")]
public sealed class ContentHostTests
{
    private static Project Show()
    {
        var cycle = new Generator("gn_cycle", "Cycle", "pattern.colorcycle", ParamSet.Empty);
        var item = PlaylistItem.For("it_cycle", SourceRef.Generator("gn_cycle"), "Cycle") with { DurationNs = 60_000_000_000, End = EndAction.Hold };
        return HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Playlist("pl_show"), 64, 36)) with
        {
            Generators = [cycle],
            Playlists = [new Playlist("pl_show", "Show", [item], false, null)],
        };
    }

    private static JsonElement Generator(HostRig rig, string id)
    {
        using var doc = JsonDocument.Parse(rig.Engine.State().Json);
        return doc.RootElement.GetProperty("content").GetProperty("generators").EnumerateArray()
            .Single(g => g.GetProperty("id").GetString() == id).Clone();
    }

    private static JsonElement Content(HostRig rig)
    {
        using var doc = JsonDocument.Parse(rig.Engine.State().Json);
        return doc.RootElement.GetProperty("content").Clone();
    }

    /// <summary>Lets the render thread draw until STATE says what is expected.</summary>
    private static async Task Until(HostRig rig, Func<bool> good, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!good())
        {
            Assert.True(waited.Elapsed < HostRig.Patience, what);
            await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        }
    }

    [Fact]
    public async Task ARoutedGeneratorDrawsOnItsItemsTimeAndAStingReachesTheFrame()
    {
        await using var rig = HostRig.Start(Show());
        await rig.Ok("PLAYLIST GO pl_show");
        // 1.5 s into the item; the leader itself is 6.5 s past the generator's origin (0).
        rig.Clock.NowNs += 1_500_000_000;
        await Until(rig, () => Generator(rig, "gn_cycle").GetProperty("state").GetString() == "ok"
            && Generator(rig, "gn_cycle").GetProperty("timeS").GetDouble() >= 1.5, "the routed generator was never drawn at its item's time");
        var drawn = Generator(rig, "gn_cycle");
        Assert.InRange(drawn.GetProperty("timeS").GetDouble(), 1.5, 1.8);
        Assert.True(drawn.GetProperty("onAir").GetBoolean());
        Assert.Equal("drawing", Content(rig).GetProperty("renderer").GetString());

        // The draw's GPU time is measured and published.
        await Until(rig, () => Generator(rig, "gn_cycle").GetProperty("gpuMs").GetDouble() > 0, "no GPU time reached STATE");

        // A sting on the wire: applied once the channel holds it, observed once a frame drew a generator with it.
        var sting = await rig.Run("STING Flash 2000");
        Assert.True(sting.Ok && sting.Applied, sting.Payload);
        await Until(rig, () => Content(rig).GetProperty("sting") is { ValueKind: JsonValueKind.Object } s && s.GetProperty("drawn").GetBoolean(),
            "no frame drew the sting");
        Assert.Equal("Flash", Content(rig).GetProperty("sting").GetProperty("preset").GetString());
    }

    [Fact]
    public async Task ANodeWithNoRendererAcceptsAStingWithoutClaimingIt()
    {
        await using var rig = new ContentRig();
        var r = await rig.Ok("STING Bloom");
        Assert.False(r.Applied);
        Assert.Contains("accepted: no renderer on this node draws it", r.Effect, StringComparison.Ordinal);
        var sting = rig.State().GetProperty("content").GetProperty("sting");
        Assert.False(sting.GetProperty("drawn").GetBoolean());
        Assert.Equal("none", rig.State().GetProperty("content").GetProperty("renderer").GetString());
    }
}
