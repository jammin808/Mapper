using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Scan.MultiView;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan.MultiView;

/// <summary>A view gone bad and dropped: the phone moved 2 px between its two projectors' runs at v2 (pj2's
/// observations there are shifted), which no pose of v2 explains; the solve's residual rises, each view's influence
/// names v2, and dropping v2 — not either good view — brings the residuals back under half a pixel.</summary>
public sealed class DropViewTests(ITestOutputHelper output)
{
    private static readonly Vec2d Shift = new(2, 0);

    private static MultiViewInput Shifted()
    {
        var input = MultiViewKit.Input;
        var tracks = new TrackSet([.. input.Tracks.Tracks.Select(t => t.ProjectorId != "pj2" ? t : t with
        {
            Seen = [.. t.Seen.Select(o => o.ViewId == "v2" ? o with { Camera = o.Camera + Shift } : o)],
        })]);
        var views = input.Views.Select(v => v.Id != "v2" ? v : v with
        {
            Pairs = v.Pairs.SetItem("pj2", [.. v.Pairs["pj2"].Select(p => p with { Camera = p.Camera + Shift })]),
        }).ToImmutableArray();
        return input with { Tracks = tracks, Views = views };
    }

    [Fact]
    public void AShiftedViewRaisesTheResidualAndDroppingItBringsItUnderHalfAPixel()
    {
        var inv = CultureInfo.InvariantCulture;
        var clean = MultiViewKit.Solved;
        var input = Shifted();
        var bad = MultiViewCalibration.Solve(input);
        output.WriteLine(bad.Words);
        output.WriteLine(string.Create(inv, $"clean {clean.RmsPx:0.000} px; shifted {bad.RmsPx:0.000} px ({string.Join(", ", bad.Views.SelectMany(v => v.RmsByProjector.Select(kv => $"{v.Id}/{kv.Key} {kv.Value:0.000}")))})"));
        // The shift raises the residual tenfold.
        Assert.True(bad.RmsPx > 10 * clean.RmsPx, $"{bad.RmsPx:0.000} px against {clean.RmsPx:0.000} px");
        output.WriteLine(string.Join(", ", bad.Views.Select(v => string.Create(inv, $"{v.Id} {v.Inliers} of {v.Observations} inliers"))));

        // Each view's influence: dropping v2 brings the residual under half a pixel; dropping either good view leaves
        // the disagreement in. So v2 is the view to drop, and it is named by the numbers alone.
        var influence = MultiViewCalibration.Influence(input, bad);
        output.WriteLine(string.Create(inv, $"without: {string.Join("; ", influence.Select(i => $"{i.ViewId} {i.RmsWithoutPx:0.000} px"))}"));
        Assert.Equal(["v1", "v2", "v3"], influence.Select(i => i.ViewId));
        Assert.Equal("v2", influence.MaxBy(i => i.Gain)!.ViewId);
        Assert.All(influence, i => Assert.Equal(bad.RmsPx, i.RmsWithPx));
        var dropped = MultiViewCalibration.DropView(input, bad, "v2");
        output.WriteLine(dropped.Words);
        Assert.Equal(influence.Single(i => i.ViewId == "v2").RmsWithoutPx, dropped.RmsPx);
        Assert.True(dropped.RmsPx < 0.5, $"{dropped.RmsPx:0.000} px");
        // The shifted solve is itself under half a pixel (the joint solve spreads the shift over every view), so the
        // drop must bring the residual back to the clean scan's, not merely under the bar.
        Assert.True(dropped.RmsPx < bad.RmsPx / 10, $"{dropped.RmsPx:0.000} px against {bad.RmsPx:0.000} px");
        Assert.True(dropped.RmsPx < 2 * clean.RmsPx, $"{dropped.RmsPx:0.000} px against the clean {clean.RmsPx:0.000} px");
        Assert.All(influence.Where(i => i.ViewId != "v2"), i => Assert.True(i.RmsWithoutPx > 5 * dropped.RmsPx, $"without {i.ViewId}: {i.RmsWithoutPx:0.000} px"));
        Assert.Equal(["v1", "v3"], dropped.Views.Select(v => v.Id));
        Assert.Equal(["pj1", "pj2"], dropped.Projectors.Select(p => p.Id));
        // The warm start keeps the frame: the first view stays where the bad solve had it.
        Assert.Equal(bad.View("v1").Pose, dropped.View("v1").Pose);
        // And the projectors are still where they were truly hung.
        var toWorld = MultiViewKit.Registration(dropped);
        foreach (var p in dropped.Projectors)
        {
            var (position, angle, throwError) = MultiViewKit.Errors(p, toWorld);
            output.WriteLine(string.Create(inv, $"{p.Id}: {position * 1000:0.00} mm, {angle:0.0000}°, throw {throwError:P3}"));
            Assert.True(position < 0.005 && angle < 0.05 && throwError < 0.002, p.Id);
        }
    }

    [Fact]
    public void DroppingTheOnlyViewOfAProjectorLeavesThatProjectorOutAndSolvesTheRest()
    {
        // Only v1 decoded pj2; v2 and v3 saw pj1 alone.
        var decodes = new[] { MultiViewKit.Decode("v1", "pj1"), MultiViewKit.Decode("v1", "pj2"), MultiViewKit.Decode("v2", "pj1"), MultiViewKit.Decode("v3", "pj1") };
        var input = MultiViewKit.MakeInput(TrackSet.Build(decodes));
        input = input with { Views = [.. input.Views.Select(v => v.Id == "v1" ? v : v with { Pairs = v.Pairs.Remove("pj2") })] };
        var all = MultiViewCalibration.Solve(input);
        Assert.Equal(["pj1", "pj2"], all.Projectors.Select(p => p.Id));
        var dropped = MultiViewCalibration.DropView(input, all, "v1");
        output.WriteLine(dropped.Words);
        Assert.Equal(["v2", "v3"], dropped.Views.Select(v => v.Id));
        Assert.Equal(["pj1"], dropped.Projectors.Select(p => p.Id));
        Assert.Contains("projector pj2 is seen by no view left in the scan", dropped.Words, StringComparison.Ordinal);
        Assert.True(dropped.RmsPx < 0.5, $"{dropped.RmsPx:0.000} px");
        Assert.All(dropped.Tracks, t => Assert.Equal("pj1", t.ProjectorId));
    }

    [Fact]
    public void DroppingAViewTheScanDoesNotHaveIsRefused()
    {
        var e = Assert.Throws<ArgumentException>(() => MultiViewCalibration.DropView(MultiViewKit.Input, MultiViewKit.Solved, "v9"));
        Assert.StartsWith("This scan has no view v9.", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DroppingTheOnlyViewIsRefused()
    {
        var one = MultiViewKit.Input.WithoutView("v2").WithoutView("v3");
        var e = Assert.Throws<ArgumentException>(() => MultiViewCalibration.DropView(one, MultiViewKit.Solved, "v1"));
        Assert.StartsWith("View v1 is the scan's only view", e.Message, StringComparison.Ordinal);
    }
}
