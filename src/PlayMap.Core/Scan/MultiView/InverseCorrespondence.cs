using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>How a view's decode is inverted onto its projector's raster.</summary>
public sealed record InverseOptions
{
    public static readonly InverseOptions Default = new();

    /// <summary>The track grid's spacing in projector pixels: nodes sit at ((i + ½)·s, (j + ½)·s).</summary>
    public int Stride { get; init; } = 16;

    /// <summary>Decoded camera pixels whose projector coordinate lies within this many projector pixels of the
    /// asked point enter its local fit.</summary>
    public double RadiusPx { get; init; } = 3;

    /// <summary>The nearest of them must lie within this many projector pixels of the asked point: a band of the
    /// projector's raster the view did not see (a fold, such as a top the camera looks up at) is never bridged.</summary>
    public double MaxGapPx { get; init; } = 1.5;

    /// <summary>The pixels must fill at least this share of the disc: the count the local fit's own scale predicts
    /// for a disc of <see cref="RadiusPx"/> seen whole (a rim or a silhouette leaves it half empty).</summary>
    public double MinFill { get; init; } = 0.5;

    /// <summary>A point needs at least this many such pixels.</summary>
    public int MinSamples { get; init; } = 6;

    /// <summary>Pixels decoded with less confidence than this are not used.</summary>
    public float MinConfidence { get; init; } = 0.3f;

    /// <summary>A local fit whose residual is larger than this (camera pixels) spans a depth edge or a misdecode,
    /// and the point is not answered.</summary>
    public double MaxFitResidualPx { get; init; } = 0.5;
}

/// <summary>Where a view saw one projector pixel: the camera position, the mean confidence of the decoded pixels
/// that placed it, the local fit's residual (camera pixels) and how many pixels it used.</summary>
public readonly record struct ProjectorGridSample(Vec2d Projector, Vec2d Camera, float Confidence, double FitResidualPx, int Samples);

/// <summary>
/// One view's decode of one projector turned around: for a projector pixel u, the camera position that saw it. The
/// decoded camera pixels near u (projector coordinate within <see cref="InverseOptions.RadiusPx"/>) are fitted
/// with a local affine map projector ← camera by weighted least squares (the noise is in the decoded projector
/// coordinate, not in the camera pixel's place), which is inverted at u. The fit is refused when it is degenerate,
/// when its residual, carried to camera pixels, exceeds <see cref="InverseOptions.MaxFitResidualPx"/> (a
/// neighbourhood that spans a depth edge, two surfaces seen in two places), when u is not surrounded by decoded
/// pixels on every side (the edge of what the view saw), when they fill too little of the disc (a rim seen edge-on),
/// when the nearest decoded pixel is farther than <see cref="InverseOptions.MaxGapPx"/> (a fold the view did not see),
/// or when the camera pixels around the answer did not themselves decode near u (an answer past a silhouette). Immutable after construction; safe to query
/// from many threads.
/// </summary>
public sealed class InverseCorrespondence
{
    private const int Bucket = 4;
    private readonly int _bw;
    private readonly int _bh;
    private readonly int[] _start;
    private readonly int[] _pixels;
    private readonly float[] _px;
    private readonly float[] _py;
    private readonly float[] _confidence;
    private readonly int _cameraWidth;

    public InverseCorrespondence(CorrespondenceMap map, InverseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        Options = options ?? InverseOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.Stride, 1);
        Map = map;
        _cameraWidth = map.Width;
        _bw = (map.ProjectorWidth + Bucket - 1) / Bucket;
        _bh = (map.ProjectorHeight + Bucket - 1) / Bucket;
        _px = map.ProjectorX;
        _py = map.ProjectorY;
        _confidence = map.Confidence;
        // Counting sort of the usable camera pixels by the projector bucket they decoded into.
        var counts = new int[_bw * _bh + 1];
        var usable = new List<int>();
        for (var k = 0; k < map.Flags.Length; k++)
        {
            if ((map.Flags[k] & DecodeStatus.Valid) == 0 || map.Confidence[k] < Options.MinConfidence)
                continue;
            var b = BucketOf(_px[k], _py[k]);
            if (b < 0)
                continue;
            usable.Add(k);
            counts[b + 1]++;
        }
        for (var b = 0; b < _bw * _bh; b++)
            counts[b + 1] += counts[b];
        _start = (int[])counts.Clone();
        _pixels = new int[usable.Count];
        var fill = (int[])counts.Clone();
        foreach (var k in usable)
            _pixels[fill[BucketOf(_px[k], _py[k])]++] = k;
        UsablePixels = usable.Count;
    }

    public CorrespondenceMap Map { get; }

    public InverseOptions Options { get; }

    public int ProjectorWidth => Map.ProjectorWidth;

    public int ProjectorHeight => Map.ProjectorHeight;

    /// <summary>The decoded camera pixels confident enough to place a projector pixel.</summary>
    public int UsablePixels { get; }

    private int BucketOf(float x, float y)
    {
        if (!(x >= 0) || !(y >= 0) || x >= Map.ProjectorWidth || y >= Map.ProjectorHeight)
            return -1;
        return (int)y / Bucket * _bw + (int)x / Bucket;
    }

    /// <summary>The grid nodes at the options' stride that this view placed, row by row.</summary>
    public IReadOnlyList<ProjectorGridSample> Grid() => Grid(Options.Stride);

    /// <summary>The grid nodes at stride <paramref name="stride"/> (centres ((i + ½)·s, (j + ½)·s)) that this view placed.</summary>
    public IReadOnlyList<ProjectorGridSample> Grid(int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);
        var list = new List<ProjectorGridSample>();
        for (var y = 0; (y + 0.5) * stride < ProjectorHeight; y++)
        {
            for (var x = 0; (x + 0.5) * stride < ProjectorWidth; x++)
            {
                if (TryAt(new Vec2d((x + 0.5) * stride, (y + 0.5) * stride), out var s))
                    list.Add(s);
            }
        }
        return list;
    }

    /// <summary>The camera position that saw projector pixel <paramref name="projector"/>; false when too few
    /// confident pixels decoded near it or their local fit is degenerate or poor.</summary>
    public bool TryAt(Vec2d projector, out ProjectorGridSample sample)
    {
        sample = default;
        var r = Options.RadiusPx;
        var r2 = r * r;
        var bx0 = Math.Max(0, (int)Math.Floor((projector.X - r) / Bucket));
        var by0 = Math.Max(0, (int)Math.Floor((projector.Y - r) / Bucket));
        var bx1 = Math.Min(_bw - 1, (int)Math.Floor((projector.X + r) / Bucket));
        var by1 = Math.Min(_bh - 1, (int)Math.Floor((projector.Y + r) / Bucket));
        if (bx0 > bx1 || by0 > by1)
            return false;
        // Weighted sums about the first pixel's place keep the normal equations well scaled.
        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        double su = 0, sxu = 0, syu = 0, sv = 0, sxv = 0, syv = 0, suu = 0, svv = 0, sconf = 0;
        var n = 0;
        var origin = Vec2d.Zero;
        var sides = 0;
        var nearest = double.PositiveInfinity;
        for (var by = by0; by <= by1; by++)
        {
            for (var bx = bx0; bx <= bx1; bx++)
            {
                var b = by * _bw + bx;
                for (var i = _start[b]; i < _start[b + 1]; i++)
                {
                    var k = _pixels[i];
                    var du = _px[k] - projector.X;
                    var dv = _py[k] - projector.Y;
                    if (du * du + dv * dv > r2)
                        continue;
                    sides |= (du < 0 ? 1 : du > 0 ? 2 : 3) | (dv < 0 ? 4 : dv > 0 ? 8 : 12);
                    nearest = Math.Min(nearest, du * du + dv * dv);
                    var cam = new Vec2d(k % _cameraWidth + 0.5, k / _cameraWidth + 0.5);
                    if (n == 0)
                        origin = cam;
                    var x = cam.X - origin.X;
                    var y = cam.Y - origin.Y;
                    double w = _confidence[k];
                    sw += w;
                    sx += w * x;
                    sy += w * y;
                    sxx += w * x * x;
                    sxy += w * x * y;
                    syy += w * y * y;
                    su += w * du;
                    sxu += w * x * du;
                    syu += w * y * du;
                    sv += w * dv;
                    sxv += w * x * dv;
                    syv += w * y * dv;
                    suu += w * du * du;
                    svv += w * dv * dv;
                    sconf += w;
                    n++;
                }
            }
        }
        // The asked pixel must be surrounded: decoded pixels on both sides of it along both axes, so it is
        // interpolated, never extrapolated past the edge of what this view decoded (an occluding edge).
        if (n < Options.MinSamples || !(sw > 0) || sides != 15 || nearest > Options.MaxGapPx * Options.MaxGapPx)
            return false;
        // Normal equations of du ≈ a0 + a1·x + a2·y (and dv likewise); one matrix serves both axes.
        var mx = sx / sw;
        var my = sy / sw;
        var cxx = sxx / sw - mx * mx;
        var cyy = syy / sw - my * my;
        var cxy = sxy / sw - mx * my;
        // The pixels must spread in two directions (a line of pixels fixes nothing across it).
        if (!(cxx * cyy - cxy * cxy > 1e-3))
            return false;
        var m = new Mat3d(sw, sx, sy, sx, sxx, sxy, sy, sxy, syy);
        if (!m.TryInverse(out var mi))
            return false;
        var a = mi * new Vec3d(su, sxu, syu);
        var c = mi * new Vec3d(sv, sxv, syv);
        // Projector offset at camera offset (x, y): (a0 + a1·x + a2·y, c0 + c1·x + c2·y); zero at the asked pixel.
        var det = a.Y * c.Z - a.Z * c.Y;
        if (!(Math.Abs(det) > 1e-6))
            return false;
        // |det| is projector px² per camera px², so a whole disc holds π·r²/|det| camera pixels.
        if (n < Options.MinFill * Math.PI * r2 / Math.Abs(det))
            return false;
        var x0 = (-a.X * c.Z + a.Z * c.X) / det;
        var y0 = (-a.Y * c.X + a.X * c.Y) / det;
        // Weighted residual of both axes: Σw·(du − fit)² = Σw·du² − coefficients·(right-hand side).
        var ru = Math.Max(0, suu - (a.X * su + a.Y * sxu + a.Z * syu));
        var rv = Math.Max(0, svv - (c.X * sv + c.Y * sxv + c.Z * syv));
        var projectorRms = Math.Sqrt((ru + rv) / (2 * sw));
        // Projector pixels → camera pixels by the inverse map's mean stretch.
        var residual = projectorRms / Math.Sqrt(Math.Abs(det));
        if (!(residual <= Options.MaxFitResidualPx) || !double.IsFinite(x0) || !double.IsFinite(y0))
            return false;
        // The asked pixel must lie among the pixels that placed it, not beyond them.
        var spread = Math.Sqrt(Math.Max(0, cxx + cyy));
        if ((new Vec2d(x0 - mx, y0 - my)).Length > 2 * spread + 1)
            return false;
        var camera = origin + new Vec2d(x0, y0);
        // The 2 × 2 camera pixels around the answer must have decoded near u themselves: an answer just past a
        // silhouette in the camera lands on pixels that see something else (far off in the projector), or nothing.
        var tolerance = r + 2 * Math.Sqrt(Math.Abs(det));
        var ix = (int)Math.Floor(camera.X - 0.5);
        var iy = (int)Math.Floor(camera.Y - 0.5);
        for (var y = iy; y <= iy + 1; y++)
        {
            for (var x = ix; x <= ix + 1; x++)
            {
                if (x < 0 || y < 0 || x >= Map.Width || y >= Map.Height)
                    return false;
                var k = y * Map.Width + x;
                if ((Map.Flags[k] & DecodeStatus.Valid) == 0)
                    return false;
                var du = _px[k] - projector.X;
                var dv = _py[k] - projector.Y;
                if (du * du + dv * dv > tolerance * tolerance)
                    return false;
            }
        }
        sample = new ProjectorGridSample(projector, camera, (float)(sconf / n), residual, n);
        return true;
    }
}
