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

/// <summary>
/// A scene traced once from its camera: per camera pixel (row-major) the world point seen through the pixel's
/// centre, its normal turned toward the camera, the triangle, the scene's albedo and colour there, and where
/// the projector's light comes from — the projector pixel and cos incidence where the point faces the
/// projector inside its raster, and whether something occludes it. With <see cref="Supersample"/> s &gt; 1
/// the light is also traced through s × s points spread over each pixel (<c>Sample…</c>, s² per pixel, pixel
/// by pixel), so the simulated camera integrates over its pixel's footprint as a real one does: fine stripes
/// a pixel spans blur into grey. Independent of the plan and the light levels, so one trace serves any number
/// of simulated scans. Immutable after construction.
/// </summary>
public sealed class SimGeometry
{
    public SimGeometry(SimScene scene, int supersample = 1)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfLessThan(supersample, 1);
        Scene = scene;
        Supersample = supersample;
        var kc = scene.Camera.Intrinsics;
        Width = kc.Width;
        Height = kc.Height;
        var n = Width * Height;
        Points = new Vec3d[n];
        Normals = new Vec3d[n];
        Triangles = new int[n];
        Albedo = new float[n];
        Colour = new int[n];
        RasterX = new float[n];
        RasterY = new float[n];
        Cos = new float[n];
        Occluded = new bool[n];
        Array.Fill(Points, PointCloud.Invalid);
        Array.Fill(Triangles, -1);
        Array.Fill(RasterX, float.NaN);
        Array.Fill(RasterY, float.NaN);
        var caster = new MeshRaycaster(scene.Mesh);
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                var k = y * Width + x;
                var centre = Trace(caster, new Vec2d(x + 0.5, y + 0.5));
                if (centre.Triangle < 0)
                    continue;
                Points[k] = centre.Point;
                Normals[k] = centre.Normal;
                Triangles[k] = centre.Triangle;
                Albedo[k] = centre.Albedo;
                Colour[k] = Scene.Colour(centre.Point);
                RasterX[k] = centre.RasterX;
                RasterY[k] = centre.RasterY;
                Cos[k] = centre.Cos;
                Occluded[k] = centre.Occluded;
            }
        });
        if (supersample == 1)
        {
            SampleRasterX = RasterX;
            SampleRasterY = RasterY;
            SampleCos = Cos;
            SampleOccluded = Occluded;
            SampleAlbedo = Albedo;
            return;
        }
        var s2 = supersample * supersample;
        SampleRasterX = new float[n * s2];
        SampleRasterY = new float[n * s2];
        SampleCos = new float[n * s2];
        SampleOccluded = new bool[n * s2];
        SampleAlbedo = new float[n * s2];
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                for (var j = 0; j < supersample; j++)
                {
                    for (var i = 0; i < supersample; i++)
                    {
                        var at = (y * Width + x) * s2 + j * supersample + i;
                        var sample = Trace(caster, new Vec2d(x + (i + 0.5) / supersample, y + (j + 0.5) / supersample));
                        SampleRasterX[at] = sample.RasterX;
                        SampleRasterY[at] = sample.RasterY;
                        SampleCos[at] = sample.Cos;
                        SampleOccluded[at] = sample.Occluded;
                        SampleAlbedo[at] = sample.Albedo;
                    }
                }
            }
        });
    }

    public SimScene Scene { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Light samples per pixel along each axis.</summary>
    public int Supersample { get; }

    public Vec3d[] Points { get; }
    public Vec3d[] Normals { get; }
    public int[] Triangles { get; }
    public float[] Albedo { get; }
    public int[] Colour { get; }

    /// <summary>The projector point lighting the seen point, NaN where it faces away or lies outside the raster.</summary>
    public float[] RasterX { get; }

    public float[] RasterY { get; }

    /// <summary>Cos incidence of the projector's light (0 where <see cref="RasterX"/> is NaN).</summary>
    public float[] Cos { get; }

    /// <summary>Something lies between the seen point and the projector, or the point faces away from it.</summary>
    public bool[] Occluded { get; }

    /// <summary>The light samples (Supersample² per pixel): the same as the per-pixel arrays when 1.</summary>
    public float[] SampleRasterX { get; }

    public float[] SampleRasterY { get; }
    public float[] SampleCos { get; }
    public bool[] SampleOccluded { get; }

    /// <summary>The albedo at each light sample (0 where the sample's ray meets nothing).</summary>
    public float[] SampleAlbedo { get; }

    private readonly record struct Traced(int Triangle, Vec3d Point, Vec3d Normal, float Albedo, float RasterX, float RasterY, float Cos, bool Occluded);

    private Traced Trace(MeshRaycaster caster, Vec2d pixel)
    {
        var cam = Scene.Camera;
        var ray = CameraModel.PixelRay(pixel, cam.Intrinsics, cam.Lens, cam.Pose);
        if (caster.Cast(ray) is not { } hit)
            return new Traced(-1, PointCloud.Invalid, Vec3d.Zero, 0, float.NaN, float.NaN, 0, false);
        var mesh = caster.Mesh;
        var (a, b, c) = mesh.Triangle(hit.Triangle);
        var normal = Vec3d.Cross(mesh.Position(b) - mesh.Position(a), mesh.Position(c) - mesh.Position(a)).Normalized();
        if (Vec3d.Dot(normal, ray.Direction) > 0)
            normal = -normal;
        var albedo = (float)Scene.Albedo(hit.Point);
        var proj = Scene.Projector;
        if (CameraModel.Project(hit.Point, proj.Intrinsics, proj.Lens, proj.Pose) is not { } p || !CameraModel.InRaster(p, proj.Intrinsics))
            return new Traced(hit.Triangle, hit.Point, normal, albedo, float.NaN, float.NaN, 0, false);
        var toProjector = proj.Pose.Position - hit.Point;
        var distance = toProjector.Length;
        var direction = toProjector / distance;
        var cos = Vec3d.Dot(normal, direction);
        if (cos <= 0)
            return new Traced(hit.Triangle, hit.Point, normal, albedo, float.NaN, float.NaN, 0, true);
        var occluded = caster.Occluded(new Ray(hit.Point + direction * 1e-6, direction), distance - 2e-6);
        return new Traced(hit.Triangle, hit.Point, normal, albedo, (float)p.X, (float)p.Y, (float)cos, occluded);
    }
}

/// <summary>What the simulated camera really saw, per camera pixel (row-major).</summary>
public sealed class SimTruth
{
    internal SimTruth(SimGeometry geometry)
    {
        Width = geometry.Width;
        Height = geometry.Height;
        var n = Width * Height;
        ProjectorX = new float[n];
        ProjectorY = new float[n];
        Points = geometry.Points;
        Normals = geometry.Normals;
        Triangles = geometry.Triangles;
        Lit = new bool[n];
        Shadowed = new bool[n];
        ExpectedContrast = new float[n];
        Array.Fill(ProjectorX, float.NaN);
        Array.Fill(ProjectorY, float.NaN);
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
/// A virtual projector and camera around a mesh, for tests and the desk's demo mode. The scene is traced once
/// (<see cref="SimGeometry"/>: each camera pixel's ray cast into the mesh, the hit projected into the projector
/// through the model's camera model, lens distortion included, and tested for occlusion with a shadow ray).
/// Each plan frame is then rendered as the camera would record it: albedo × (the pattern sampled bilinearly,
/// through the projector's gamma, × gain × cos incidence) + albedo × ambient, then exposure, the camera's
/// gamma, read noise (Pcg32, seeded per frame and row) and 8-bit clipping. Deterministic for a seed; frames
/// render in parallel by row.
/// </summary>
public sealed class ScanSimulator
{
    private readonly ScanPlan _plan;
    private readonly SimOptions _options;
    private readonly SimGeometry _geometry;
    private readonly float[] _direct;
    private readonly float[] _ambient;
    private readonly float[] _irradiance;
    private readonly int _samples;
    private readonly float[] _projectorLut = new float[256];
    private readonly float[] _cameraLut = new float[4097];

    public ScanSimulator(SimScene scene, ScanPlan plan, SimOptions? options = null)
        : this(new SimGeometry(scene), plan, options)
    {
    }

    /// <summary>A device knocked in its own axes (wp2-wall's re-solve tests and the "knock Projector 2" demo): turned by
    /// <paramref name="rotation"/> (a rotation vector, radians) and moved by <paramref name="translation"/> (metres), both in the
    /// device's frame (x right, y down, z along the lens); its optics unchanged.</summary>
    public static SimDevice Bump(SimDevice device, Vec3d rotation, Vec3d translation)
    {
        ArgumentNullException.ThrowIfNull(device);
        var c2w = device.Pose.CameraToWorld;
        var turned = c2w * Mat3d.FromRotationVector(rotation);
        return device with { Pose = new Pose(device.Pose.Position + c2w * translation, turned.ToRotationVector()) };
    }

    /// <summary>A simulator on an already traced scene.</summary>
    public ScanSimulator(SimGeometry geometry, ScanPlan plan, SimOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(plan);
        var kp = geometry.Scene.Projector.Intrinsics;
        if (kp.Width != plan.ProjectorWidth || kp.Height != plan.ProjectorHeight)
            throw new ArgumentException("The plan is for another projector raster than the scene's projector.", nameof(plan));
        _geometry = geometry;
        _plan = plan;
        _options = options ?? new SimOptions();
        CameraWidth = geometry.Width;
        CameraHeight = geometry.Height;
        var n = CameraWidth * CameraHeight;
        _samples = geometry.Supersample * geometry.Supersample;
        _direct = new float[n * _samples];
        _ambient = new float[n * _samples];
        _irradiance = new float[n * _samples];
        for (var i = 0; i < 256; i++)
            _projectorLut[i] = (float)Math.Pow(i / 255.0, _options.ProjectorGamma);
        for (var i = 0; i < _cameraLut.Length; i++)
            _cameraLut[i] = (float)(255 * Math.Pow(i / (double)(_cameraLut.Length - 1), 1 / _options.CameraGamma));
        for (var at = 0; at < _direct.Length; at++)
        {
            var albedo = Math.Clamp(geometry.SampleAlbedo[at] * _options.AlbedoScale, 0, 1);
            _ambient[at] = (float)(albedo * _options.Ambient);
            if (float.IsNaN(geometry.SampleRasterX[at]) || (_options.Shadows && geometry.SampleOccluded[at]))
                continue;
            _irradiance[at] = (float)(_options.ProjectorGain * geometry.SampleCos[at]);
            _direct[at] = (float)(albedo * _irradiance[at]);
        }
        Truth = new SimTruth(geometry);
        var region = plan.Region;
        for (var k = 0; k < n; k++)
        {
            if (geometry.Triangles[k] < 0)
                continue;
            var u = geometry.RasterX[k];
            if (float.IsNaN(u) || (_options.Shadows && geometry.Occluded[k]))
            {
                Truth.Shadowed[k] = !float.IsNaN(u) || geometry.Occluded[k];
                continue;
            }
            var v = geometry.RasterY[k];
            if (u >= region.X && v >= region.Y && u < region.Right && v < region.Bottom)
            {
                Truth.Lit[k] = true;
                Truth.ProjectorX[k] = u;
                Truth.ProjectorY[k] = v;
                double direct = 0, ambient = 0;
                for (var at = k * _samples; at < (k + 1) * _samples; at++)
                {
                    direct += _direct[at];
                    ambient += _ambient[at];
                }
                Truth.ExpectedContrast[k] = Record((direct + ambient) / _samples) - Record(ambient / _samples);
            }
        }
    }

    public SimScene Scene => _geometry.Scene;
    public SimGeometry Geometry => _geometry;
    public int CameraWidth { get; }
    public int CameraHeight { get; }
    public SimTruth Truth { get; }

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
        var sigma = (float)_options.NoiseSigma;
        var u = _geometry.SampleRasterX;
        var v = _geometry.SampleRasterY;
        var samples = _samples;
        Parallel.For(0, CameraHeight, y =>
        {
            var rng = new Pcg32(seed, (ulong)y);
            var spare = float.NaN;
            for (var x = 0; x < CameraWidth; x++)
            {
                var k = y * CameraWidth + x;
                double shade = 0;
                for (var at = k * samples; at < (k + 1) * samples; at++)
                {
                    shade += _ambient[at];
                    if (_direct[at] > 0)
                        shade += _direct[at] * SampleBilinear(light, pw, ph, u[at] - 0.5, v[at] - 0.5);
                }
                var level = Record(shade / samples);
                if (sigma > 0)
                {
                    // Box–Muller gives two independent normals per draw; the second is kept for the next pixel.
                    if (float.IsNaN(spare))
                    {
                        var u1 = Math.Max(rng.NextFloat(), 1e-7f);
                        var radius = MathF.Sqrt(-2 * MathF.Log(u1));
                        var angle = 2 * MathF.PI * rng.NextFloat();
                        level += sigma * radius * MathF.Cos(angle);
                        spare = radius * MathF.Sin(angle);
                    }
                    else
                    {
                        level += sigma * spare;
                        spare = float.NaN;
                    }
                }
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
            if (_geometry.Triangles[k] < 0)
                continue;
            double irradiance = 0;
            for (var at = k * _samples; at < (k + 1) * _samples; at++)
                irradiance += _irradiance[at];
            var light = (irradiance / _samples + _options.Ambient) * _options.AlbedoScale / 255.0;
            var colour = _geometry.Colour[k];
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
