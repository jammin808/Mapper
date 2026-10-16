using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>A blob the camera saw: its intensity-weighted centre (camera px), its mass (sum of levels over the black picture) and
/// how many pixels crossed the threshold.</summary>
public readonly record struct DotBlob(Vec2d At, double Mass, int Pixels);

/// <summary>Which observed blob each expected dot was matched to (−1: none), the consensus shift the matching started from, and how
/// many matched.</summary>
public sealed record DotMatch(ImmutableArray<int> Observed, Vec2d Shift, int Matched);

/// <summary>
/// The dot check's patterns: a grid of dots at a spacing, each moved by a seeded jitter so the pattern never repeats itself (a shift
/// of one spacing cannot pass for no shift), drawn as small Gaussian spots on black. Detection takes the camera's picture of one
/// projector's dots against the black picture and answers each spot's weighted centre; matching pairs the spots with where the
/// project says each dot should land, by a consensus shift first (every pair within reach votes), then by an affine fit and each
/// dot's neighbours' residual. Pure functions; the same code runs against the simulator and a real camera.
/// </summary>
public static class DotCheck
{
    /// <summary>The default spacing of the dots (projector px).</summary>
    public const double DefaultSpacing = 64;

    /// <summary>Jittered grid dots inside a raster: a margin of half a spacing, each dot moved up to ±<paramref name="jitter"/> of the
    /// spacing on each axis.</summary>
    public static ImmutableArray<Vec2d> Pattern(int width, int height, double spacing, ulong seed, double jitter = 0.25)
    {
        if (width <= 0 || height <= 0 || spacing <= 2)
            return [];
        var rng = new Pcg32(seed, 0x0d07);
        var cols = Math.Max(1, (int)Math.Floor((width - spacing) / spacing) + 1);
        var rows = Math.Max(1, (int)Math.Floor((height - spacing) / spacing) + 1);
        var x0 = (width - (cols - 1) * spacing) / 2;
        var y0 = (height - (rows - 1) * spacing) / 2;
        var dots = ImmutableArray.CreateBuilder<Vec2d>(cols * rows);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                dots.Add(new Vec2d(x0 + c * spacing + rng.Range(-jitter, jitter) * spacing, y0 + r * spacing + rng.Range(-jitter, jitter) * spacing));
        return dots.MoveToImmutable();
    }

    /// <summary>The dots drawn as Gaussian spots of σ <paramref name="sigma"/> (projector px), peak <paramref name="level"/>, on black.</summary>
    public static byte[] Render(ReadOnlySpan<Vec2d> dots, int width, int height, double sigma, byte level = 255)
    {
        var frame = new byte[width * height];
        var reach = (int)Math.Ceiling(3 * sigma);
        var inv = 1 / (2 * sigma * sigma);
        foreach (var d in dots)
        {
            var cx = (int)Math.Round(d.X - 0.5);
            var cy = (int)Math.Round(d.Y - 0.5);
            for (var y = Math.Max(0, cy - reach); y <= Math.Min(height - 1, cy + reach); y++)
                for (var x = Math.Max(0, cx - reach); x <= Math.Min(width - 1, cx + reach); x++)
                {
                    var dx = x + 0.5 - d.X;
                    var dy = y + 0.5 - d.Y;
                    var v = level * Math.Exp(-(dx * dx + dy * dy) * inv);
                    var at = y * width + x;
                    frame[at] = (byte)Math.Max(frame[at], Math.Min(255, Math.Round(v)));
                }
        }
        return frame;
    }

    /// <summary>The spots in a picture of dots against the black picture: connected pixels brighter than the black by the threshold
    /// (a quarter of the brightest difference, at least <paramref name="minLevel"/>), each centred by its levels over a window of
    /// ±<paramref name="window"/> px around its brightest pixel. Spots smaller than <paramref name="minPixels"/> are noise.</summary>
    public static List<DotBlob> Detect(byte[] picture, byte[] black, int width, int height, int minLevel = 12, int window = 3, int minPixels = 2)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(black);
        var n = width * height;
        if (picture.Length < n || black.Length < n)
            throw new ArgumentException("the pictures are smaller than their raster", nameof(picture));
        var diff = new short[n];
        var peak = 0;
        for (var i = 0; i < n; i++)
        {
            var d = picture[i] - black[i];
            diff[i] = (short)d;
            peak = Math.Max(peak, d);
        }
        var threshold = Math.Max(minLevel, peak / 4);
        var floor = Math.Max(2, threshold / 4);
        var seen = new bool[n];
        var stack = new Stack<int>();
        var blobs = new List<DotBlob>();
        for (var start = 0; start < n; start++)
        {
            if (seen[start] || diff[start] < threshold)
                continue;
            var best = start;
            var count = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                count++;
                if (diff[i] > diff[best])
                    best = i;
                var x = i % width;
                var y = i / width;
                if (x > 0)
                    Visit(i - 1);
                if (x + 1 < width)
                    Visit(i + 1);
                if (y > 0)
                    Visit(i - width);
                if (y + 1 < height)
                    Visit(i + width);
            }
            if (count < minPixels)
                continue;
            // The centre: levels over the floor in a window that holds the spot (from its size), centred on the brightest pixel,
            // then once more on the first centre, so the window's edges sit evenly around the spot.
            var r = Math.Max(window, (int)Math.Ceiling(1.5 * Math.Sqrt(count / Math.PI)) + 1);
            var (sx, sy, sw) = Centre(diff, width, height, best % width, best / width, r, floor);
            if (sw > 0)
                (sx, sy, sw) = Centre(diff, width, height, (int)Math.Floor(sx / sw), (int)Math.Floor(sy / sw), r, floor);
            if (sw > 0)
                blobs.Add(new DotBlob(new Vec2d(sx / sw, sy / sw), sw, count));
        }
        return blobs;

        void Visit(int j)
        {
            if (!seen[j] && diff[j] >= threshold)
            {
                seen[j] = true;
                stack.Push(j);
            }
        }
    }

    private static (double X, double Y, double W) Centre(short[] diff, int width, int height, int cx, int cy, int r, int floor)
    {
        double sx = 0, sy = 0, sw = 0;
        for (var y = Math.Max(0, cy - r); y <= Math.Min(height - 1, cy + r); y++)
            for (var x = Math.Max(0, cx - r); x <= Math.Min(width - 1, cx + r); x++)
            {
                var w = diff[y * width + x] - floor;
                if (w <= 0)
                    continue;
                sx += w * (x + 0.5);
                sy += w * (y + 0.5);
                sw += w;
            }
        return (sx, sy, sw);
    }

    /// <summary>The median distance from each predicted dot to its nearest predicted neighbour: the pattern's spacing in the
    /// camera's picture (0 with fewer than two).</summary>
    public static double CameraSpacing(IReadOnlyList<Vec2d?> predicted)
    {
        ArgumentNullException.ThrowIfNull(predicted);
        var pts = predicted.Where(p => p is not null).Select(p => p!.Value).ToList();
        if (pts.Count < 2)
            return 0;
        var near = new List<double>(pts.Count);
        var grid = new PointGrid(pts, 16);
        foreach (var p in pts)
        {
            var d = grid.Nearest(p, double.PositiveInfinity, exclude: p);
            if (d.Index >= 0)
                near.Add(d.Distance);
        }
        near.Sort();
        return near.Count == 0 ? 0 : near[near.Count / 2];
    }

    /// <summary>Pairs expected dots (camera px, null where the dot lands outside the camera's view) with observed spots. Every
    /// expected dot votes for the shift to each spot within <paramref name="reach"/>; the most voted shift starts the matching; then an
    /// affine map and each dot's neighbours' residual refine where each dot is looked for. A spot is matched to one dot at most; a dot
    /// whose nearest spot is further than a third of the spacing from where it is looked for stays unmatched.</summary>
    public static DotMatch Match(IReadOnlyList<Vec2d?> predicted, IReadOnlyList<DotBlob> observed, double reach)
    {
        ArgumentNullException.ThrowIfNull(predicted);
        ArgumentNullException.ThrowIfNull(observed);
        var none = Enumerable.Repeat(-1, predicted.Count).ToImmutableArray();
        var spacing = CameraSpacing(predicted);
        if (observed.Count == 0 || spacing <= 0)
            return new DotMatch(none, Vec2d.Zero, 0);
        var spots = observed.Select(b => b.At).ToList();
        var grid = new PointGrid(spots, Math.Max(4, spacing));

        // 1. The consensus shift: a vote per (dot, spot) pair within reach, in 1 px bins, smoothed over 3 × 3 bins.
        var bin = 1.0;
        var half = (int)Math.Ceiling(reach / bin);
        var side = 2 * half + 1;
        var votes = new int[side * side];
        foreach (var p in predicted)
        {
            if (p is not { } q)
                continue;
            foreach (var j in grid.Within(q, reach))
            {
                var d = spots[j] - q;
                var bx = (int)Math.Round(d.X / bin) + half;
                var by = (int)Math.Round(d.Y / bin) + half;
                if (bx >= 0 && by >= 0 && bx < side && by < side)
                    votes[by * side + bx]++;
            }
        }
        var bestAt = 0;
        var bestVotes = -1;
        for (var y = 1; y < side - 1; y++)
            for (var x = 1; x < side - 1; x++)
            {
                var s = 0;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        s += votes[(y + dy) * side + x + dx];
                if (s > bestVotes)
                    (bestVotes, bestAt) = (s, y * side + x);
            }
        var shift = new Vec2d((bestAt % side - half) * bin, (bestAt / side - half) * bin);

        // 2. Assign around the shift, then twice more around an affine fit plus the neighbours' residual.
        var gate = spacing / 3;
        var looked = predicted.Select(p => p is { } q ? q + shift : (Vec2d?)null).ToList();
        var assigned = Assign(looked, spots, grid, gate);
        for (var pass = 0; pass < 2; pass++)
        {
            if (Affine(predicted, spots, assigned) is not { } a)
                break;
            var matchedAt = new List<Vec2d>();
            var residual = new List<Vec2d>();
            for (var i = 0; i < predicted.Count; i++)
                if (assigned[i] >= 0)
                {
                    var p = predicted[i]!.Value;
                    matchedAt.Add(p);
                    residual.Add(spots[assigned[i]] - a.Apply(p));
                }
            var near = new PointGrid(matchedAt, Math.Max(4, spacing));
            for (var i = 0; i < predicted.Count; i++)
            {
                if (predicted[i] is not { } p)
                    continue;
                // Its neighbours' residual, never its own: a dot does not vote for the spot it was given.
                var ids = near.NearestK(p, 7).Where(k => matchedAt[k] != p).Take(6).ToList();
                var rx = ids.Select(k => residual[k].X).ToList();
                var ry = ids.Select(k => residual[k].Y).ToList();
                looked[i] = a.Apply(p) + (ids.Count == 0 ? Vec2d.Zero : new Vec2d(Median(rx), Median(ry)));
            }
            assigned = Assign(looked, spots, grid, gate);
        }
        return new DotMatch([.. assigned], shift, assigned.Count(i => i >= 0));
    }

    private static int[] Assign(List<Vec2d?> looked, List<Vec2d> spots, PointGrid grid, double gate)
    {
        var best = new int[looked.Count];
        var owner = new Dictionary<int, (int Dot, double Distance)>();
        for (var i = 0; i < looked.Count; i++)
        {
            best[i] = -1;
            if (looked[i] is not { } q)
                continue;
            var (j, d) = grid.Nearest(q, gate);
            if (j < 0)
                continue;
            if (owner.TryGetValue(j, out var o))
            {
                if (o.Distance <= d)
                    continue;
                best[o.Dot] = -1;
            }
            owner[j] = (i, d);
            best[i] = j;
        }
        return best;
    }

    /// <summary>An affine map x' = A·x + t.</summary>
    private readonly record struct AffineMap(double A, double B, double C, double D, double Tx, double Ty)
    {
        public Vec2d Apply(Vec2d p) => new(A * p.X + B * p.Y + Tx, C * p.X + D * p.Y + Ty);
    }

    private static AffineMap? Affine(IReadOnlyList<Vec2d?> predicted, List<Vec2d> spots, int[] assigned)
    {
        var m = new double[3, 3];
        var bx = new double[3];
        var by = new double[3];
        var count = 0;
        for (var i = 0; i < predicted.Count; i++)
        {
            if (assigned[i] < 0 || predicted[i] is not { } p)
                continue;
            var o = spots[assigned[i]];
            double[] r = [p.X, p.Y, 1];
            for (var a = 0; a < 3; a++)
            {
                for (var b = 0; b < 3; b++)
                    m[a, b] += r[a] * r[b];
                bx[a] += r[a] * o.X;
                by[a] += r[a] * o.Y;
            }
            count++;
        }
        if (count < 6 || Solve3(m, bx) is not { } sx || Solve3(m, by) is not { } sy)
            return null;
        return new AffineMap(sx[0], sx[1], sy[0], sy[1], sx[2], sy[2]);
    }

    private static double[]? Solve3(double[,] m, double[] b)
    {
        var a = new double[3, 4];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
                a[r, c] = m[r, c];
            a[r, 3] = b[r];
        }
        for (var c = 0; c < 3; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < 3; r++)
                if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c]))
                    pivot = r;
            if (Math.Abs(a[pivot, c]) < 1e-12)
                return null;
            for (var k = 0; k < 4; k++)
                (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
            for (var r = 0; r < 3; r++)
            {
                if (r == c)
                    continue;
                var f = a[r, c] / a[c, c];
                for (var k = c; k < 4; k++)
                    a[r, k] -= f * a[c, k];
            }
        }
        return [a[0, 3] / a[0, 0], a[1, 3] / a[1, 1], a[2, 3] / a[2, 2]];
    }

    /// <summary>The median of a list (sorted in place); 0 when empty.</summary>
    public static double Median(List<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return 0;
        values.Sort();
        var m = values.Count / 2;
        return values.Count % 2 == 1 ? values[m] : 0.5 * (values[m - 1] + values[m]);
    }

    /// <summary>Points hashed into square cells for nearest and within-reach queries.</summary>
    private sealed class PointGrid
    {
        private readonly IReadOnlyList<Vec2d> _points;
        private readonly double _cell;
        private readonly Dictionary<(int, int), List<int>> _cells = [];

        public PointGrid(IReadOnlyList<Vec2d> points, double cell)
        {
            _points = points;
            _cell = cell;
            for (var i = 0; i < points.Count; i++)
            {
                var key = Key(points[i]);
                if (!_cells.TryGetValue(key, out var list))
                    _cells[key] = list = [];
                list.Add(i);
            }
        }

        private (int, int) Key(Vec2d p) => ((int)Math.Floor(p.X / _cell), (int)Math.Floor(p.Y / _cell));

        public IEnumerable<int> Within(Vec2d p, double reach)
        {
            var r = (int)Math.Ceiling(reach / _cell);
            var (kx, ky) = Key(p);
            for (var y = ky - r; y <= ky + r; y++)
                for (var x = kx - r; x <= kx + r; x++)
                    if (_cells.TryGetValue((x, y), out var list))
                        foreach (var i in list)
                            if (Vec2d.Distance(_points[i], p) <= reach)
                                yield return i;
        }

        public (int Index, double Distance) Nearest(Vec2d p, double reach, Vec2d? exclude = null)
        {
            var best = -1;
            var bestD = reach;
            var r = double.IsFinite(reach) ? (int)Math.Ceiling(reach / _cell) : 1;
            var (kx, ky) = Key(p);
            for (var grow = 0; grow < 64; grow++)
            {
                for (var y = ky - r; y <= ky + r; y++)
                    for (var x = kx - r; x <= kx + r; x++)
                        if (_cells.TryGetValue((x, y), out var list))
                            foreach (var i in list)
                            {
                                if (exclude is { } e && _points[i] == e)
                                    continue;
                                var d = Vec2d.Distance(_points[i], p);
                                if (d < bestD || (best < 0 && !double.IsFinite(bestD)))
                                    (best, bestD) = (i, d);
                            }
                if (best >= 0 || double.IsFinite(reach))
                    break;
                r++;
            }
            return (best, bestD);
        }

        public List<int> NearestK(Vec2d p, int k)
        {
            var found = new List<(int Index, double Distance)>();
            var (kx, ky) = Key(p);
            for (var r = 1; r < 64 && found.Count < k && found.Count < _points.Count; r++)
            {
                found.Clear();
                for (var y = ky - r; y <= ky + r; y++)
                    for (var x = kx - r; x <= kx + r; x++)
                        if (_cells.TryGetValue((x, y), out var list))
                            foreach (var i in list)
                                found.Add((i, Vec2d.Distance(_points[i], p)));
            }
            return [.. found.OrderBy(f => f.Distance).Take(k).Select(f => f.Index)];
        }
    }
}
