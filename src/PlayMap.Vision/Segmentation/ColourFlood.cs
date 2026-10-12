using System.Runtime.CompilerServices;
namespace PlayMap.Vision.Segmentation;

/// <summary>A competitive minimax flood over a colour picture (a seeded watershed): every seed grows at once and a
/// pixel joins the seed that reaches it through the path whose worst colour distance (ΔE to that seed's own colour)
/// is smallest. Each pixel's level is that worst distance, so the region a seed holds at tolerance t is exactly its
/// pixels with level ≤ t — one flood answers every tolerance, which is what lets the tolerance be chosen
/// afterwards from the data. Levels are kept in buckets of <see cref="BucketWidth"/> ΔE (a bucket queue, O(1) per
/// step); ties within a bucket go first-in first-out, so equal seeds meet half way.</summary>
internal static class ColourFlood
{
    public const float BucketWidth = 0.25f;
    public const int Buckets = 400;
    public const float Cap = BucketWidth * Buckets;

    public readonly record struct Seed(int Pixel, float L, float A, float B);

    /// <summary>Floods from the seeds over the pixels <paramref name="allowed"/> admits (all when null), never
    /// past <paramref name="maxLevel"/> (no tolerance will be chosen above it, so nothing beyond is needed).
    /// Returns per pixel the seed index (−1 unreached) and the level.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (int[] Labels, float[] Levels) Run(LabImage lab, IReadOnlyList<Seed> seeds, bool[]? allowed, float maxLevel, CancellationToken ct)
    {
        int w = lab.Width, h = lab.Height, n = w * h;
        var labels = new int[n];
        var levels = new float[n];
        var best = new float[n];
        Array.Fill(labels, -1);
        Array.Fill(levels, float.PositiveInfinity);
        Array.Fill(best, float.PositiveInfinity);
        var queue = new BucketQueue(Buckets, Math.Max(1024, (w + h) * 4));
        for (var s = 0; s < seeds.Count; s++)
        {
            var p = seeds[s].Pixel;
            if (best[p] == 0)
                continue;
            best[p] = 0;
            queue.Push(0, p, s, 0);
        }
        var steps = 0;
        while (queue.TryPop(out var p, out var s, out var level))
        {
            if (labels[p] != -1 || level > best[p])
                continue;
            labels[p] = s;
            levels[p] = level;
            if ((++steps & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();
            int x = p % w, y = p / w;
            var seed = seeds[s];
            if (x > 0) Relax(p - 1);
            if (x + 1 < w) Relax(p + 1);
            if (y > 0) Relax(p - w);
            if (y + 1 < h) Relax(p + w);

            void Relax(int q)
            {
                if (labels[q] != -1 || (allowed is not null && !allowed[q]))
                    return;
                var c = Math.Max(level, lab.DeltaE(q, seed.L, seed.A, seed.B));
                if (c >= best[q] || c > maxLevel)
                    return;
                best[q] = c;
                queue.Push(Math.Min((int)(c / BucketWidth), Buckets - 1), q, s, c);
            }
        }
        return (labels, levels);
    }

    /// <summary>The tolerance at which seed <paramref name="seed"/>'s region is most stable: the middle of the
    /// widest flat stretch of its area-versus-tolerance curve within [<paramref name="min"/>, <paramref name="max"/>]
    /// (a maximally stable region). A leak shows as a step in that curve; a surface as a plateau. Returns the
    /// tolerance and the relative area change there (0 = perfectly stable).</summary>
    public static (float Tolerance, double Instability) StableTolerance(int[] labels, float[] levels, int seed, float min, float max)
    {
        var hist = new int[Buckets + 1];
        for (var i = 0; i < labels.Length; i++)
            if (labels[i] == seed)
                hist[Math.Min((int)(levels[i] / BucketWidth), Buckets)]++;
        var cum = new long[Buckets + 1];
        long run = 0;
        for (var b = 0; b <= Buckets; b++)
        {
            run += hist[b];
            cum[b] = run;
        }
        const int delta = (int)(2f / BucketWidth);
        var lo = Math.Clamp((int)(min / BucketWidth), delta, Buckets - delta - 1);
        var hi = Math.Clamp((int)(max / BucketWidth), lo, Buckets - delta - 1);
        var q = new double[hi - lo + 1];
        var bestQ = double.MaxValue;
        for (var b = lo; b <= hi; b++)
        {
            var area = Math.Max(cum[b], 1);
            q[b - lo] = (cum[b + delta] - cum[b - delta]) / (double)area;
            bestQ = Math.Min(bestQ, q[b - lo]);
        }
        // The widest run of near-minimal instability; its middle is the tolerance.
        var slack = bestQ + 0.01;
        int bestStart = lo, bestLen = 0, start = -1;
        for (var b = lo; b <= hi + 1; b++)
        {
            var inside = b <= hi && q[b - lo] <= slack;
            if (inside && start < 0)
                start = b;
            if (!inside && start >= 0)
            {
                if (b - start > bestLen)
                {
                    bestLen = b - start;
                    bestStart = start;
                }
                start = -1;
            }
        }
        var mid = bestStart + bestLen / 2;
        return ((mid + 0.5f) * BucketWidth, bestQ);
    }

    /// <summary>A FIFO-per-bucket priority queue over integer buckets, popping the lowest non-empty bucket;
    /// entries live in growable parallel arrays linked by index, and a popped entry's slot is reused, so the
    /// arrays hold the frontier, not the picture.</summary>
    private sealed class BucketQueue
    {
        private readonly int[] _head;
        private readonly int[] _tail;
        private int[] _pixel;
        private int[] _seed;
        private float[] _level;
        private int[] _next;
        private int _count;
        private int _current;
        private int _free = -1;

        public BucketQueue(int buckets, int capacity)
        {
            _head = new int[buckets];
            _tail = new int[buckets];
            Array.Fill(_head, -1);
            Array.Fill(_tail, -1);
            _pixel = new int[capacity];
            _seed = new int[capacity];
            _level = new float[capacity];
            _next = new int[capacity];
        }

        public void Push(int bucket, int pixel, int seed, float level)
        {
            int e;
            if (_free >= 0)
            {
                e = _free;
                _free = _next[e];
            }
            else
            {
                if (_count == _pixel.Length)
                {
                    var size = _pixel.Length * 2;
                    Array.Resize(ref _pixel, size);
                    Array.Resize(ref _seed, size);
                    Array.Resize(ref _level, size);
                    Array.Resize(ref _next, size);
                }
                e = _count++;
            }
            _pixel[e] = pixel;
            _seed[e] = seed;
            _level[e] = level;
            _next[e] = -1;
            if (_tail[bucket] >= 0)
                _next[_tail[bucket]] = e;
            else
                _head[bucket] = e;
            _tail[bucket] = e;
            // A minimax level never falls below the level being expanded, so the cursor only moves forward —
            // except for a fresh seed pushed before the walk began.
            if (bucket < _current)
                _current = bucket;
        }

        public bool TryPop(out int pixel, out int seed, out float level)
        {
            while (_current < _head.Length && _head[_current] < 0)
                _current++;
            if (_current >= _head.Length)
            {
                pixel = seed = 0;
                level = 0;
                return false;
            }
            var e = _head[_current];
            _head[_current] = _next[e];
            if (_head[_current] < 0)
                _tail[_current] = -1;
            pixel = _pixel[e];
            seed = _seed[e];
            level = _level[e];
            _next[e] = _free;
            _free = e;
            return true;
        }
    }
}
