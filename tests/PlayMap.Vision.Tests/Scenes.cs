using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Tests;

/// <summary>A box standing on a floor, all of one colour, seen by a camera from above and to the side: the image,
/// the organised points the scan would give (ray cast per pixel centre), the face under every pixel, and the
/// mesh. Faces: 0 floor, 1 box top, 2…5 box sides (+x, −x, +z, −z); −1 where nothing was hit.</summary>
internal sealed class BoxScene
{
    public const int Floor = 0;
    public const int Top = 1;

    public required ImageRgb8 Image { get; init; }
    public required PointCloud Points { get; init; }
    public required int[] Face { get; init; }
    public required int[] Triangle { get; init; }
    public required MeshData Mesh { get; init; }
    public required Intrinsics Intrinsics { get; init; }
    public required Pose Pose { get; init; }

    public VisionFrame Frame => new(Image, Points);

    public bool[] FaceMask(int face) => Face.Select(f => f == face).ToArray();

    public Vec2d Project(Vec3d world) => CameraModel.Project(world, Intrinsics, LensDistortion.None, Pose)!.Value;

    /// <summary>Box half-extents 0.25 × 0.4/2 with height 0.3, floor 3 m square.</summary>
    public static readonly Vec3d BoxMin = new(-0.25, 0, -0.2);
    public static readonly Vec3d BoxMax = new(0.25, 0.3, 0.2);

    public static BoxScene Build(int w = 160, int h = 120, double depthNoise = 0.0005, int imageNoise = 2, ulong seed = 1)
    {
        var mesh = BuildMesh(out var triFace);
        var k = Intrinsics.FromFieldOfView(w, h, 60);
        var pose = Pose.LookAt(new Vec3d(0.6, 1.3, 1.4), new Vec3d(0, 0.1, 0), Vec3d.UnitY);
        var caster = new MeshRaycaster(mesh);
        var rng = new Pcg32(seed);
        var points = new Vec3d[w * h];
        var face = new int[w * h];
        var tri = new int[w * h];
        var img = ImageRgb8.Create(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var ray = CameraModel.PixelRay(new Vec2d(x + 0.5, y + 0.5), k, LensDistortion.None, pose);
                var hit = caster.Cast(ray);
                if (hit is { } hv)
                {
                    // Range noise along the ray, as a triangulated scan has.
                    points[i] = ray.At(hv.Distance + rng.NextGaussian() * depthNoise);
                    face[i] = triFace[hv.Triangle];
                    tri[i] = hv.Triangle;
                }
                else
                {
                    points[i] = PointCloud.Invalid;
                    face[i] = -1;
                    tri[i] = -1;
                }
                var row = img.MutableRow(y);
                row[x * 3] = (byte)(180 + rng.NextInt(2 * imageNoise + 1) - imageNoise);
                row[x * 3 + 1] = (byte)(170 + rng.NextInt(2 * imageNoise + 1) - imageNoise);
                row[x * 3 + 2] = (byte)(160 + rng.NextInt(2 * imageNoise + 1) - imageNoise);
            }
        }
        return new BoxScene
        {
            Image = img,
            Points = new PointCloud(points, gridWidth: w, gridHeight: h),
            Face = face,
            Triangle = tri,
            Mesh = mesh,
            Intrinsics = k,
            Pose = pose,
        };
    }

    private static MeshData BuildMesh(out int[] triFace)
    {
        var pos = new List<float>();
        var idx = new List<uint>();
        var faces = new List<int>();
        void Quad(Vec3d a, Vec3d b, Vec3d c, Vec3d d, int f)
        {
            var o = (uint)(pos.Count / 3);
            foreach (var v in new[] { a, b, c, d })
                pos.AddRange([(float)v.X, (float)v.Y, (float)v.Z]);
            idx.AddRange([o, o + 1, o + 2, o, o + 2, o + 3]);
            faces.Add(f);
            faces.Add(f);
        }
        Quad(new(-1.5, 0, -1.5), new(-1.5, 0, 1.5), new(1.5, 0, 1.5), new(1.5, 0, -1.5), Floor);
        var (x0, y0, z0) = (BoxMin.X, BoxMin.Y, BoxMin.Z);
        var (x1, y1, z1) = (BoxMax.X, BoxMax.Y, BoxMax.Z);
        Quad(new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), Top);
        Quad(new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1), 2);
        Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), 3);
        Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), 4);
        Quad(new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(x1, y0, z0), 5);
        triFace = [.. faces];
        return new MeshData([.. pos], [], [], [.. idx]);
    }
}

/// <summary>Small helpers for picture-only scenes.</summary>
internal static class Pictures
{
    public static ImageRgb8 Filled(int w, int h, byte r, byte g, byte b)
    {
        var img = ImageRgb8.Create(w, h);
        for (var i = 0; i < w * h; i++)
        {
            img.Data[i * 3] = r;
            img.Data[i * 3 + 1] = g;
            img.Data[i * 3 + 2] = b;
        }
        return img;
    }

    public static void Rect(ImageRgb8 img, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        for (var y = y0; y < y1; y++)
        {
            var row = img.MutableRow(y);
            for (var x = x0; x < x1; x++)
            {
                row[x * 3] = r;
                row[x * 3 + 1] = g;
                row[x * 3 + 2] = b;
            }
        }
    }

    public static void Noise(ImageRgb8 img, int amplitude, ulong seed)
    {
        var rng = new Pcg32(seed);
        for (var i = 0; i < img.Width * img.Height * 3; i++)
            img.Data[i] = (byte)Math.Clamp(img.Data[i] + rng.NextInt(2 * amplitude + 1) - amplitude, 0, 255);
    }
}
