using PlayMap.Core.Model;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>Panic on the air, read back from the render host on a hidden context (Mesa is enough): a white output under PANIC FADE
/// 1000 reads 255, ≈191, ≈128 and 0 at the fade's start, a quarter, half way and its end on the show clock (the level is linear in
/// the output's signal, multiplied last so 0 is true black); SAFE dips to black and shows the planned look; CLEAR brings the show
/// back.</summary>
[Collection("render host")]
public sealed class PanicRenderTests(ITestOutputHelper log)
{
    private static Project White() => HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#FFFFFF"), 64, 48));

    private static async Task<(int R, int G, int B)> CentreAt(HostRig rig, long leaderNs, Func<(int R, int G, int B), bool> good)
    {
        rig.Clock.NowNs = leaderNs;
        var px = await rig.ReadUntil("op_1", p => good(HostRig.At(p, 64, 32, 24)));
        return HostRig.At(px, 64, 32, 24);
    }

    private static bool Near(int value, int want) => Math.Abs(value - want) <= 2;

    [Fact]
    public async Task AWhiteOutputFadesToBlackOnTheShowClock()
    {
        await using var rig = HostRig.Start(White());
        var t = rig.Engine.LeaderNowNs;
        Assert.Equal((255, 255, 255), await CentreAt(rig, t, p => p.R == 255));

        await rig.Ok("PANIC FADE 1000");
        foreach (var (ms, want) in new[] { (0, 255), (250, 191), (500, 128), (1000, 0) })
        {
            var p = await CentreAt(rig, t + ms * 1_000_000L, px => Near(px.R, want));
            log.WriteLine($"+{ms} ms: {p}");
            Assert.True(Near(p.R, want) && Near(p.G, want) && Near(p.B, want), $"+{ms} ms read {p}, wanted {want} ± 2");
        }

        // CLEAR brings the show back over its fade, on the same clock.
        var c = t + 1_000_000_000;
        await rig.Ok("PANIC CLEAR FADE 1000");
        Assert.True(Near((await CentreAt(rig, c + 500_000_000, px => Near(px.R, 128))).R, 128));
        Assert.Equal((255, 255, 255), await CentreAt(rig, c + 1_000_000_000, px => px.R == 255));
    }

    [Fact]
    public async Task SafeShowsThePlannedLookAfterTheDip()
    {
        await using var rig = HostRig.Start(White());
        await rig.Ok("PANIC SET op_1 SAFE FADE 1000 SAFE color:#FF0000");
        var t = rig.Engine.LeaderNowNs;
        await rig.Ok("PANIC");
        // Half way down the dip: the show at half level.
        var dip = await CentreAt(rig, t + 250_000_000, px => Near(px.R, 128));
        Assert.True(Near(dip.G, 128) && Near(dip.B, 128), $"the dip read {dip}");
        // At the bottom: black; then the safe look comes up.
        Assert.Equal((0, 0, 0), await CentreAt(rig, t + 500_000_000, px => px.R == 0));
        var up = await CentreAt(rig, t + 750_000_000, px => Near(px.R, 128) && px.G <= 2);
        Assert.True(Near(up.R, 128) && up.G <= 2 && up.B <= 2, $"the safe look coming up read {up}");
        Assert.Equal((255, 0, 0), await CentreAt(rig, t + 1_000_000_000, px => px == (255, 0, 0)));
    }
}
