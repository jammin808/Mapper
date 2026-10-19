using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Render.Effects;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Effects;

/// <summary>The ten built-in effects, each drawn at 64×36 over a picture made for it and read back at chosen pixels: the codes
/// each one promises, the right way up.</summary>
[Collection("gl")]
public sealed class FxLibraryTests(GlFixture gl)
{
    private const int W = 64, H = 36;
    private static readonly Project Show = Project.CreateDefault("Effects");

    private byte[] Draw(string kind, ParamSet ps, Func<int, int, (int, int, int)> input)
    {
        return gl.Run(c =>
        {
            var d = c.Device;
            using var lane = new FxCompileLane(c);
            using var runner = new FxChainRunner(d, lane);
            using var picture = FxRig.Picture(d, W, H, input);
            long frame = 0;
            var px = FxRig.Run(d, lane, runner, Show, FxRig.Chain(FxRig.Slot(kind, ps)), picture, ref frame);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(runner, "fx00000001").State);
            Assert.Empty(d.TakeErrors());
            return px;
        });
    }

    private static (int R, int G, int B) At(byte[] px, int x, int y) => Pixels.At(px, W, x, y);

    [Fact]
    public void TheLibraryHasTenKindsThatEachReadAsIsf()
    {
        var kinds = FxCatalog.Builtin.Kinds;
        Assert.Equal(["fx.invert", "fx.levels", "fx.hsl", "fx.threshold", "fx.blur", "fx.vignette", "fx.pixelate", "fx.mirror", "fx.tile", "fx.feedback"],
            kinds.Select(k => k.Id).ToArray());
        Assert.All(kinds, k => Assert.True(k.Document.Valid && k.Document.Issues.IsEmpty && k.Role == IsfRole.Filter, k.Id));
        Assert.Equal(["fx.feedback"], kinds.Where(k => k.Traits.HistoryDependent).Select(k => k.Id).ToArray());
        Assert.All(kinds, k => Assert.Equal("PlayMap", k.Document.Credit));
    }

    [Fact]
    public void InvertTurnsEachCodeIntoItsComplement()
    {
        var px = Draw("fx.invert", ParamSet.Empty, (_, _) => (40, 100, 200));
        Pixels.Near((215, 155, 55), At(px, 10, 10), 1);
    }

    [Fact]
    public void LevelsStretchesBetweenItsBlackAndWhitePoints()
    {
        var px = Draw("fx.levels", ParamSet.Empty.With("inBlack", 0.2).With("inWhite", 0.8),
            (x, _) => x < 21 ? (25, 25, 25) : x < 42 ? (128, 128, 128) : (230, 230, 230));
        Pixels.Near((0, 0, 0), At(px, 5, 18), 1);
        Pixels.Near((128, 128, 128), At(px, 31, 18), 1);
        Pixels.Near((255, 255, 255), At(px, 60, 18), 1);
    }

    [Fact]
    public void HslTurnsTheHueAndTakesTheSaturation()
    {
        var turned = Draw("fx.hsl", ParamSet.Empty.With("hue", 1.0 / 3), (_, _) => (255, 0, 0));
        Pixels.Near((0, 255, 0), At(turned, 20, 20), 2);
        var grey = Draw("fx.hsl", ParamSet.Empty.With("saturation", 0.0), (_, _) => (200, 40, 40));
        var (r, g, b) = At(grey, 20, 20);
        Assert.True(r == g && g == b, $"no saturation is grey, got {(r, g, b)}");
    }

    [Fact]
    public void ThresholdIsBlackBelowItsLevelAndWhiteAbove()
    {
        var px = Draw("fx.threshold", ParamSet.Empty.With("level", 0.5), (x, _) => x < 32 ? (50, 50, 50) : (200, 200, 200));
        Assert.Equal((0, 0, 0), At(px, 10, 10));
        Assert.Equal((255, 255, 255), At(px, 50, 10));
    }

    [Fact]
    public void BlurSoftensAnEdgeAndKeepsAFlatField()
    {
        var px = Draw("fx.blur", ParamSet.Empty.With("radius", 8.0), (x, _) => x < 32 ? (0, 0, 0) : (255, 255, 255));
        Assert.Equal((0, 0, 0), At(px, 2, 18));
        Assert.Equal((255, 255, 255), At(px, 61, 18));
        var (edge, _, _) = At(px, 31, 18);
        Assert.InRange(edge, 40, 215);
        Assert.Equal(At(px, 31, 0), At(px, 31, 35));
    }

    [Fact]
    public void VignetteDarkensTheCornersAndLeavesTheCentre()
    {
        var px = Draw("fx.vignette", ParamSet.Empty.With("amount", 1.0).With("size", 0.2).With("softness", 0.2), (_, _) => (255, 255, 255));
        Pixels.Near((255, 255, 255), At(px, 32, 18), 1);
        Assert.True(At(px, 0, 0).R < 128 && At(px, 63, 35).R < 128, $"corners stay bright: {At(px, 0, 0)} {At(px, 63, 35)}");
    }

    [Fact]
    public void PixelateGivesEachCellOneColour()
    {
        var px = Draw("fx.pixelate", ParamSet.Empty.With("size", 8.0), (x, _) => (x * 4, 0, 0));
        Assert.Equal(At(px, 0, 0), At(px, 7, 7));
        Assert.Equal(At(px, 8, 0), At(px, 15, 7));
        Assert.NotEqual(At(px, 7, 0), At(px, 8, 0));
    }

    [Fact]
    public void MirrorFoldsLeftToRightAndTopToBottomTheRightWayUp()
    {
        var across = Draw("fx.mirror", ParamSet.Empty, (x, _) => (x * 4, 0, 0));
        Pixels.Near(At(across, 10, 5), At(across, 53, 5), 1);
        Pixels.Near((40, 0, 0), At(across, 10, 5), 1);
        // The top half is red: folded top to bottom, the whole picture is red (the wrong way up it would be blue).
        var down = Draw("fx.mirror", ParamSet.Empty.With("mode", "Top to bottom"), (_, y) => y < 18 ? (255, 0, 0) : (0, 0, 255));
        Assert.Equal((255, 0, 0), At(down, 30, 2));
        Assert.Equal((255, 0, 0), At(down, 30, 33));
    }

    [Fact]
    public void TileRepeatsThePicture()
    {
        var px = Draw("fx.tile", ParamSet.Empty.With("across", 2.0).With("down", 1.0), (x, _) => x < 32 ? (255, 0, 0) : (0, 0, 255));
        Assert.Equal((255, 0, 0), At(px, 8, 18));
        Assert.Equal((0, 0, 255), At(px, 24, 18));
        Assert.Equal((255, 0, 0), At(px, 40, 18));
        Assert.Equal((0, 0, 255), At(px, 56, 18));
    }

    [Fact]
    public void FeedbackKeepsTheLastFrameFadedByItsDecay()
    {
        gl.Run(c =>
        {
            var d = c.Device;
            using var lane = new FxCompileLane(c);
            using var runner = new FxChainRunner(d, lane);
            using var white = FxRig.Picture(d, W, H, (_, _) => (255, 255, 255));
            using var black = FxRig.Picture(d, W, H, (_, _) => (0, 0, 0));
            var chain = FxRig.Chain(FxRig.Slot("fx.feedback", ParamSet.Empty.With("decay", 0.5)));
            long frame = 0;
            Pixels.Near((255, 255, 255), Pixels.At(FxRig.Run(d, lane, runner, Show, chain, white, ref frame), W, 32, 18), 1);
            var faded = Scene.ReadTexture(d, FxRig.Step(d, lane, runner, Show, chain, black, ref frame, 1_016_666_667));
            Pixels.Near((128, 128, 128), Pixels.At(faded, W, 32, 18), 1);
            var twice = Scene.ReadTexture(d, FxRig.Step(d, lane, runner, Show, chain, black, ref frame, 1_033_333_333));
            Pixels.Near((64, 64, 64), Pixels.At(twice, W, 32, 18), 1);
            Assert.True(FxRig.Live(runner, "fx00000001").History);
        });
    }
}
