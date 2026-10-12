using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>How a box's six faces share the UV square.</summary>
public enum BoxUvLayout
{
    /// <summary>Every face fills the whole square (one picture on each face).</summary>
    PerFace,
    /// <summary>The unfolded cross of a 4 × 3 picture: left, front, right, back across the middle row, the
    /// top above the front and the bottom below it, so neighbouring faces share their edges.</summary>
    Cross,
}

/// <summary>How a dome's UVs read a picture.</summary>
public enum DomeUv
{
    /// <summary>An angular fisheye (a dome master): the zenith at the centre, the rim on the circle of
    /// radius ½, the front (+Z) at the bottom and +X to the right as seen from above.</summary>
    Fisheye,
    /// <summary>Longitude across (the front at u = ½), zenith to rim down.</summary>
    Equirect,
}

/// <summary>Meshes made from parameters: plane, box, cylinder, sphere, dome — each with outward normals
/// (the side a projector lights), counter-clockwise front faces, and UVs with (0, 0) at the top-left of
/// the content and v growing downward (the picture's own rows). Sizes are metres; every solid is centred
/// on the origin with +Y up and its front facing +Z.</summary>
public static class Primitives
{
    public static readonly ImmutableArray<string> Kinds = ["plane", "box", "cylinder", "sphere", "dome"];

    /// <summary>A primitive from its kind and parameters ("width", "height", "depth", "radius",
    /// "segments", "rings", "caps", "angle", "uv", "inside", "segmentsX", "segmentsY"); missing ones take
    /// their defaults, out-of-range ones are clamped. Throws for an unknown kind.</summary>
    public static MeshData Create(string kind, ParamSet parameters)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(parameters);
        var p = parameters;
        switch (kind.Trim().ToLowerInvariant())
        {
            case "plane":
                var seg = p.GetInt("segments", 1);
                return Plane(p.GetDouble("width", 1), p.GetDouble("height", 1), p.GetInt("segmentsX", seg), p.GetInt("segmentsY", seg));
            case "box":
                return Box(p.GetDouble("width", 1), p.GetDouble("height", 1), p.GetDouble("depth", 1),
                    string.Equals(p.GetString("uv", "faces"), "cross", StringComparison.OrdinalIgnoreCase) ? BoxUvLayout.Cross : BoxUvLayout.PerFace);
            case "cylinder":
                return Cylinder(p.GetDouble("radius", 0.5), p.GetDouble("height", 1), p.GetInt("segments", 32), p.GetBool("caps", true));
            case "sphere":
                var sphereSegments = p.GetInt("segments", 32);
                return Sphere(p.GetDouble("radius", 0.5), sphereSegments, p.GetInt("rings", Math.Max(2, sphereSegments / 2)));
            case "dome":
                return Dome(p.GetDouble("radius", 1), p.GetDouble("angle", 180), p.GetInt("segments", 48), p.GetInt("rings", 16),
                    string.Equals(p.GetString("uv", "fisheye"), "equirect", StringComparison.OrdinalIgnoreCase) ? DomeUv.Equirect : DomeUv.Fisheye,
                    p.GetBool("inside", false));
            default:
                throw new ArgumentException($"There is no primitive called \"{kind}\"; the primitives are {string.Join(", ", Kinds)}.", nameof(kind));
        }
    }

    /// <summary>The mesh of a primitive asset (<see cref="MeshOrigin.Primitive"/>).</summary>
    public static MeshData FromAsset(MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Create(asset.Primitive, asset.PrimitiveParams);
    }

    /// <summary>A flat rectangle standing in the XY plane, facing +Z, with segmentsX × segmentsY quads.</summary>
    public static MeshData Plane(double width = 1, double height = 1, int segmentsX = 1, int segmentsY = 1)
    {
        var b = new Builder();
        var w = Size(width);
        var h = Size(height);
        b.Grid(new Vec3d(-w / 2, h / 2, 0), new Vec3d(w, 0, 0), new Vec3d(0, -h, 0), Segments(segmentsX, 1), Segments(segmentsY, 1), 0, 0, 1, 1);
        return b.ToMesh();
    }

    /// <summary>A box of six flat faces (hard edges: each face has its own vertices).</summary>
    public static MeshData Box(double width = 1, double height = 1, double depth = 1, BoxUvLayout layout = BoxUvLayout.PerFace)
    {
        var hx = Size(width) / 2;
        var hy = Size(height) / 2;
        var hz = Size(depth) / 2;
        var b = new Builder();
        // Each face: its top-left corner seen from outside, its right and down edges; normal = down × right.
        // Cross cells (column, row) in a 4 × 3 grid.
        void Face(Vec3d origin, Vec3d right, Vec3d down, int col, int row)
        {
            if (layout == BoxUvLayout.Cross)
                b.Grid(origin, right, down, 1, 1, col / 4.0, row / 3.0, 0.25, 1 / 3.0);
            else
                b.Grid(origin, right, down, 1, 1, 0, 0, 1, 1);
        }
        Face(new Vec3d(-hx, hy, hz), new Vec3d(2 * hx, 0, 0), new Vec3d(0, -2 * hy, 0), 1, 1);  // front +Z
        Face(new Vec3d(hx, hy, hz), new Vec3d(0, 0, -2 * hz), new Vec3d(0, -2 * hy, 0), 2, 1);  // right +X
        Face(new Vec3d(hx, hy, -hz), new Vec3d(-2 * hx, 0, 0), new Vec3d(0, -2 * hy, 0), 3, 1); // back −Z
        Face(new Vec3d(-hx, hy, -hz), new Vec3d(0, 0, 2 * hz), new Vec3d(0, -2 * hy, 0), 0, 1); // left −X
        Face(new Vec3d(-hx, hy, -hz), new Vec3d(2 * hx, 0, 0), new Vec3d(0, 0, 2 * hz), 1, 0);  // top +Y
        Face(new Vec3d(-hx, -hy, hz), new Vec3d(2 * hx, 0, 0), new Vec3d(0, 0, -2 * hz), 1, 2); // bottom −Y
        return b.ToMesh();
    }

    /// <summary>A cylinder about the Y axis: the side's u runs once round (the seam at the back, the front
    /// at u = ½), v from top to bottom; the caps are discs with planar UVs (seen from above the front is at
    /// the bottom; seen from below, at the top).</summary>
    public static MeshData Cylinder(double radius = 0.5, double height = 1, int segments = 32, bool caps = true)
    {
        var r = Size(radius);
        var hh = Size(height) / 2;
        var n = Segments(segments, 3);
        var b = new Builder();
        var top = new int[n + 1];
        var bottom = new int[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var u = i / (double)n;
            var (s, c) = Math.SinCos(2 * Math.PI * (u - 0.5));
            var normal = new Vec3d(s, 0, c);
            top[i] = b.Vertex(new Vec3d(r * s, hh, r * c), normal, u, 0);
            bottom[i] = b.Vertex(new Vec3d(r * s, -hh, r * c), normal, u, 1);
        }
        for (var i = 0; i < n; i++)
            b.Quad(top[i], top[i + 1], bottom[i + 1], bottom[i]);
        if (caps)
        {
            var ct = b.Vertex(new Vec3d(0, hh, 0), Vec3d.UnitY, 0.5, 0.5);
            var cb = b.Vertex(new Vec3d(0, -hh, 0), -Vec3d.UnitY, 0.5, 0.5);
            var ringTop = new int[n];
            var ringBottom = new int[n];
            for (var i = 0; i < n; i++)
            {
                var (s, c) = Math.SinCos(2 * Math.PI * (i / (double)n - 0.5));
                ringTop[i] = b.Vertex(new Vec3d(r * s, hh, r * c), Vec3d.UnitY, 0.5 + 0.5 * s, 0.5 + 0.5 * c);
                ringBottom[i] = b.Vertex(new Vec3d(r * s, -hh, r * c), -Vec3d.UnitY, 0.5 + 0.5 * s, 0.5 - 0.5 * c);
            }
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                b.Triangle(ct, ringTop[i], ringTop[j]);
                b.Triangle(cb, ringBottom[j], ringBottom[i]);
            }
        }
        return b.ToMesh();
    }

    /// <summary>A UV sphere: u is longitude (the seam at the back, the front at u = ½), v latitude from the
    /// north pole (0) to the south (1) — an equirectangular picture wraps it.</summary>
    public static MeshData Sphere(double radius = 0.5, int segments = 32, int rings = 16) =>
        Cap(Size(radius), Math.PI, Segments(segments, 3), Segments(rings, 2), (u, theta) => new Vec2d(u, theta / Math.PI), inside: false);

    /// <summary>A dome: the cap of a sphere of <paramref name="radius"/> about the zenith (+Y), opening
    /// <paramref name="angleDegrees"/> across (180 = a hemisphere, up to 360), its centre at the origin.
    /// <paramref name="inside"/> turns the normals and the faces inward, for a dome lit from within.</summary>
    public static MeshData Dome(double radius = 1, double angleDegrees = 180, int segments = 48, int rings = 16,
        DomeUv uv = DomeUv.Fisheye, bool inside = false)
    {
        var thetaMax = Math.Clamp(angleDegrees, 1, 360) * Math.PI / 360;
        Func<double, double, Vec2d> map = uv == DomeUv.Fisheye
            ? (u, theta) =>
            {
                var rho = 0.5 * theta / thetaMax;
                var (s, c) = Math.SinCos(2 * Math.PI * (u - 0.5));
                return new Vec2d(0.5 + rho * s, 0.5 + rho * c);
            }
            : (u, theta) => new Vec2d(u, theta / thetaMax);
        return Cap(Size(radius), thetaMax, Segments(segments, 3), Segments(rings, 1), map, inside);
    }

    /// <summary>The spherical cap θ ∈ [0, θmax] from the zenith, φ once round: rows of latitude, columns
    /// of longitude, the seam column duplicated, the zenith row's degenerate triangles dropped.</summary>
    private static MeshData Cap(double r, double thetaMax, int segments, int rings, Func<double, double, Vec2d> uv, bool inside)
    {
        var b = new Builder();
        var idx = new int[(rings + 1) * (segments + 1)];
        for (var j = 0; j <= rings; j++)
        {
            var theta = thetaMax * j / rings;
            var (st, ct) = Math.SinCos(theta);
            for (var i = 0; i <= segments; i++)
            {
                var u = i / (double)segments;
                var (sp, cp) = Math.SinCos(2 * Math.PI * (u - 0.5));
                var d = new Vec3d(st * sp, ct, st * cp);
                var t = uv(u, theta);
                idx[j * (segments + 1) + i] = b.Vertex(d * r, inside ? -d : d, t.X, t.Y);
            }
        }
        for (var j = 0; j < rings; j++)
            for (var i = 0; i < segments; i++)
            {
                var a = idx[j * (segments + 1) + i];
                var bb = idx[j * (segments + 1) + i + 1];
                var c = idx[(j + 1) * (segments + 1) + i + 1];
                var d = idx[(j + 1) * (segments + 1) + i];
                // Row 0 is the zenith: its top edge is one point, so only the lower triangle has area.
                // The last row of a full sphere is the nadir: only the upper triangle has area.
                var topDegenerate = j == 0;
                var bottomDegenerate = j == rings - 1 && thetaMax >= Math.PI - 1e-12;
                if (!bottomDegenerate)
                    b.Triangle(a, d, c, inside);
                if (!topDegenerate)
                    b.Triangle(a, c, bb, inside);
            }
        return b.ToMesh();
    }

    private static double Size(double v) => double.IsFinite(v) ? Math.Max(Math.Abs(v), 1e-4) : 1;

    private static int Segments(int v, int min) => Math.Clamp(v, min, 1024);

    /// <summary>Accumulates a mesh; faces are wound counter-clockwise about their normal.</summary>
    private sealed class Builder
    {
        private readonly List<float> _pos = [];
        private readonly List<float> _nor = [];
        private readonly List<float> _uv = [];
        private readonly List<uint> _idx = [];

        public int Vertex(Vec3d p, Vec3d n, double u, double v)
        {
            var i = _pos.Count / 3;
            _pos.Add((float)p.X);
            _pos.Add((float)p.Y);
            _pos.Add((float)p.Z);
            _nor.Add((float)n.X);
            _nor.Add((float)n.Y);
            _nor.Add((float)n.Z);
            _uv.Add((float)u);
            _uv.Add((float)v);
            return i;
        }

        public void Triangle(int first, int second, int third, bool flip = false)
        {
            _idx.Add((uint)first);
            _idx.Add((uint)(flip ? third : second));
            _idx.Add((uint)(flip ? second : third));
        }

        /// <summary>A quad seen from outside: a top-left, b top-right, c bottom-right, d bottom-left; as two
        /// triangles a-d-c and a-c-b, both counter-clockwise about the outward normal.</summary>
        public void Quad(int a, int b, int c, int d)
        {
            Triangle(a, d, c);
            Triangle(a, c, b);
        }

        /// <summary>A flat grid from its top-left corner along right and down (normal = down × right), its
        /// UVs filling the rectangle (u0, v0, du, dv).</summary>
        public void Grid(Vec3d origin, Vec3d right, Vec3d down, int sx, int sy, double u0, double v0, double du, double dv)
        {
            var normal = Vec3d.Cross(down, right).Normalized();
            var first = _pos.Count / 3;
            for (var j = 0; j <= sy; j++)
                for (var i = 0; i <= sx; i++)
                {
                    var s = i / (double)sx;
                    var t = j / (double)sy;
                    Vertex(origin + right * s + down * t, normal, u0 + du * s, v0 + dv * t);
                }
            for (var j = 0; j < sy; j++)
                for (var i = 0; i < sx; i++)
                {
                    var a = first + j * (sx + 1) + i;
                    Quad(a, a + 1, a + sx + 2, a + sx + 1);
                }
        }

        public MeshData ToMesh() => new([.. _pos], [.. _nor], [.. _uv], [.. _idx]);
    }
}
