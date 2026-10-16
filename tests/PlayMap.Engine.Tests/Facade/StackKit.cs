using System.Collections.Immutable;
using PlayMap.Core.Align;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>A stack on align's simulator, as a node holds it: two projectors stacked by hand (the second's picture 0.8 px along, 0.4 px
/// down, a 0.3 % longer throw, 0.5° of roll; 6 500 K and a redder 7 500 K), both modelled at the first's pose, each on an output of its
/// raster, and a third aimed elsewhere; and the scan that saw both through one camera — align's real capture and decode, each
/// member's colour white frame, built into projector-space views as SCAN START keeps them.</summary>
internal static class StackKit
{
    public const string P1 = "pj00000001", P2 = "pj00000002", P3 = "pj00000003", Scan = "sc00000001";

    public static MultiProjectorSim Sim()
    {
        var stack = MultiProjectorSim.Stack(2);
        return new MultiProjectorSim(stack.Screen, stack.Camera,
        [
            stack.Projectors[0] with { Response = SimProjector.ResponseAt(6500) },
            stack.Projectors[1] with { Response = SimProjector.ResponseAt(7500, 0.01), White = 0.5 },
        ]);
    }

    public static Output OutputFor(string id, string projectorId, int width, int height) =>
        new(id, id.ToUpperInvariant(), OutputKind.Virtual, "", width, height, 60, OutputMode.Projector, projectorId, SourceRef.None,
            new RectD(0, 0, width, height), OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(0, 0, width, height), 0, true);

    public static Project Project(MultiProjectorSim sim)
    {
        var k = sim.Projectors[0].Intrinsics;
        var pose = sim.Projectors[0].Pose;
        Projector At(string id, string name, Pose at) => new(id, name, k, LensDistortion.None, at, 10_000, "#FFFFFF", false, [], SolveLocks.None, 0);
        var away = Pose.LookAt(pose.Position + new Vec3d(8, 0, 0), new Vec3d(14, 0, -3), Vec3d.UnitY);
        var p = Core.Model.Project.CreateDefault("Stacks");
        return p with
        {
            Outputs = [OutputFor("op00000001", P1, k.Width, k.Height), OutputFor("op00000002", P2, k.Width, k.Height), OutputFor("op00000003", P3, k.Width, k.Height)],
            Stage = p.Stage with
            {
                Projectors = [At(P1, "P1", pose), At(P2, "P2", pose), At(P3, "P3", away)],
                Scans = [new ScanRecord(Scan, "Stack scan", ScanKind.StructuredLight, 1_760_000_000_000, null, [P1, P2], "", null, 0, 0, "")],
            },
        };
    }

    private static List<AlignOutputSpec> Specs(MultiProjectorSim sim) =>
        [.. sim.Projectors.Select((p, j) => new AlignOutputSpec($"op{j + 1}", $"P{j + 1}", p.Intrinsics.Width, p.Intrinsics.Height,
            new ProjectorPrior(p.Intrinsics.Width, p.Intrinsics.Height, p.Intrinsics.Cx, p.Intrinsics.Cy, p.Intrinsics.Fx, 0.05)))];

    /// <summary>The scan of both members: each decode with its colour white frame (the member's full white, camera-encoded).</summary>
    public static async Task<ScanOutcome> OutcomeAsync(MultiProjectorSim sim, bool colour = true)
    {
        var shooter = new SimShooter(sim);
        var (_, captures) = await AlignSequence.CaptureAsync(shooter, Specs(sim), null, CancellationToken.None);
        var views = new List<(string, ProjectorView)>();
        string[] ids = [P1, P2];
        for (var j = 0; j < captures.Count; j++)
        {
            var map = captures[j].Map;
            if (colour)
            {
                var frames = new byte[]?[sim.Projectors.Count];
                var k = sim.Projectors[j].Intrinsics;
                frames[j] = AlignSequence.SolidRgb(k.Width, k.Height, 255, 255, 255, 1);
                var rgb = sim.CaptureRgb(frames);
                var white = new int[rgb.Length / 3];
                for (var i = 0; i < white.Length; i++)
                    white[i] = (rgb[i * 3] << 16) | (rgb[i * 3 + 1] << 8) | rgb[i * 3 + 2];
                map = WithColour(map, white);
            }
            views.Add((ids[j], ProjectorView.Build(map)));
        }
        var camera = new ScanCameraView("sim", "Simulator", sim.Camera.Intrinsics, sim.Camera.Lens, sim.Camera.Pose, true);
        var solution = new ScanSolution(camera, [], null, null, 0, [], 1, true, "stack", "the stack scanned through one camera");
        return new ScanOutcome(Scan, "Stack scan", "sim", true, "stack", solution, views, []);
    }

    private static CorrespondenceMap WithColour(CorrespondenceMap map, int[] white)
    {
        var copy = new CorrespondenceMap(map.Width, map.Height, map.ProjectorWidth, map.ProjectorHeight, map.Region, map.Axes, map.SubPixel)
        {
            White = map.White,
            WhiteColour = white,
        };
        map.ProjectorX.CopyTo(copy.ProjectorX, 0);
        map.ProjectorY.CopyTo(copy.ProjectorY, 0);
        map.Confidence.CopyTo(copy.Confidence, 0);
        map.Contrast.CopyTo(copy.Contrast, 0);
        map.Flags.CopyTo(copy.Flags, 0);
        return copy;
    }

    /// <summary>The misregistration (truth) of member 1 against member 0 over the master's raster, each drawing its picture through its
    /// output's measured lattice: p95 in master pixels.</summary>
    public static double MisregistrationP95(MultiProjectorSim sim, WarpLattice? master, WarpLattice? member) =>
        MisregistrationP95(sim, new WarpField(OutputWarp.None, null, master), new WarpField(OutputWarp.None, null, member));

    /// <summary>The same through each output's whole warp (its operator's warp with the measured lattice under it).</summary>
    public static double MisregistrationP95(MultiProjectorSim sim, WarpField master, WarpField member)
    {
        var k = sim.Projectors[0].Intrinsics;
        Vec2d Emit(WarpField f, Vec2d q)
        {
            var at = f.Evaluate(q.X / k.Width, q.Y / k.Height);
            return new Vec2d(at.X * k.Width, at.Y * k.Height);
        }
        var errors = new List<double>();
        for (var y = 0.08; y <= 0.92; y += 0.04)
            for (var x = 0.08; x <= 0.92; x += 0.04)
            {
                var q = new Vec2d(x * k.Width, y * k.Height);
                if (sim.ProjectorToScreen(0, Emit(master, q)) is not { } a || sim.ProjectorToScreen(1, Emit(member, q)) is not { } b
                    || sim.ScreenToRaster(0, a) is not { } qa || sim.ScreenToRaster(0, b) is not { } qb)
                    continue;
                errors.Add((qa - qb).Length);
            }
        return Core.Stacks.StackAlignment.Quantile(errors, 0.95);
    }

    public static ImmutableArray<string> Members => [P1, P2];

    /// <summary>Member j's white as the camera sees it over the area every member lights, showing its colour gains (linear light,
    /// out through the simulator's γ 2.2 as the output stage sends them): camera-linear RGB less black.</summary>
    public static Vec3d WhiteThrough(MultiProjectorSim sim, int j, double r, double g, double b)
    {
        static byte Level(double gain) => (byte)Math.Clamp(Math.Round(255 * Math.Pow(gain, 1 / 2.2)), 0, 255);
        var n = sim.CameraWidth * sim.CameraHeight;
        var shared = new bool[n];
        for (var y = 0; y < sim.CameraHeight; y++)
            for (var x = 0; x < sim.CameraWidth; x++)
            {
                if (sim.CameraToScreen(new Vec2d(x + 0.5, y + 0.5)) is not { } w)
                    continue;
                var all = true;
                for (var m = 0; m < sim.Projectors.Count && all; m++)
                    all = sim.ScreenToProjector(m, w) is { } q && q.X > 8 && q.Y > 8 && q.X < sim.Projectors[m].Intrinsics.Width - 8 && q.Y < sim.Projectors[m].Intrinsics.Height - 8;
                shared[y * sim.CameraWidth + x] = all;
            }
        var k = sim.Projectors[j].Intrinsics;
        var frames = new byte[]?[sim.Projectors.Count];
        frames[j] = AlignSequence.SolidRgb(k.Width, k.Height, Level(r), Level(g), Level(b), 1);
        var lit = sim.CaptureRgb(frames);
        var black = sim.CaptureRgb(new byte[]?[sim.Projectors.Count]);
        return ColourMatch.Mean(lit, shared) - ColourMatch.Mean(black, shared);
    }

    public static double Duv(Vec3d a, Vec3d b)
    {
        var (ua, va) = ColourMatch.Uv(a);
        var (ub, vb) = ColourMatch.Uv(b);
        return Math.Sqrt((ua - ub) * (ua - ub) + (va - vb) * (va - vb));
    }
}
