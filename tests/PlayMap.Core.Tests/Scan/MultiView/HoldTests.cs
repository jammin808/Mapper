using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Scan.MultiView;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan.MultiView;

/// <summary>HOLD: a held projector keeps its stage pose and optics bit for bit, and fixes the frame — re-solve only
/// the projector that moved, or, holding every projector, place the phone's views on the stage.</summary>
public sealed class HoldTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static void BitIdentical(MultiViewProjector given, SolvedProjector solved)
    {
        Assert.True(solved.Held);
        long Bits(double v) => BitConverter.DoubleToInt64Bits(v);
        var a = given.Stage!;
        var b = solved.Pose;
        Assert.Equal(
            new[] { a.Position.X, a.Position.Y, a.Position.Z, a.Rotation.X, a.Rotation.Y, a.Rotation.Z }.Select(Bits),
            new[] { b.Position.X, b.Position.Y, b.Position.Z, b.Rotation.X, b.Rotation.Y, b.Rotation.Z }.Select(Bits));
        Assert.Equal(
            new[] { given.Prior.Fx, given.Prior.Fy, given.Prior.Cx, given.Prior.Cy }.Select(Bits),
            new[] { solved.Intrinsics.Fx, solved.Intrinsics.Fy, solved.Intrinsics.Cx, solved.Intrinsics.Cy }.Select(Bits));
        Assert.Equal(given.Prior, solved.Intrinsics);
        Assert.Equal(given.Lens, solved.Lens);
        Assert.Equal(0, solved.AxialSigma);
    }

    [Fact]
    public void AHeldProjectorIsBitIdenticalAndTheOtherIsSolvedInTheStageFrame()
    {
        // pj1 held where the stage has it (its truth, priors as surveyed); pj2 free, with a stage pose 6 cm and 2° off
        // (it was bumped) that the solve may use for scale only.
        var truth2 = MultiViewKit.Scene.Projector("pj2").Device.Pose;
        var bumped = new Pose(truth2.Position + new Numerics.Vec3d(0.04, -0.03, 0.03), truth2.Rotation + new Numerics.Vec3d(0, 0.035, 0));
        var input = MultiViewKit.Input with
        {
            Projectors = [.. MultiViewKit.Input.Projectors.Select(p => p.Id == "pj1"
                ? p with { Held = true, Prior = MultiViewKit.Scene.Projector("pj1").Device.Intrinsics }
                : p with { Stage = bumped })],
        };
        var r = MultiViewCalibration.Solve(input);
        output.WriteLine(r.Words);
        BitIdentical(input.Projectors[0], r.Projector("pj1"));
        Assert.False(r.Metric);
        Assert.Contains("one projector held: the scale comes from the stage's distances to the others", r.Words, StringComparison.Ordinal);

        // The frame is the stage's: pj2's orientation and throw need no registration (they do not depend on scale).
        var pj2 = r.Projector("pj2");
        var truth = MultiViewKit.Scene.Projector("pj2").Device;
        var angle = MultiViewKit.AngleDeg(pj2.Pose, truth.Pose);
        var throwError = Math.Abs(pj2.Intrinsics.Fx / truth.Intrinsics.Fx - 1);
        // Its centre lies on the true line from the held projector (the scale along it is the stage's estimate): within
        // the 5 mm position tolerance across that 1.4 m baseline, 0.2°.
        var held = MultiViewKit.Scene.Projector("pj1").Device.Pose.Position;
        var bearing = Vec3dAngleDeg(pj2.Pose.Position - held, truth.Pose.Position - held);
        output.WriteLine(string.Create(Inv, $"pj2: {angle:0.0000}°, throw {throwError:P3}, bearing from pj1 {bearing:0.0000}°, RMS {r.RmsPx:0.000} px"));
        Assert.False(pj2.Held);
        Assert.True(angle < 0.05, $"{angle:0.0000}°");
        Assert.True(throwError < 0.002, $"{throwError:P3}");
        Assert.True(bearing < 0.2, $"{bearing:0.0000}°");
        Assert.True(r.RmsPx < 0.5);
    }

    [Fact]
    public void HoldingEveryProjectorPlacesTheViewsOnTheStage()
    {
        var input = MultiViewKit.Input with
        {
            Projectors = [.. MultiViewKit.Input.Projectors.Select(p => p with { Held = true, Prior = MultiViewKit.Scene.Projector(p.Id).Device.Intrinsics })],
        };
        var r = MultiViewCalibration.Solve(input);
        output.WriteLine(r.Words);
        Assert.True(r.Metric);
        foreach (var p in input.Projectors)
            BitIdentical(p, r.Projector(p.Id));
        foreach (var v in r.Views)
        {
            var truth = MultiViewKit.Scene.View(v.Id).Pose;
            var position = (v.Pose.Position - truth.Position).Length;
            var angle = MultiViewKit.AngleDeg(v.Pose, truth);
            output.WriteLine(string.Create(Inv, $"{v.Id}: {position * 1000:0.00} mm, {angle:0.0000}°, RMS {v.RmsPx:0.000} px"));
            Assert.True(position < 0.005, $"{v.Id}: {position * 1000:0.00} mm");
            Assert.True(angle < 0.05, $"{v.Id}: {angle:0.0000}°");
        }
        var focal = Math.Abs(r.Rig(MultiViewKit.Rig).Intrinsics.Fx / MultiViewKit.Scene.Phone.Fx - 1);
        Assert.True(focal < 0.005, $"{focal:P3}");
    }

    [Fact]
    public void AHeldProjectorWithoutAStagePoseIsRefused()
    {
        var input = MultiViewKit.Input with
        {
            Projectors = [.. MultiViewKit.Input.Projectors.Select(p => p.Id == "pj2" ? p with { Held = true, Stage = null } : p)],
        };
        var e = Assert.Throws<ArgumentException>(() => MultiViewCalibration.Solve(input));
        Assert.StartsWith("Projector pj2 is held but has no stage pose.", e.Message, StringComparison.Ordinal);
    }

    private static double Vec3dAngleDeg(Numerics.Vec3d a, Numerics.Vec3d b) =>
        Math.Acos(Math.Clamp(Numerics.Vec3d.Dot(a.Normalized(), b.Normalized()), -1, 1)) * 180 / Math.PI;
}
