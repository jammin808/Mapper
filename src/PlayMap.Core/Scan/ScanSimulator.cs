using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>A simulated camera or projector: optics and pose.</summary>
public sealed record SimDevice(Intrinsics Intrinsics, LensDistortion Lens, Pose Pose);

/// <summary>A scene to scan: a mesh, its grey albedo (0–1) and colour (0xRRGGBB) at a world point, and a
/// projector and camera that frame it.</summary>
public sealed record SimScene(
    string Name,
    MeshData Mesh,
    Func<Vec3d, double> Albedo,
    Func<Vec3d, int> Colour,
    SimDevice Projector,
    SimDevice Camera);

/// <summary>How the simulated light and camera behave. Levels are on the camera's 0–255 scale.</summary>
public sealed record SimOptions
{
    /// <summary>The projector's full-white irradiance on a surface facing it squarely, as a share of the
    /// camera's full scale at exposure 1 and albedo 1.</summary>
    public double ProjectorGain { get; init; } = 0.8;

    /// <summary>The room's light, as a share of full scale at albedo 1.</summary>
    public double Ambient { get; init; } = 0.03;

    /// <summary>The camera's read noise (standard deviation in levels).</summary>
    public double NoiseSigma { get; init; } = 1.0;

    /// <summary>The projector's display gamma: a level v emits (v/255)^gamma.</summary>
    public double ProjectorGamma { get; init; } = 2.2;

    /// <summary>The camera's encoding gamma: light L is recorded as 255·L^(1/gamma).</summary>
    public double CameraGamma { get; init; } = 1.0;

    public double Exposure { get; init; } = 1.0;

    /// <summary>Scales every albedo (a darker or brighter scene).</summary>
    public double AlbedoScale { get; init; } = 1.0;

    /// <summary>Points the projector cannot see (behind an occluder) get no projector light.</summary>
    public bool Shadows { get; init; } = true;

    public ulong Seed { get; init; } = 1;
}

/// <summary>What the simulated camera really saw, per camera pixel (row-major).</summary>
public sealed class SimTruth
{
    internal SimTruth(int width, int height)
    {
        Width = width;
        Height = height;
        var n = width * height;
        ProjectorX = new float[n];
        ProjectorY = new float[n];
        Points = new Vec3d[n];
        Normals = new Vec3d[n];
        Triangles = new int[n];
        Lit = new bool[n];
        Shadowed = new bool[n];
        ExpectedContrast = new float[n];
        Array.Fill(ProjectorX, float.NaN);
        Array.Fill(ProjectorY, float.NaN);
        Array.Fill(Points, PointCloud.Invalid);
        Array.Fill(Triangles, -1);
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The projector point lighting the seen surface point (NaN where the scan region's light does not reach it).</summary>
    public float[] ProjectorX { get; }

    public float[] ProjectorY { get; }

    /// <summary>The seen world point (<see cref="PointCloud.Invalid"/> where the ray met nothing).</summary>
    public Vec3d[] Points { get; }

    /// <summary>The seen surface's normal, turned toward the camera.</summary>
    public Vec3d[] Normals { get; }

    /// <summary>The mesh triangle seen, −1 for none.</summary>
    public int[] Triangles { get; }

    /// <summary>Seen, inside the scan region, facing the projector and not occluded from it.</summary>
    public bool[] Lit { get; }

    /// <summary>Seen and inside the projector's raster, but no projector light arrives: occluded or facing away.</summary>
    public bool[] Shadowed { get; }

    /// <summary>White − black the camera records here without noise (linear levels).</summary>
    public float[] ExpectedContrast { get; }

    /// <summary>The seen points as an organised cloud on the camera grid, optionally with Gaussian noise along
    /// each axis (metres), seeded.</summary>
    public PointCloud Cloud(double noise = 0, ulong seed = 7)
    {
        var points = (Vec3d[])Points.Clone();
        if (noise > 0)
        {
            var rng = new Pcg32(seed);
            for (var k = 0; k < points.Length; k++)
            {
                if (PointCloud.IsValid(points[k]))
                    points[k] += new Vec3d(rng.NextGaussian(), rng.NextGaussian(), rng.NextGaussian()) * noise;
            }
        }
        return new PointCloud(points, gridWidth: Width, gridHeight: Height);
    }
}

/// <summary>
/// A virtual projector and camera around a mesh, for tests and the desk's demo mode. Each camera pixel's ray
/// is cast once into the scene; the hit is projected into the projector (the model's camera model, lens
/// distortion included) and tested for occlusion with a shadow ray. Each plan frame is then rendered as the
/// camera would record it: albedo × (the pattern sampled bilinearly, through the projector's gamma, × gain ×
/// cos incidence) + albedo × ambient, then exposure, the camera's gamma, read noise (Pcg32, seeded per frame
/// and row) and 8-bit clipping. Deterministic for a seed; frames render in parallel by row.
/// </summary>
public sealed class ScanSimulator
{
    private readonly ScanPlan _plan;
    private readonly SimOptions _options;
    private readonly float[] _u;
    private readonly float[] _v;
    private readonly float[] _direct;
    private readonly float[] _ambient;
    private readonly float[] _irradiance;
    private readonly int[] _colour;
    private readonly float[] _projectorLut = new float[256];
    private readonly float[] _cameraLut = new float[4097];

    public ScanSimulator(SimScene scene, ScanPlan plan, SimOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(plan);
        var kp = scene.Projector.Intrinsics;
        if (kp.Width != plan.ProjectorWidth || kp.Height != plan.ProjectorHeight)
            throw new ArgumentException("The plan is for another projector raster than the scene's projector.", nameof(plan));
        Scene = scene;
        _plan = plan;
        _options = options ?? new SimOptions();
        var kc = scene.Camera.Intrinsics;
        CameraWidth = kc.Width;
        CameraHeight = kc.Height;
        var n = CameraWidth * CameraHeight;
        _u = new float[n];
        _v = new float[n];
        _direct = new float[n];
        _ambient = new float[n];
        _irradiance = new float[n];
        _colour = new int[n];
        Array.Fill(_u, float.NaN);
        Array.Fill(_v, float.NaN);
        for (var i = 0; i < 256; i++)
            _projectorLut[i] = (float)Math.Pow(i / 255.0, _options.ProjectorGamma);
        for (var i = 0; i < _cameraLut.Length; i++)
            _cameraLut[i] = (float)(255 * Math.Pow(i / (double)(_cameraLut.Length - 1), 1 / _options.CameraGamma));
        Truth = new SimTruth(CameraWidth, CameraHeight);
        var caster = new MeshRaycaster(scene.Mesh);
        Parallel.For(0, CameraHeight, y =>
        {
            for (var x = 0; x < CameraWidth; x++)
                Trace(caster, x, y);
        });
    }

    public SimScene Scene { get; }
    public int CameraWidth { get; }
    public int CameraHeight { get; }
    public SimTruth Truth { get; }

    private void Trace(MeshRaycaster caster, int x, int y)
    {
        var k = y * CameraWidth + x;
        var cam = Scene.Camera;
        var ray = CameraModel.PixelRay(new Vec2d(x + 0.5, y + 0.5), cam.Intrinsics, cam.Lens, cam.Pose);
        if (caster.Cast(ray) is not { } hit)
            return;
        var mesh = caster.Mesh;
        var (a, b, c) = mesh.Triangle(hit.Triangle);
        var normal = Vec3d.Cross(mesh.Position(b) - mesh.Position(a), mesh.Position(c) - mesh.Position(a)).Normalized();
        if (Vec3d.Dot(normal, ray.Direction) > 0)
            normal = -normal;
        var albedo = Math.Clamp(Scene.Albedo(hit.Point) * _options.AlbedoScale, 0, 1);
        Truth.Points[k] = hit.Point;
        Truth.Normals[k] = normal;
        Truth.Triangles[k] = hit.Triangle;
        _ambient[k] = (float)(albedo * _options.Ambient);
        _colour[k] = Scene.Colour(hit.Point);

        var proj = Scene.Projector;
        if (CameraModel.Project(hit.Point, proj.Intrinsics, proj.Lens, proj.Pose) is not { } p || !CameraModel.InRaster(p, proj.Intrinsics))
            return;
        var toProjector = proj.Pose.Position - hit.Point;
        var distance = toProjector.Length;
        var direction = toProjector / distance;
        var cos = Vec3d.Dot(normal, direction);
        var lit = cos > 0
            && !(_options.Shadows && caster.Occluded(new Ray(hit.Point + direction * 1e-6, direction), distance - 2e-6));
        if (!lit)
        {
            Truth.Shadowed[k] = true;
            return;
        }
        _u[k] = (float)p.X;
        _v[k] = (float)p.Y;
        _irradiance[k] = (float)(_options.ProjectorGain * cos);
        _direct[k] = (float)(albedo * _irradiance[k]);
        var region = _plan.Region;
        if (p.X >= region.X && p.Y >= region.Y && p.X < region.Right && p.Y < region.Bottom)
        {
            Truth.Lit[k] = true;
            Truth.ProjectorX[k] = (float)p.X;
            Truth.ProjectorY[k] = (float)p.Y;
            Truth.ExpectedContrast[k] = Record(_direct[k] + _ambient[k]) - Record(_ambient[k]);
        }
    }

    /// <summary>The level a linear light records at without noise.</summary>
    private float Record(double light)
    {
        var e = Math.Clamp(light * _options.Exposure, 0, 1);
        var i = e * (_cameraLut.Length - 1);
        var i0 = (int)i;
        var i1 = Math.Min(i0 + 1, _cameraLut.Length - 1);
        return (float)(_cameraLut[i0] + (_cameraLut[i1] - _cameraLut[i0]) * (i - i0));
    }

    /// <summary>The camera's gray8 picture of plan frame <paramref name="index"/>.</summary>
    public byte[] Render(int index)
    {
        var pattern = _plan.Render(index);
        var light = new float[pattern.Length];
        for (var i = 0; i < pattern.Length; i++)
            light[i] = _projectorLut[pattern[i]];
        var image = new byte[CameraWidth * CameraHeight];
        var pw = _plan.ProjectorWidth;
        var ph = _plan.ProjectorHeight;
        var seed = _options.Seed * 0x9E3779B97F4A7C15UL + (ulong)index;
        Parallel.For(0, CameraHeight, y =>
        {
            var rng = new Pcg32(seed, (ulong)y);
            for (var x = 0; x < CameraWidth; x++)
            {
                var k = y * CameraWidth + x;
                double shade = _ambient[k];
                if (!float.IsNaN(_u[k]))
                    shade += _direct[k] * SampleBilinear(light, pw, ph, _u[k] - 0.5, _v[k] - 0.5);
                var level = Record(shade);
                if (_options.NoiseSigma > 0)
                    level += (float)(rng.NextGaussian() * _options.NoiseSigma);
                image[k] = (byte)Math.Clamp(MathF.Round(level), 0, 255);
            }
        });
        return image;
    }

    /// <summary>Every plan frame, in plan order.</summary>
    public byte[][] RenderAll()
    {
        var frames = new byte[_plan.Count][];
        for (var i = 0; i < frames.Length; i++)
            frames[i] = Render(i);
        return frames;
    }

    /// <summary>The camera's colour picture under the white frame (0xRRGGBB, no noise).</summary>
    public int[] RenderWhiteColour()
    {
        var image = new int[CameraWidth * CameraHeight];
        for (var k = 0; k < image.Length; k++)
        {
            if (Truth.Triangles[k] < 0)
                continue;
            var light = (_irradiance[k] + _options.Ambient) * _options.AlbedoScale / 255.0;
            var colour = _colour[k];
            var r = Record(((colour >> 16) & 0xFF) * light);
            var g = Record(((colour >> 8) & 0xFF) * light);
            var b = Record((colour & 0xFF) * light);
            image[k] = (Level(r) << 16) | (Level(g) << 8) | Level(b);
        }
        return image;
    }

    private static int Level(float v) => (int)Math.Clamp(MathF.Round(v), 0, 255);

    private static float SampleBilinear(float[] image, int w, int h, double fx, double fy)
    {
        fx = Math.Clamp(fx, 0, w - 1);
        fy = Math.Clamp(fy, 0, h - 1);
        var x0 = Math.Max(0, Math.Min((int)fx, w - 2));
        var y0 = Math.Max(0, Math.Min((int)fy, h - 2));
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var tx = (float)(fx - x0);
        var ty = (float)(fy - y0);
        var top = image[y0 * w + x0] * (1 - tx) + image[y0 * w + x1] * tx;
        var bottom = image[y1 * w + x0] * (1 - tx) + image[y1 * w + x1] * tx;
        return top * (1 - ty) + bottom * ty;
    }
}
