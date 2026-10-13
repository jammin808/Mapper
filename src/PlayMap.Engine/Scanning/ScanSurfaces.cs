using System.Collections.Immutable;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Vision;
using PlayMap.Vision.Surfaces;
using PlayMap.Vision.Vectorise;

namespace PlayMap.Engine.Scanning;

/// <summary>What click-to-surface works from: the organised cloud on the camera's grid (with normals), the camera's
/// picture on the same grid, the scan mesh with the camera cell each vertex came from (its UVs are that grid,
/// normalised), and the camera and the first projector as solved (the smart scan is the first projector's view).</summary>
public sealed record ScanGeometry(PointCloud Cloud, ImageRgb8 Image, MeshData Mesh, int[] VertexCells, DeviceView Camera, DeviceView Projector);

/// <summary>A surface the scan suggests, for the desk's review: where it is on the smart-scan picture (0–1 across
/// and down), what it looks like, how sure the suggestion is, and its mask on the camera's grid.</summary>
public sealed record ScanRegion(string Id, string Label, ImmutableArray<string> Tags, double X, double Y, double W, double H, double Confidence,
    SegmentationMask Mask);

/// <summary>
/// The scan's surfaces for people: suggested regions with no click (the cloud's surfaces as its depth and crease
/// edges separate them, named by <see cref="SurfaceSuggestion"/>), and click-to-surface — a point of the smart-scan
/// picture is the first projector's ray, cast into the scan mesh and seen by the camera, where the segmenter grows
/// a region over the scanned surface (stopping at depth jumps); the region's mesh faces and its outline (pen-tool
/// paths on the mesh's UVs) make the surface.
/// </summary>
public static class ScanSurfaces
{
    /// <summary>The camera's white frame as an RGB picture (colour when the camera gave colour).</summary>
    public static ImageRgb8 CameraImage(CorrespondenceMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var image = ImageRgb8.Create(map.Width, map.Height);
        for (var k = 0; k < map.Width * map.Height; k++)
        {
            int r, g, b;
            if (map.WhiteColour is { } colour)
                (r, g, b) = ((colour[k] >> 16) & 0xFF, (colour[k] >> 8) & 0xFF, colour[k] & 0xFF);
            else
                r = g = b = map.White is { } white ? white[k] : (int)Math.Clamp(map.Contrast[k], 0, 255);
            image.Data[3 * k] = (byte)r;
            image.Data[3 * k + 1] = (byte)g;
            image.Data[3 * k + 2] = (byte)b;
        }
        return image;
    }

    /// <summary>The suggested regions, largest first (at most <paramref name="max"/>), each placed on the smart scan.</summary>
    public static IReadOnlyList<ScanRegion> Regions(ScanGeometry g, int max = 12)
    {
        ArgumentNullException.ThrowIfNull(g);
        var masks = SurfaceSuggestion.AutoMasks(g.Cloud, 0.01);
        var proposals = SurfaceSuggestion.Propose(masks.Take(max).ToList(), g.Cloud);
        var regions = new List<ScanRegion>();
        foreach (var p in proposals)
        {
            if (SmartRect(g, p.Mask) is not { } rect)
                continue;
            regions.Add(new ScanRegion("rg" + (regions.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), p.Name, p.Tags,
                rect.X, rect.Y, rect.W, rect.H, Math.Round(Math.Clamp(p.Confidence, 0, 1), 3), p.Mask));
        }
        return regions;
    }

    /// <summary>Where a mask lies on the smart scan: its points seen by the first projector, normalised, the 2nd to
    /// the 98th percentile each way (a stray point does not stretch the box). Null when none of it is lit.</summary>
    public static (double X, double Y, double W, double H)? SmartRect(ScanGeometry g, SegmentationMask mask)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(mask);
        var xs = new List<double>();
        var ys = new List<double>();
        var k = g.Projector.Intrinsics;
        for (var i = 0; i < mask.Coverage.Length; i++)
        {
            if (mask.Coverage[i] < 128 || !PointCloud.IsValid(g.Cloud.Points[i]) || g.Projector.Project(g.Cloud.Points[i]) is not { } q)
                continue;
            if (q.X < 0 || q.Y < 0 || q.X > k.Width || q.Y > k.Height)
                continue;
            xs.Add(q.X / k.Width);
            ys.Add(q.Y / k.Height);
        }
        if (xs.Count < 4)
            return null;
        xs.Sort();
        ys.Sort();
        double At(List<double> v, double f) => v[(int)Math.Clamp(Math.Round(f * (v.Count - 1)), 0, v.Count - 1)];
        var (x0, x1, y0, y1) = (At(xs, 0.02), At(xs, 0.98), At(ys, 0.02), At(ys, 0.98));
        return (Math.Round(x0, 4), Math.Round(y0, 4), Math.Round(Math.Max(x1 - x0, 1e-4), 4), Math.Round(Math.Max(y1 - y0, 1e-4), 4));
    }

    /// <summary>The camera pixel that sees what the first projector lights at (x, y) on the smart scan (0–1): its ray
    /// cast into the scan mesh, the hit seen by the camera. Null where the projector lights nothing scanned.</summary>
    public static Vec2d? CameraPixelAt(ScanGeometry g, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(g);
        var k = g.Projector.Intrinsics;
        var ray = g.Projector.Ray(new Vec2d(x * k.Width, y * k.Height));
        if (new MeshRaycaster(g.Mesh).Cast(ray) is not { } hit || g.Camera.Project(hit.Point) is not { } pixel)
            return null;
        var c = g.Camera.Intrinsics;
        return pixel.X >= 0 && pixel.Y >= 0 && pixel.X < c.Width && pixel.Y < c.Height ? pixel : null;
    }

    /// <summary>The scan mesh's triangles whose three vertices lie in the mask.</summary>
    public static int[] Faces(ScanGeometry g, SegmentationMask mask)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(mask);
        var faces = new List<int>();
        var idx = g.Mesh.Indices;
        for (var t = 0; t < idx.Length / 3; t++)
        {
            if (In(idx[3 * t]) && In(idx[3 * t + 1]) && In(idx[3 * t + 2]))
                faces.Add(t);
        }
        return [.. faces];

        bool In(uint v) => mask.Coverage[g.VertexCells[v]] >= 128;
    }

    /// <summary>The mask's outline as the surface's masks, on the mesh's UVs (the camera grid normalised, top-left,
    /// v down): each outline adds, each hole subtracts. Ids come from <paramref name="newId"/>.</summary>
    public static ImmutableArray<Mask> Masks(SegmentationMask mask, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(newId);
        var paths = MaskToPath.Vectorise(mask);
        var masks = ImmutableArray.CreateBuilder<Mask>();
        var n = 0;
        foreach (var outline in paths.Outlines)
            masks.Add(new Mask(newId(), n++ == 0 ? "Outline" : "Outline " + n.ToString(System.Globalization.CultureInfo.InvariantCulture), outline, MaskMode.Add, 0.002, false, true));
        var h = 0;
        foreach (var hole in paths.Holes)
            masks.Add(new Mask(newId(), "Hole " + (++h).ToString(System.Globalization.CultureInfo.InvariantCulture), hole, MaskMode.Subtract, 0.002, false, true));
        return masks.ToImmutable();
    }
}
