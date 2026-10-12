using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>Primitives face outward with valid UVs and the right size; projected UVs keep a region's aspect
/// and read upright.</summary>
public class PrimitivesAndUvTests
{
    private static Vec3d FaceNormal(MeshData m, int t)
    {
        var (a, b, c) = m.Triangle(t);
        return Vec3d.Cross(m.Position(b) - m.Position(a), m.Position(c) - m.Position(a));
    }

    private static double Area(MeshData m) => Enumerable.Range(0, m.TriangleCount).Sum(t => FaceNormal(m, t).Length / 2);

    [Fact]
    public void EveryPrimitiveFacesOutwardWithWindingThatAgreesAndUvsInTheSquare()
    {
        foreach (var kind in Primitives.Kinds)
        {
            var mesh = Primitives.Create(kind, ParamSet.Empty);
            Assert.True(mesh.TriangleCount > 0, kind);
            Assert.True(mesh.HasNormals && mesh.HasUvs, kind);
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var fn = FaceNormal(mesh, t);
                Assert.True(fn.Length > 1e-9, $"{kind}: triangle {t} has no area");
                var (a, b, c) = mesh.Triangle(t);
                var vn = mesh.Normal(a) + mesh.Normal(b) + mesh.Normal(c);
                Assert.True(Vec3d.Dot(fn, vn) > 0, $"{kind}: triangle {t} is wound against its normals");
                var centroid = (mesh.Position(a) + mesh.Position(b) + mesh.Position(c)) / 3;
                var outward = kind == "plane" ? Vec3d.UnitZ : centroid;
                Assert.True(Vec3d.Dot(fn, outward) > 0, $"{kind}: triangle {t} faces inward");
            }
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                Assert.Equal(1, mesh.Normal(i).Length, 5);
                var uv = mesh.Uv(i);
                Assert.InRange(uv.X, -1e-6, 1 + 1e-6);
                Assert.InRange(uv.Y, -1e-6, 1 + 1e-6);
            }
        }
    }

    [Fact]
    public void PrimitivesHaveTheirSizesAndSurfaceAreas()
    {
        var plane = Primitives.Plane(1.6, 0.9, 4, 3);
        Assert.Equal(1.6 * 0.9, Area(plane), 5);
        Assert.Equal(20, plane.VertexCount);

        var box = Primitives.Create("box", ParamSet.Empty.With("width", 2.0).With("height", 1.0).With("depth", 0.5));
        var (min, max) = box.Bounds();
        Assert.Equal(new Vec3d(-1, -0.5, -0.25), min);
        Assert.Equal(new Vec3d(1, 0.5, 0.25), max);
        Assert.Equal(2 * (2 * 1 + 1 * 0.5 + 2 * 0.5), Area(box), 5);

        var sphere = Primitives.Sphere(2, 96, 48);
        Assert.All(Enumerable.Range(0, sphere.VertexCount), i => Assert.Equal(2, sphere.Position(i).Length, 5));
        Assert.Equal(4 * Math.PI * 4, Area(sphere), 4 * Math.PI * 4 * 0.002);

        var dome = Primitives.Dome(3, 180, 96, 32);
        Assert.Equal(2 * Math.PI * 9, Area(dome), 2 * Math.PI * 9 * 0.003);
        Assert.True(dome.Bounds().Min.Y > -1e-6);

        var cylinder = Primitives.Cylinder(0.5, 2, 128, caps: true);
        Assert.Equal(2 * Math.PI * 0.5 * 2 + 2 * Math.PI * 0.25, Area(cylinder), 0.01);

        Assert.Throws<ArgumentException>(() => Primitives.Create("teapot", ParamSet.Empty));
    }

    [Fact]
    public void ABoxUnfoldedAsACrossSharesUvsAlongTheEdgesItsFacesShare()
    {
        var box = Primitives.Box(1, 1, 1, BoxUvLayout.Cross);
        // The front face (normal +Z) fills the cross's middle cell.
        for (var i = 0; i < box.VertexCount; i++)
        {
            if (box.Normal(i).Z < 0.99)
                continue;
            Assert.InRange(box.Uv(i).X, 0.25 - 1e-6, 0.5 + 1e-6);
            Assert.InRange(box.Uv(i).Y, 1 / 3.0 - 1e-6, 2 / 3.0 + 1e-6);
        }
        // Where the front meets the right, and the front meets the top, the two faces' vertices share UVs.
        AssertSharedEdge(box, Vec3d.UnitZ, Vec3d.UnitX);
        AssertSharedEdge(box, Vec3d.UnitZ, Vec3d.UnitY);
        AssertSharedEdge(box, Vec3d.UnitZ, -Vec3d.UnitX);
        AssertSharedEdge(box, Vec3d.UnitZ, -Vec3d.UnitY);
        AssertSharedEdge(box, Vec3d.UnitX, -Vec3d.UnitZ);
    }

    private static void AssertSharedEdge(MeshData box, Vec3d faceA, Vec3d faceB)
    {
        var matched = 0;
        for (var i = 0; i < box.VertexCount; i++)
            for (var j = 0; j < box.VertexCount; j++)
            {
                if ((box.Normal(i) - faceA).Length > 1e-6 || (box.Normal(j) - faceB).Length > 1e-6)
                    continue;
                if ((box.Position(i) - box.Position(j)).Length > 1e-6)
                    continue;
                Assert.Equal(box.Uv(i).X, box.Uv(j).X, 6);
                Assert.Equal(box.Uv(i).Y, box.Uv(j).Y, 6);
                matched++;
            }
        Assert.Equal(2, matched);
    }

    [Fact]
    public void ADomeLitFromWithinFacesItsCentreAndItsFisheyeHasTheZenithInTheMiddle()
    {
        var inside = Primitives.Dome(2, 180, 32, 8, DomeUv.Fisheye, inside: true);
        for (var t = 0; t < inside.TriangleCount; t++)
        {
            var (a, b, c) = inside.Triangle(t);
            var centroid = (inside.Position(a) + inside.Position(b) + inside.Position(c)) / 3;
            Assert.True(Vec3d.Dot(FaceNormal(inside, t), centroid) < 0);
        }
        for (var i = 0; i < inside.VertexCount; i++)
        {
            var p = inside.Position(i);
            var uv = inside.Uv(i);
            var theta = Math.Acos(Math.Clamp(p.Y / 2, -1, 1));
            // Angular fisheye: the distance from the centre is θ / θmax × ½.
            Assert.Equal(theta / (Math.PI / 2) * 0.5, (uv - new Vec2d(0.5, 0.5)).Length, 4);
            if (Math.Abs(p.Y) < 1e-6 && p.Z > 1.99)
                Assert.Equal(new Vec2d(0.5, 1), new Vec2d(Math.Round(uv.X, 5), Math.Round(uv.Y, 5)));
        }
    }

    /// <summary>A scanned rectangle: a grid of points on a tilted plane, turned in the plane, with noise
    /// off the plane — what a region grown on a scan looks like.</summary>
    private static MeshData ScannedRectangle(double width, double height, double turnDegrees, Vec3d normal, ulong seed)
    {
        normal = normal.Normalized();
        var refUp = Math.Abs(normal.Y) < 0.9 ? Vec3d.UnitY : Vec3d.UnitZ;
        var up0 = (refUp - normal * Vec3d.Dot(refUp, normal)).Normalized();
        var right0 = Vec3d.Cross(up0, normal).Normalized();
        var (s, c) = Math.SinCos(turnDegrees * Math.PI / 180);
        var right = right0 * c + up0 * s;
        var up = Vec3d.Cross(normal, right);
        var rng = new Pcg32(seed);
        const int nx = 24, ny = 13;
        var pos = new List<float>();
        var origin = new Vec3d(3, 1.5, -2);
        for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
            {
                var p = origin + right * (width * i / (nx - 1)) + up * (height * j / (ny - 1)) + normal * rng.Range(-0.002, 0.002);
                pos.AddRange([(float)p.X, (float)p.Y, (float)p.Z]);
            }
        var idx = new List<uint>();
        for (var j = 0; j < ny - 1; j++)
            for (var i = 0; i < nx - 1; i++)
            {
                var a = (uint)(j * nx + i);
                // Rows run up the plane, so this winding faces +normal.
                idx.AddRange([a, a + 1, a + nx + 1, a, a + nx + 1, a + nx]);
            }
        return new MeshData([.. pos], [], [], [.. idx]);
    }

    [Fact]
    public void PlanarUvsOfAScannedRectangleRegionPreserveItsAspectAndReadUpright()
    {
        var mesh = ScannedRectangle(4, 2, 25, new Vec3d(0.3, 0.1, 1), 31);
        var proj = UvMapping.Planar(mesh);
        Assert.Equal(4, proj.Width, 0.02);
        Assert.Equal(2, proj.Height, 0.02);
        Assert.Equal(2, proj.Aspect, 0.02);
        var uvs = Enumerable.Range(0, proj.Mesh.VertexCount).Select(proj.Mesh.Uv).ToList();
        Assert.Equal(1, uvs.Max(u => u.X) - uvs.Min(u => u.X), 6);
        Assert.Equal(0.5, uvs.Max(u => u.Y) - uvs.Min(u => u.Y), 0.01);
        // One scale on both axes: UV distances are surface distances over the longer side.
        var rng = new Pcg32(37);
        for (var k = 0; k < 100; k++)
        {
            var i = rng.NextInt(proj.Mesh.VertexCount);
            var j = rng.NextInt(proj.Mesh.VertexCount);
            Assert.Equal((proj.Mesh.Position(i) - proj.Mesh.Position(j)).Length, (proj.Mesh.Uv(i) - proj.Mesh.Uv(j)).Length * 4, 0.02);
        }
        // Upright: the highest point of the region has the smallest v.
        var top = Enumerable.Range(0, mesh.VertexCount).MaxBy(i => mesh.Position(i).Y);
        Assert.True(proj.Mesh.Uv(top).Y < 0.25 + 0.05);
        var stretched = UvMapping.Planar(mesh, fit: UvFit.Stretch);
        Assert.Equal(1, Enumerable.Range(0, stretched.Mesh.VertexCount).Max(i => stretched.Mesh.Uv(i).Y), 6);
    }

    [Fact]
    public void PlanarUvsLineASquareUpWithItsSidesAndAFloorReadsTowardTheFront()
    {
        var wall = UvMapping.Planar(Primitives.Plane(2, 2, 3, 3));
        var plane = Primitives.Plane(2, 2, 3, 3);
        for (var i = 0; i < plane.VertexCount; i++)
        {
            Assert.Equal(plane.Uv(i).X, wall.Mesh.Uv(i).X, 5);
            Assert.Equal(plane.Uv(i).Y, wall.Mesh.Uv(i).Y, 5);
        }
        // The box's top face is a floor seen from above: v grows toward +Z, u toward +X.
        var box = Primitives.Box(1, 1, 1);
        var topFaces = Enumerable.Range(0, box.TriangleCount).Where(t => FaceNormal(box, t).Y > 0.5).ToList();
        var floor = UvMapping.Planar(box, topFaces);
        var front = Enumerable.Range(0, floor.Mesh.VertexCount).MaxBy(i => floor.Mesh.Position(i).Z + 0.1 * floor.Mesh.Position(i).X);
        Assert.Equal(new Vec2d(1, 1), new Vec2d(Math.Round(floor.Mesh.Uv(front).X, 5), Math.Round(floor.Mesh.Uv(front).Y, 5)));
    }

    [Fact]
    public void BoxAndCylindricalUvsKeepTheSurfacesProportions()
    {
        var box = UvMapping.Box(Primitives.Box(2, 1, 1));
        Assert.Equal(2, box.Width, 9);
        for (var i = 0; i < box.Mesh.VertexCount; i++)
        {
            Assert.InRange(box.Mesh.Uv(i).X, -1e-6, 1 + 1e-6);
            Assert.InRange(box.Mesh.Uv(i).Y, -1e-6, 0.5 + 1e-6);
        }

        // The side of a column: u runs once round (2πr), v down its height; no triangle spans the cut.
        var cylinder = Primitives.Cylinder(0.5, 3, 64, caps: false);
        var col = UvMapping.Cylindrical(cylinder);
        Assert.Equal(2 * Math.PI * 0.5, col.Width, 0.01);
        Assert.Equal(3, col.Height, 1e-6);
        for (var t = 0; t < col.Mesh.TriangleCount; t++)
        {
            var (a, b, c) = col.Mesh.Triangle(t);
            var us = new[] { col.Mesh.Uv(a).X, col.Mesh.Uv(b).X, col.Mesh.Uv(c).X };
            Assert.True(us.Max() - us.Min() < 0.1, $"triangle {t} spans the cut");
        }

        // A quarter of a round wall: the arc's length across, unrolled flat.
        var quarter = Enumerable.Range(0, cylinder.TriangleCount)
            .Where(t => { var (a, _, _) = cylinder.Triangle(t); var p = cylinder.Position(a); return p.Z > 0 && p.X >= -1e-6 && p.X < 0.5 * Math.Sin(Math.PI / 2) - 1e-3; })
            .ToList();
        var arc = UvMapping.Cylindrical(cylinder, quarter);
        Assert.InRange(arc.Width, 0.5 * Math.PI / 2 * 0.9, 0.5 * Math.PI / 2 * 1.01);
    }
}
