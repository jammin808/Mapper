using PlayMap.Core.Align;
using PlayMap.Core.Mapping;

namespace PlayMap.Core.Tests.Align;

/// <summary>Each output's footprint in the camera, from every output black and that output white alone, against the simulator's truth
/// (the camera pixels whose screen point the output's raster reaches): every pixel the two disagree on lies within 2 px of the true
/// edge, and the bounds and centroid agree.</summary>
public sealed class FootprintTests
{
    [Fact]
    public void EachOutputsFootprintIsWithinTwoPixelsOfTheTruth()
    {
        var sim = MultiProjectorSim.Tiles(3, 1);
        var w = sim.CameraWidth;
        var h = sim.CameraHeight;
        var black = sim.Capture(new byte[]?[3]);
        for (var j = 0; j < 3; j++)
        {
            var shown = new byte[]?[3];
            var k = sim.Projectors[j].Intrinsics;
            shown[j] = AlignSequence.Solid(k.Width, k.Height, 255);
            var footprint = Footprints.Find(black, sim.Capture(shown), w, h);

            var truth = new bool[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    truth[y * w + x] = sim.CameraToProjector(j, x, y) is not null;
            var toTruth = DistanceTransform.Euclidean(truth, w, h);
            var outside = new bool[truth.Length];
            for (var i = 0; i < truth.Length; i++)
                outside[i] = !truth[i];
            var toOutside = DistanceTransform.Euclidean(outside, w, h);
            var worst = 0.0;
            var disagree = 0;
            for (var i = 0; i < truth.Length; i++)
            {
                if (footprint.Mask[i] == truth[i])
                    continue;
                disagree++;
                worst = Math.Max(worst, footprint.Mask[i] ? toTruth[i] : toOutside[i]);
            }
            Assert.True(worst <= 2, $"P{j + 1}: a pixel {worst:0.0} px from the true edge disagrees");
            var t = Footprints.Describe(truth, w, h, 0);
            Assert.True(disagree < 0.02 * t.Area, $"P{j + 1}: {disagree} pixels disagree of {t.Area}");
            Assert.InRange(footprint.Bounds.X - t.Bounds.X, -2, 2);
            Assert.InRange(footprint.Bounds.Y - t.Bounds.Y, -2, 2);
            Assert.InRange(footprint.Bounds.Right - t.Bounds.Right, -2, 2);
            Assert.InRange(footprint.Bounds.Bottom - t.Bounds.Bottom, -2, 2);
            Assert.True((footprint.Centroid - t.Centroid).Length < 1, $"P{j + 1}: centroid {footprint.Centroid} vs {t.Centroid}");
        }
    }

    [Fact]
    public void AStacksMembersShareTheirFootprintAndNeighboursOverlapByTheirOverlap()
    {
        var stack = MultiProjectorSim.Stack(2);
        var blackS = stack.Capture(new byte[]?[2]);
        var a = Footprints.Find(blackS, stack.Capture([AlignSequence.Solid(320, 180, 255), null]), stack.CameraWidth, stack.CameraHeight);
        var b = Footprints.Find(blackS, stack.Capture([null, AlignSequence.Solid(320, 180, 255)]), stack.CameraWidth, stack.CameraHeight);
        Assert.True(a.Overlap(b) >= 0.95, $"stack overlap {a.Overlap(b):0.000}");

        var tiles = MultiProjectorSim.Tiles(2, 1);
        var blackT = tiles.Capture(new byte[]?[2]);
        var l = Footprints.Find(blackT, tiles.Capture([AlignSequence.Solid(320, 180, 255), null]), tiles.CameraWidth, tiles.CameraHeight);
        var r = Footprints.Find(blackT, tiles.Capture([null, AlignSequence.Solid(320, 180, 255)]), tiles.CameraWidth, tiles.CameraHeight);
        Assert.InRange(l.Overlap(r), 0.1, 0.35);
    }

    [Fact]
    public void OtsuSplitsTwoLevels()
    {
        var histogram = new int[256];
        histogram[10] = 1000;
        histogram[12] = 400;
        histogram[150] = 600;
        histogram[160] = 300;
        var t = Footprints.Otsu(histogram);
        Assert.InRange(t, 12, 149);
    }
}
