using PlayMap.Core.Phone;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Tests.Phone;

/// <summary>The tag every pattern carries: each value reads back, every single flipped pair is caught by the parity, a picture whose
/// top site shows one pattern and bottom site the one before is a rolling-shutter mix and refused, and the sites found by the
/// locate run read every pattern's tag in a simulated camera's picture.</summary>
public sealed class TagCodecTests
{
    private const int Lit = 190;
    private const int Dark = 35;

    private static int[] Means(int value, int flipPair = -1)
    {
        var m = new int[16];
        for (var c = 0; c < 16; c++)
            m[c] = FrameTag.Cell(value, c) ? Lit : Dark;
        if (flipPair >= 0)
            (m[2 * flipPair + 1], m[2 * flipPair + 2]) = (m[2 * flipPair + 2], m[2 * flipPair + 1]);
        return m;
    }

    [Fact]
    public void EveryPatternIndexHasANonZeroTagThatReadsBack()
    {
        Assert.Equal(1, FrameTag.Value(0));
        Assert.Equal(63, FrameTag.Value(62));
        Assert.Equal(1, FrameTag.Value(63));
        for (var v = 1; v <= 63; v++)
        {
            var r = FrameTag.Read(Means(v));
            Assert.Equal(TagSiteState.Read, r.State);
            Assert.Equal(v, r.Value);
            Assert.Equal(Lit - Dark, r.Contrast);
            Assert.True(FrameTag.Cell(v, 0));
            Assert.False(FrameTag.Cell(v, 15));
            // A differential pair is always one lit and one dark: a uniform change of brightness never reads as a bit.
            for (var k = 0; k < 7; k++)
                Assert.NotEqual(FrameTag.Cell(v, 2 * k + 1), FrameTag.Cell(v, 2 * k + 2));
        }
    }

    [Fact]
    public void EverySingleFlippedPairIsCaughtByTheParity()
    {
        for (var v = 1; v <= 63; v++)
        {
            for (var k = 0; k < 7; k++)
            {
                var r = FrameTag.Read(Means(v, k));
                Assert.True(r.State == TagSiteState.Unsure, $"value {v} with pair {k} flipped read as {r}");
            }
        }
    }

    [Fact]
    public void ASiteTooDarkOrTooCloseToCallIsNotRead()
    {
        var dim = Means(21).Select(x => x == Lit ? Dark + 15 : Dark).ToArray();
        Assert.Equal(TagSiteState.Dark, FrameTag.Read(dim).State);
        var close = Means(21);
        close[3] = close[4] + 5; // a pair 5 levels apart: below the 6-level floor
        Assert.Equal(TagSiteState.Unsure, FrameTag.Read(close).State);
    }

    [Fact]
    public void LocatedSitesReadEveryPatternsTagAndAMixedPictureIsRefused()
    {
        var kit = TagKit.Floor();
        var located = kit.Locate();
        Assert.True(located.Chosen.Length >= 2, located.Words);
        Assert.True(located.RollingCovered, located.Words);
        Assert.Equal(2, located.MinSites);

        var reads = 0;
        for (var i = 0; i < kit.Plan.Count; i++)
        {
            var frame = kit.Camera(kit.Tagged(i, located), (ulong)(100 + i));
            var r = FrameTag.ReadFrame(frame, kit.Width, kit.Height, located.Chosen, located.MinSites);
            Assert.True(r.Outcome == FrameTagOutcome.Read, $"pattern {i} ({kit.Plan.Frames[i].Name}): {r}");
            Assert.Equal(FrameTag.Value(i), r.Value);
            reads++;
        }
        Assert.Equal(kit.Plan.Count, reads);

        // A rolling shutter caught mid-change: the rows above the middle show pattern 9, those below still pattern 8.
        var now = kit.Camera(kit.Tagged(9, located), 7);
        var before = kit.Camera(kit.Tagged(8, located), 8);
        var top = located.Chosen.MinBy(s => s.RowTop);
        var bottom = located.Chosen.MaxBy(s => s.RowBottom);
        var split = (top.RowBottom + bottom.RowTop) / 2;
        var mixed = (byte[])now.Clone();
        Array.Copy(before, split * kit.Width, mixed, split * kit.Width, mixed.Length - split * kit.Width);
        var read = FrameTag.ReadFrame(mixed, kit.Width, kit.Height, located.Chosen, located.MinSites);
        Assert.Equal(FrameTagOutcome.Disagree, read.Outcome);
    }
}

/// <summary>A projector and a camera over the floor-and-box scene: the plan, the projector-raster pictures (patterns with tags,
/// locate frames) and the camera's pictures of them.</summary>
internal sealed class TagKit
{
    private TagKit(SimScene scene, SimOptions? options)
    {
        Plan = ScanPlan.Create(scene.Projector.Intrinsics.Width, scene.Projector.Intrinsics.Height, new ScanPlanOptions { Mode = ScanMode.Robust });
        Simulator = new ScanSimulator(new SimGeometry(scene), Plan, options);
        Candidates = CellLocate.Candidates(Plan.ProjectorWidth, Plan.ProjectorHeight);
    }

    public static TagKit Floor(SimOptions? options = null) => new(SimScenes.FloorAndBox(), options);

    public ScanPlan Plan { get; }

    public ScanSimulator Simulator { get; }

    public IReadOnlyList<TagSite> Candidates { get; }

    public int Width => Simulator.CameraWidth;

    public int Height => Simulator.CameraHeight;

    public byte[] Camera(byte[] raster, ulong seed, double gain = 1) => Simulator.RenderRaster(raster, seed, gain);

    public byte[] LocateRaster(int j, byte background = 0)
    {
        var r = new byte[Plan.ProjectorWidth * Plan.ProjectorHeight];
        CellLocate.RenderFrame(j, r, Plan.ProjectorWidth, Plan.ProjectorHeight, Candidates, background);
        return r;
    }

    public CellLocateResult Locate(byte background = 0) =>
        CellLocate.Decode(Width, Height, [.. Enumerable.Range(0, CellLocate.Frames).Select(j => Camera(LocateRaster(j, background), (ulong)(500 + j)))],
            Candidates);

    public byte[] Tagged(int index, CellLocateResult located)
    {
        var r = Plan.Render(index);
        TagOverlay.Apply(r, Plan.ProjectorWidth, located.Sites, FrameTag.Value(index), Plan.Options.High, Plan.Options.Low);
        return r;
    }
}
