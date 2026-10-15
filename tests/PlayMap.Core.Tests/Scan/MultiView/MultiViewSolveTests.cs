using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan.MultiView;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan.MultiView;

/// <summary>The projector-anchored multi-view solve on the wall-and-cylinder scene: two projectors, three phone views,
/// the phone's focal length prior 8 % off and the projectors' throw priors 5 % off. Every projector must land within
/// the thresholds a single calibrated view is held to (5 mm, 0.05°, throw 0.2 %), and the phone's focal length within
/// 0.5 %, with nothing about the scene known but its decodes.</summary>
public sealed class MultiViewSolveTests(ITestOutputHelper output)
{
    private const double PositionToleranceM = 0.005;
    private const double RotationToleranceDeg = 0.05;
    private const double ThrowTolerance = 0.002;
    private const double PhoneFocalTolerance = 0.005;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void TwoProjectorsAndThreeViewsSolveWithinTheSingleViewThresholds()
    {
        var r = MultiViewKit.Solved;
        output.WriteLine(r.Words);
        Assert.True(r.Solved);
        Assert.Empty(r.LeftOut);
        Assert.False(r.Metric);
        Assert.Equal(["v1", "v2", "v3"], r.Views.Select(v => v.Id));
        Assert.Equal(["pj1", "pj2"], r.Projectors.Select(p => p.Id));
        Assert.True(r.RmsPx < 0.5, $"{r.RmsPx:0.000} px");
        Assert.All(r.Views, v => Assert.Equal(v.Observations, v.Inliers));

        var toWorld = MultiViewKit.ToWorld;
        foreach (var p in r.Projectors)
        {
            var (position, angle, throwError) = MultiViewKit.Errors(p, toWorld);
            output.WriteLine(string.Create(Inv,
                $"{p.Id}: {position * 1000:0.00} mm, {angle:0.0000}°, throw {throwError:P3}, axial σ {p.AxialSigma * toWorld.Scale * 1000:0.00} mm, {p.Tracks} tracks"));
            Assert.False(p.Held);
            Assert.True(position < PositionToleranceM, $"{p.Id}: {position * 1000:0.00} mm");
            Assert.True(angle < RotationToleranceDeg, $"{p.Id}: {angle:0.0000}°");
            Assert.True(throwError < ThrowTolerance, $"{p.Id}: throw {throwError:P3}");
            Assert.True(p.AxialSigma > 0 && double.IsFinite(p.AxialSigma));
        }

        var rig = r.Rig(MultiViewKit.Rig);
        var focal = Math.Abs(rig.Intrinsics.Fx / MultiViewKit.Scene.Phone.Fx - 1);
        output.WriteLine(string.Create(Inv, $"phone focal {focal:P3} (σ {rig.FocalSigma:P3}), k1 {rig.Lens.K1:0.0000}, k2 {rig.Lens.K2:0.0000}; {r.Iterations} iterations"));
        Assert.False(rig.Held);
        Assert.True(focal < PhoneFocalTolerance, $"phone focal {focal:P3}");
        Assert.True(rig.FocalSigma < PhoneFocalTolerance);
    }

    [Fact]
    public void EachViewsDecodeInvertedLandsWhereTheCameraSawThatProjectorPixel()
    {
        var caster = new MeshRaycaster(MultiViewKit.Scene.Mesh);
        foreach (var d in MultiViewKit.Decodes)
        {
            var camera = MultiViewKit.TruthCamera(d.ViewId);
            var errors = new List<double>();
            var hidden = 0;
            for (var y = 2.0; y < d.Inverse.ProjectorHeight; y += 4)
            {
                for (var x = 2.0; x < d.Inverse.ProjectorWidth; x += 4)
                {
                    var u = new Vec2d(x, y);
                    if (!d.Inverse.TryAt(u, out var sample))
                        continue;
                    var point = MultiViewKit.Sim.TruthPoint(d.ProjectorId, u)!.Value;
                    var toPoint = point - camera.Pose.Position;
                    // Hidden: something stands at least a centimetre in front of the point (a silhouette's own grazing
                    // edge is seen, as the decode saw it).
                    if (caster.Cast(new Ray(camera.Pose.Position, toPoint.Normalized()), toPoint.Length - 0.01) is { } hit)
                    {
                        output.WriteLine(string.Create(Inv, $"  hidden u {u} point {point} hit {hit.Point} d {hit.Distance:0.000} of {toPoint.Length:0.000} sample {sample.Camera} truth {camera.Project(point)} n {sample.Samples} res {sample.FitResidualPx:0.000}"));
                        hidden++;
                        continue;
                    }
                    errors.Add((camera.Project(point)!.Value - sample.Camera).Length);
                }
            }
            errors.Sort();
            var rms = Math.Sqrt(errors.Sum(e => e * e) / errors.Count);
            output.WriteLine(string.Create(Inv,
                $"{d.ViewId}/{d.ProjectorId}: {errors.Count} answered, RMS {rms:0.000} px, 99 % {errors[(int)(errors.Count * 0.99)]:0.000} px, worst {errors[^1]:0.000} px, {hidden} hidden"));
            // No projector pixel the camera could not see is answered (edges and folds are refused, not bridged).
            Assert.Equal(0, hidden);
            Assert.True(rms < 0.1, $"{d.ViewId}/{d.ProjectorId}: RMS {rms:0.000} px");
            Assert.True(errors[(int)(errors.Count * 0.99)] < 0.3, $"{d.ViewId}/{d.ProjectorId}: 99 % {errors[(int)(errors.Count * 0.99)]:0.000} px");
            Assert.True(errors[^1] < 1.5, $"{d.ViewId}/{d.ProjectorId}: worst {errors[^1]:0.000} px");
        }
    }

    [Fact]
    public void TracksAreKeyedByProjectorPixelWhateverOrderTheViewsArrive()
    {
        var tracks = MultiViewKit.Input.Tracks;
        var reversed = TrackSet.Build(MultiViewKit.Decodes.Reverse());
        Assert.Equal(tracks.Count, reversed.Count);
        for (var i = 0; i < tracks.Count; i++)
        {
            Assert.Equal(tracks.Tracks[i].ProjectorId, reversed.Tracks[i].ProjectorId);
            Assert.Equal(tracks.Tracks[i].Projector, reversed.Tracks[i].Projector);
            Assert.Equal(tracks.Tracks[i].Seen.Select(o => o.ViewId), reversed.Tracks[i].Seen.Select(o => o.ViewId));
        }
        // Nodes on the 16-px grid, each view at most once per track, and most nodes seen from more than one place.
        Assert.All(tracks.Tracks, t =>
        {
            Assert.Equal(8.0, t.Projector.X % 16);
            Assert.Equal(8.0, t.Projector.Y % 16);
            Assert.Equal(t.Seen.Length, t.Seen.Select(o => o.ViewId).Distinct().Count());
        });
        Assert.True(tracks.SeenBy(2) > tracks.Count * 0.7, $"{tracks.SeenBy(2)} of {tracks.Count} seen twice");
        Assert.True(tracks.SeenBy(3) > 0);

        var without = tracks.Without("v2");
        Assert.DoesNotContain(without.Tracks, t => t.Seen.Any(o => o.ViewId == "v2"));
        Assert.Equal(tracks.Tracks.Count(t => t.Seen.Any(o => o.ViewId != "v2")), without.Count);
    }

    [Fact]
    public void AViewThatSharesNoProjectorIsLeftOutWithWords()
    {
        // v2 decoded only pj2, which no other view decoded: nothing ties it to the others.
        var decodes = new[] { MultiViewKit.Decode("v1", "pj1"), MultiViewKit.Decode("v2", "pj2"), MultiViewKit.Decode("v3", "pj1") };
        var input = MultiViewKit.MakeInput(TrackSet.Build(decodes));
        input = input with
        {
            Views = [.. input.Views.Select(v => v with { Pairs = v.Pairs.RemoveRange(v.Id == "v2" ? ["pj1"] : ["pj2"]) })],
        };
        var r = MultiViewCalibration.Solve(input);
        output.WriteLine(r.Words);
        var left = Assert.Single(r.LeftOut);
        Assert.Equal("v2", left.ViewId);
        Assert.Equal("view v2 shares no projector with the others; include part of a projector the other views saw", left.Words);
        Assert.Equal(["v1", "v3"], r.Views.Select(v => v.Id));
        Assert.Equal(["pj1"], r.Projectors.Select(p => p.Id));
        Assert.Contains("projector pj2 was solved by no chained view", r.Words, StringComparison.Ordinal);
        Assert.True(r.RmsPx < 0.5);
    }
}
