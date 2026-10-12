using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>Points with optional normals and colours (packed 0xRRGGBB). An organised cloud keeps its sensor
/// grid (<see cref="GridWidth"/> × <see cref="GridHeight"/>, row-major, invalid points NaN) so neighbours are
/// free and a mesh comes straight from the grid.</summary>
public sealed class PointCloud
{
    public Vec3d[] Points { get; }
    public Vec3d[] Normals { get; }
    public int[] Colors { get; }
    public int GridWidth { get; }
    public int GridHeight { get; }

    public PointCloud(Vec3d[] points, Vec3d[]? normals = null, int[]? colors = null, int gridWidth = 0, int gridHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (normals is { Length: > 0 } && normals.Length != points.Length)
            throw new ArgumentException("One normal per point (or none).", nameof(normals));
        if (colors is { Length: > 0 } && colors.Length != points.Length)
            throw new ArgumentException("One colour per point (or none).", nameof(colors));
        if (gridWidth * gridHeight != 0 && gridWidth * gridHeight != points.Length)
            throw new ArgumentException("An organised cloud has one point per grid cell.", nameof(gridWidth));
        Points = points;
        Normals = normals ?? [];
        Colors = colors ?? [];
        GridWidth = gridWidth;
        GridHeight = gridHeight;
    }

    public int Count => Points.Length;
    public bool IsOrganised => GridWidth > 0 && GridHeight > 0;
    public bool HasNormals => Normals.Length == Points.Length && Points.Length > 0;
    public bool HasColors => Colors.Length == Points.Length && Points.Length > 0;

    public static bool IsValid(Vec3d p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    public static readonly Vec3d Invalid = new(double.NaN, double.NaN, double.NaN);

    /// <summary>The valid points, unorganised.</summary>
    public PointCloud Compact()
    {
        var keep = Enumerable.Range(0, Count).Where(i => IsValid(Points[i])).ToArray();
        return new PointCloud(
            keep.Select(i => Points[i]).ToArray(),
            HasNormals ? keep.Select(i => Normals[i]).ToArray() : null,
            HasColors ? keep.Select(i => Colors[i]).ToArray() : null);
    }

    public (Vec3d Min, Vec3d Max) Bounds()
    {
        var min = new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3d(double.MinValue, double.MinValue, double.MinValue);
        foreach (var p in Points)
        {
            if (!IsValid(p))
                continue;
            min = Vec3d.Min(min, p);
            max = Vec3d.Max(max, p);
        }
        return (min, max);
    }
}

/// <summary>A static 3D k-d tree over points (invalid points skipped): nearest, k nearest and radius queries.
/// Built once, read from any thread.</summary>
public sealed class KdTree3
{
    private readonly Vec3d[] _points;
    private readonly int[] _index;
    private readonly int[] _axis;

    public KdTree3(IReadOnlyList<Vec3d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = points.ToArray();
        _index = Enumerable.Range(0, _points.Length).Where(i => PointCloud.IsValid(_points[i])).ToArray();
        _axis = new int[_index.Length];
        Build(0, _index.Length, 0);
    }

    public int Count => _index.Length;

    private void Build(int lo, int hi, int depth)
    {
        if (hi - lo <= 1)
        {
            if (hi > lo)
                _axis[lo] = depth % 3;
            return;
        }
        // Split on the axis of largest spread for balanced, well-shaped cells.
        var min = new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3d(double.MinValue, double.MinValue, double.MinValue);
        for (var i = lo; i < hi; i++)
        {
            min = Vec3d.Min(min, _points[_index[i]]);
            max = Vec3d.Max(max, _points[_index[i]]);
        }
        var ext = max - min;
        var axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
        var mid = (lo + hi) / 2;
        Select(lo, hi - 1, mid, axis);
        _axis[mid] = axis;
        Build(lo, mid, depth + 1);
        Build(mid + 1, hi, depth + 1);
    }

    private void Select(int lo, int hi, int k, int axis)
    {
        while (lo < hi)
        {
            var pivot = _points[_index[(lo + hi) / 2]][axis];
            var i = lo;
            var j = hi;
            while (i <= j)
            {
                while (_points[_index[i]][axis] < pivot)
                    i++;
                while (_points[_index[j]][axis] > pivot)
                    j--;
                if (i <= j)
                {
                    (_index[i], _index[j]) = (_index[j], _index[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j)
                hi = j;
            else if (k >= i)
                lo = i;
            else
                return;
        }
    }

    /// <summary>The index (into the points given) of the nearest point, or -1 for an empty tree.</summary>
    public int Nearest(Vec3d q, out double distanceSquared)
    {
        var best = -1;
        var bestD = double.MaxValue;
        Nearest(0, _index.Length, q, ref best, ref bestD);
        distanceSquared = bestD;
        return best;
    }

    private void Nearest(int lo, int hi, Vec3d q, ref int best, ref double bestD)
    {
        if (lo >= hi)
            return;
        var mid = (lo + hi) / 2;
        var p = _points[_index[mid]];
        var d = (p - q).LengthSquared;
        if (d < bestD)
        {
            bestD = d;
            best = _index[mid];
        }
        var axis = _axis[mid];
        var diff = q[axis] - p[axis];
        var (first, second) = diff < 0 ? ((lo, mid), (mid + 1, hi)) : ((mid + 1, hi), (lo, mid));
        Nearest(first.Item1, first.Item2, q, ref best, ref bestD);
        if (diff * diff < bestD)
            Nearest(second.Item1, second.Item2, q, ref best, ref bestD);
    }

    /// <summary>The k nearest points' indices, nearest first.</summary>
    public int[] KNearest(Vec3d q, int k)
    {
        if (k <= 0 || _index.Length == 0)
            return [];
        var heap = new PriorityQueue<int, double>(k + 1, Comparer<double>.Create((a, b) => b.CompareTo(a)));
        KNearest(0, _index.Length, q, k, heap);
        var result = new int[heap.Count];
        for (var i = result.Length - 1; i >= 0; i--)
            result[i] = heap.Dequeue();
        return result;
    }

    private void KNearest(int lo, int hi, Vec3d q, int k, PriorityQueue<int, double> heap)
    {
        if (lo >= hi)
            return;
        var mid = (lo + hi) / 2;
        var p = _points[_index[mid]];
        var d = (p - q).LengthSquared;
        if (heap.Count < k)
            heap.Enqueue(_index[mid], d);
        else if (heap.TryPeek(out _, out var worst) && d < worst)
        {
            heap.Dequeue();
            heap.Enqueue(_index[mid], d);
        }
        var axis = _axis[mid];
        var diff = q[axis] - p[axis];
        var (first, second) = diff < 0 ? ((lo, mid), (mid + 1, hi)) : ((mid + 1, hi), (lo, mid));
        KNearest(first.Item1, first.Item2, q, k, heap);
        if (heap.Count < k || (heap.TryPeek(out _, out var w) && diff * diff < w))
            KNearest(second.Item1, second.Item2, q, k, heap);
    }

    /// <summary>Every point within the radius (unordered).</summary>
    public List<int> Radius(Vec3d q, double radius)
    {
        var found = new List<int>();
        Radius(0, _index.Length, q, radius * radius, found);
        return found;
    }

    private void Radius(int lo, int hi, Vec3d q, double r2, List<int> found)
    {
        if (lo >= hi)
            return;
        var mid = (lo + hi) / 2;
        var p = _points[_index[mid]];
        if ((p - q).LengthSquared <= r2)
            found.Add(_index[mid]);
        var axis = _axis[mid];
        var diff = q[axis] - p[axis];
        if (diff <= 0 || diff * diff <= r2)
            Radius(lo, mid, q, r2, found);
        if (diff >= 0 || diff * diff <= r2)
            Radius(mid + 1, hi, q, r2, found);
    }
}
