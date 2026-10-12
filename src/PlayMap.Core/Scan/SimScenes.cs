using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>
/// Ready-made scenes for the simulator, each with a projector and a camera that frame it (world +Y up,
/// metres): a floor with a box, a wall with a standing cylinder, and a stepped facade. The camera sits off
/// to one side of the projector, as a webcam on a tripod beside it would, so part of what it sees lies in the
/// projector's shadow. Resolutions are the caller's; the optics scale with them.
/// </summary>
public static class SimScenes
{
    /// <summary>The box of <see cref="FloorAndBox"/>: half-width and depth 0.25 m, height 0.35 m, standing on the floor at the origin.</summary>
    public const double BoxHalf = 0.25;

    public const double BoxHeight = 0.35;

    /// <summary>The dark square painted on the floor of <see cref="FloorAndBox"/>, in front of the box (x, z, side).</summary>
    public static readonly (double X, double Z, double Side) FloorMarker = (0.55, 0.45, 0.2);

    /// <summary>A 5 × 5 m floor with a box standing in the middle and a dark square on the floor in front of it.</summary>
    public static SimScene FloorAndBox(int projectorWidth = 256, int projectorHeight = 160, int cameraWidth = 320, int cameraHeight = 240)
    {
        var b = new SceneMeshBuilder();
        b.AddQuad(new Vec3d(-2.5, 0, 2.5), new Vec3d(2.5, 0, 2.5), new Vec3d(2.5, 0, -2.5), new Vec3d(-2.5, 0, -2.5));
        b.AddBox(new Vec3d(-BoxHalf, 0, -BoxHalf), new Vec3d(BoxHalf, BoxHeight, BoxHalf));
        var (mx, mz, side) = FloorMarker;
        double Albedo(Vec3d p)
        {
            if (p.Y > 1e-6)
                return 0.8;
            return Math.Abs(p.X - mx) <= side / 2 && Math.Abs(p.Z - mz) <= side / 2 ? 0.12 : 0.7;
        }
        int Colour(Vec3d p) => p.Y > 1e-6 ? 0xE0B080 : Albedo(p) < 0.5 ? 0x202020 : 0xA8B4C0;
        return new SimScene(
            "floor and box",
            b.Build(),
            Albedo,
            Colour,
            Device(projectorWidth, projectorHeight, Intrinsics.FromThrowRatio(projectorWidth, projectorHeight, 1.25), new Vec3d(0, 1.6, 2.4), new Vec3d(0, 0.15, 0)),
            CameraDevice(cameraWidth, cameraHeight, 48, new Vec3d(0.55, 1.45, 2.3), new Vec3d(0, 0.15, 0)));
    }

    /// <summary>A wall with a floor, and a cylinder (radius 0.25 m, 1.2 m tall) standing half a metre in front of it.</summary>
    public static SimScene WallAndCylinder(int projectorWidth = 256, int projectorHeight = 160, int cameraWidth = 320, int cameraHeight = 240)
    {
        var b = new SceneMeshBuilder();
        b.AddQuad(new Vec3d(-2.5, 0, 0), new Vec3d(2.5, 0, 0), new Vec3d(2.5, 3, 0), new Vec3d(-2.5, 3, 0));
        b.AddQuad(new Vec3d(-2.5, 0, 3), new Vec3d(2.5, 0, 3), new Vec3d(2.5, 0, 0), new Vec3d(-2.5, 0, 0));
        b.AddCylinder(new Vec3d(0.1, 0, 0.5), 0.25, 1.2, 48);
        static double Albedo(Vec3d p) => p.Z > 1e-6 && p.Y > 1e-6 ? 0.85 : 0.65;
        static int Colour(Vec3d p) => p.Z > 1e-6 && p.Y > 1e-6 ? 0xF0F0E8 : 0xB0A890;
        return new SimScene(
            "wall and cylinder",
            b.Build(),
            Albedo,
            Colour,
            Device(projectorWidth, projectorHeight, Intrinsics.FromThrowRatio(projectorWidth, projectorHeight, 1.1), new Vec3d(0, 1.3, 3.0), new Vec3d(0, 0.8, 0)),
            CameraDevice(cameraWidth, cameraHeight, 50, new Vec3d(0.6, 1.2, 2.8), new Vec3d(0.05, 0.75, 0)));
    }

    /// <summary>A facade of three slabs stepping toward the viewer (fronts at z = 0, 0.3, 0.6 m; tops at 2.4,
    /// 1.6, 0.8 m) on a ground plane, with a dark window in the back slab.</summary>
    public static SimScene SteppedFacade(int projectorWidth = 256, int projectorHeight = 160, int cameraWidth = 320, int cameraHeight = 240)
    {
        var b = new SceneMeshBuilder();
        b.AddQuad(new Vec3d(-3, 0, 4), new Vec3d(3, 0, 4), new Vec3d(3, 0, -1), new Vec3d(-3, 0, -1));
        b.AddBox(new Vec3d(-1.5, 0, -0.6), new Vec3d(1.5, 2.4, 0));
        b.AddBox(new Vec3d(-1.2, 0, 0), new Vec3d(1.2, 1.6, 0.3));
        b.AddBox(new Vec3d(-0.9, 0, 0.3), new Vec3d(0.9, 0.8, 0.6));
        static bool Window(Vec3d p) => Math.Abs(p.Z) < 1e-6 && p.Y > 1.8 && p.Y < 2.2 && Math.Abs(p.X + 0.6) < 0.3;
        static double Albedo(Vec3d p) => Window(p) ? 0.15 : p.Y < 1e-6 ? 0.5 : 0.75;
        static int Colour(Vec3d p) => Window(p) ? 0x303848 : p.Y < 1e-6 ? 0x707070 : 0xD8C8B0;
        return new SimScene(
            "stepped facade",
            b.Build(),
            Albedo,
            Colour,
            Device(projectorWidth, projectorHeight, Intrinsics.FromThrowRatio(projectorWidth, projectorHeight, 1.0), new Vec3d(0, 1.5, 4.0), new Vec3d(0, 1.1, 0)),
            CameraDevice(cameraWidth, cameraHeight, 52, new Vec3d(0.9, 1.6, 3.8), new Vec3d(0, 1.1, 0)));
    }

    private static SimDevice Device(int width, int height, Intrinsics k, Vec3d eye, Vec3d target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        return new SimDevice(k, LensDistortion.None, Pose.LookAt(eye, target, Vec3d.UnitY));
    }

    /// <summary>A webcam: horizontal field of view in degrees and a little barrel distortion.</summary>
    private static SimDevice CameraDevice(int width, int height, double fovDegrees, Vec3d eye, Vec3d target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        return new SimDevice(Intrinsics.FromFieldOfView(width, height, fovDegrees), new LensDistortion(-0.06, 0.01, 0, 0, 0), Pose.LookAt(eye, target, Vec3d.UnitY));
    }
}

/// <summary>Builds a triangle mesh from quads, boxes and cylinders with outward, counter-clockwise faces and
/// flat normals (no vertex is shared between faces).</summary>
public sealed class SceneMeshBuilder
{
    private readonly List<float> _positions = [];
    private readonly List<uint> _indices = [];

    /// <summary>A quad a-b-c-d, counter-clockwise seen from the side it faces.</summary>
    public SceneMeshBuilder AddQuad(Vec3d a, Vec3d b, Vec3d c, Vec3d d)
    {
        var start = (uint)(_positions.Count / 3);
        foreach (var p in (ReadOnlySpan<Vec3d>)[a, b, c, d])
        {
            _positions.Add((float)p.X);
            _positions.Add((float)p.Y);
            _positions.Add((float)p.Z);
        }
        _indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        return this;
    }

    /// <summary>An axis-aligned box, all six faces outward.</summary>
    public SceneMeshBuilder AddBox(Vec3d min, Vec3d max)
    {
        Vec3d P(double x, double y, double z) => new(x, y, z);
        var (x0, y0, z0) = (min.X, min.Y, min.Z);
        var (x1, y1, z1) = (max.X, max.Y, max.Z);
        AddQuad(P(x0, y1, z1), P(x1, y1, z1), P(x1, y1, z0), P(x0, y1, z0)); // top (+Y)
        AddQuad(P(x0, y0, z0), P(x1, y0, z0), P(x1, y0, z1), P(x0, y0, z1)); // bottom (−Y)
        AddQuad(P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1)); // front (+Z)
        AddQuad(P(x1, y0, z0), P(x0, y0, z0), P(x0, y1, z0), P(x1, y1, z0)); // back (−Z)
        AddQuad(P(x1, y0, z1), P(x1, y0, z0), P(x1, y1, z0), P(x1, y1, z1)); // right (+X)
        AddQuad(P(x0, y0, z0), P(x0, y0, z1), P(x0, y1, z1), P(x0, y1, z0)); // left (−X)
        return this;
    }

    /// <summary>A vertical cylinder standing on <paramref name="baseCentre"/>, with a top cap.</summary>
    public SceneMeshBuilder AddCylinder(Vec3d baseCentre, double radius, double height, int segments)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);
        Vec3d Rim(int i, double y)
        {
            var a = 2 * Math.PI * i / segments;
            return new Vec3d(baseCentre.X + radius * Math.Cos(a), baseCentre.Y + y, baseCentre.Z - radius * Math.Sin(a));
        }
        var top = baseCentre + new Vec3d(0, height, 0);
        for (var i = 0; i < segments; i++)
        {
            AddQuad(Rim(i, 0), Rim(i + 1, 0), Rim(i + 1, height), Rim(i, height));
            AddTriangle(top, Rim(i, height), Rim(i + 1, height));
        }
        return this;
    }

    public SceneMeshBuilder AddTriangle(Vec3d a, Vec3d b, Vec3d c)
    {
        var start = (uint)(_positions.Count / 3);
        foreach (var p in (ReadOnlySpan<Vec3d>)[a, b, c])
        {
            _positions.Add((float)p.X);
            _positions.Add((float)p.Y);
            _positions.Add((float)p.Z);
        }
        _indices.AddRange([start, start + 1, start + 2]);
        return this;
    }

    public MeshData Build() => new MeshData([.. _positions], [], [], [.. _indices]).WithComputedNormals();
}
