using System.Globalization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>A decimated mesh and what it cost: the triangles before and after, the deviation measured between the two surfaces (the
/// largest distance from any vertex of either mesh to the other mesh's surface, in the mesh's units), and whether the deviation
/// limit stopped it before the triangle budget.</summary>
public sealed record DecimationResult(MeshData Mesh, int[] Colours, int TrianglesBefore, int TrianglesAfter, double DeviationM,
    bool StoppedAtError, string Words);

/// <summary>Quadric edge-collapse decimation (Garland and Heckbert's plane quadrics, collapsing an edge onto one of its own
/// vertices, so no vertex ever moves and its UV, normal and colour stay its own). Positions are welded first (copies with the same
/// attributes are one vertex); a vertex on a boundary, on a UV or normal seam (one position, copies whose attributes differ) or on a
/// non-manifold edge is never removed, so boundaries and seams keep their exact shape. A collapse is refused when it would join two
/// fans that touch elsewhere (it would make the mesh non-manifold), turn a remaining face by more than 60°, make one degenerate or
/// leave a vertex with more than 16 neighbours. Collapses run cheapest first until the triangle budget is met or the next one's
/// quadric error (a sum of squared plane distances) passes the square of the deviation limit. The deviation reported is measured
/// afterwards (<see cref="Measure"/>): what the result is, not what the quadrics estimated; the words say when it passes the limit
/// or the budget was not met.</summary>
public static class MeshDecimator
{
    /// <summary>A collapse turning a remaining face further than this (cosine of 60°) is refused.</summary>
    private const double MinNormalCosine = 0.5;

    /// <summary>A collapse leaving its vertex with more neighbours than this is refused: on a flat region every collapse costs
    /// nothing, and without the cap they pile onto one vertex (a fan whose every later collapse walks all of it).</summary>
    private const int MaxValence = 16;

    /// <summary>Equal costs (a flat region) are ordered shortest edge first, by this weight on the squared length — far below any
    /// deviation limit, so it never changes what the limit allows.</summary>
    private const double LengthTieBreak = 1e-9;

    public static DecimationResult Decimate(MeshData mesh, int targetTriangles, double maxErrorM, int[]? colours = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegative(targetTriangles);
        colours ??= [];
        var before = mesh.TriangleCount;
        if (before <= targetTriangles)
            return new DecimationResult(mesh, colours, before, before, 0, false, string.Create(CultureInfo.InvariantCulture,
                $"{before:N0} triangles, within the budget of {targetTriangles:N0}: kept as they are"));

        // Weld attribute vertices by position.
        var nAttr = mesh.VertexCount;
        var weldOf = new int[nAttr];
        var same = new int[nAttr];
        var welded = new Dictionary<(float, float, float), int>(nAttr);
        var attrOf = new List<int>(nAttr);
        var copies = new List<int>(nAttr);
        var pp = mesh.Positions;
        for (var v = 0; v < nAttr; v++)
        {
            var key = (pp[v * 3], pp[v * 3 + 1], pp[v * 3 + 2]);
            if (!welded.TryGetValue(key, out var w))
            {
                w = attrOf.Count;
                welded[key] = w;
                attrOf.Add(v);
                copies.Add(0);
            }
            weldOf[v] = w;
            // A copy whose UV, normal and colour are the first's is the same vertex (an unindexed or flat-shaded file); only a
            // copy that differs is a seam.
            if (SameAttributes(mesh, colours, attrOf[w], v))
            {
                same[v] = attrOf[w];
            }
            else
            {
                same[v] = v;
                copies[w]++;
            }
        }
        welded.Clear();
        var nw = attrOf.Count;
        var pos = new Vec3d[nw];
        for (var w = 0; w < nw; w++)
            pos[w] = mesh.Position(attrOf[w]);

        // Faces as attribute corners; faces that collapse already on welding are dropped.
        var faces = new int[before * 3];
        var dead = new bool[before];
        var live = 0;
        for (var t = 0; t < before; t++)
        {
            var (a, b, c) = mesh.Triangle(t);
            faces[t * 3] = same[a];
            faces[t * 3 + 1] = same[b];
            faces[t * 3 + 2] = same[c];
            if (weldOf[a] == weldOf[b] || weldOf[b] == weldOf[c] || weldOf[a] == weldOf[c])
                dead[t] = true;
            else
                live++;
        }
        var adj = new List<int>[nw];
        var degree = new int[nw];
        for (var t = 0; t < before; t++)
            if (!dead[t])
                for (var k = 0; k < 3; k++)
                    degree[weldOf[faces[t * 3 + k]]]++;
        for (var w = 0; w < nw; w++)
            adj[w] = new List<int>(degree[w]);
        for (var t = 0; t < before; t++)
            if (!dead[t])
                for (var k = 0; k < 3; k++)
                    adj[weldOf[faces[t * 3 + k]]].Add(t);

        int W(int t, int k) => weldOf[faces[t * 3 + k]];

        // Locked: seams, boundaries and non-manifold vertices.
        var locked = new bool[nw];
        var seam = new bool[nw];
        var counts = new Dictionary<int, int>();
        for (var w = 0; w < nw; w++)
        {
            seam[w] = copies[w] > 0;
            if (seam[w])
            {
                locked[w] = true;
                continue;
            }
            counts.Clear();
            foreach (var t in adj[w])
                for (var k = 0; k < 3; k++)
                {
                    var u = W(t, k);
                    if (u != w)
                        counts[u] = counts.GetValueOrDefault(u) + 1;
                }
            foreach (var c in counts.Values)
                if (c != 2)
                {
                    locked[w] = true;
                    break;
                }
        }

        // Plane quadrics (unweighted: the cost is a sum of squared distances, in metres²).
        var q = new double[nw * 10];
        for (var t = 0; t < before; t++)
        {
            if (dead[t])
                continue;
            var a = pos[W(t, 0)];
            var n = Vec3d.Cross(pos[W(t, 1)] - a, pos[W(t, 2)] - a);
            var len = n.Length;
            if (len < 1e-300)
                continue;
            n /= len;
            var d = -Vec3d.Dot(n, a);
            for (var k = 0; k < 3; k++)
                AddPlane(q, W(t, k), n, d);
        }

        var stamp = new int[nw];
        var gone = new bool[nw];
        var mergedInto = new int[nw];
        Array.Fill(mergedInto, -1);
        var heap = new PriorityQueue<(int Keep, int Remove, int SKeep, int SRemove), double>(live * 2);
        void Push(int a, int b)
        {
            var best = double.PositiveInfinity;
            int keep = -1, remove = -1;
            if (!locked[b] && !seam[a])
            {
                var c = Cost(q, a, b, pos[a]);
                if (c < best)
                {
                    best = c;
                    keep = a;
                    remove = b;
                }
            }
            if (!locked[a] && !seam[b])
            {
                var c = Cost(q, a, b, pos[b]);
                if (c < best)
                {
                    best = c;
                    keep = b;
                    remove = a;
                }
            }
            if (keep >= 0)
                heap.Enqueue((keep, remove, stamp[keep], stamp[remove]), best + LengthTieBreak * (pos[a] - pos[b]).LengthSquared);
        }
        for (var t = 0; t < before; t++)
        {
            if (dead[t])
                continue;
            for (var k = 0; k < 3; k++)
            {
                var a = W(t, k);
                var b = W(t, (k + 1) % 3);
                if (a < b)
                    Push(a, b);
            }
        }

        var limit2 = maxErrorM * maxErrorM;
        var stoppedAtError = false;
        var neighboursA = new List<int>(16);
        var neighboursB = new List<int>(16);
        var startLive = live;
        var total = Math.Max(1, live - targetTriangles);
        var collapses = 0;
        while (live > targetTriangles && heap.TryDequeue(out var e, out var cost))
        {
            if (gone[e.Keep] || gone[e.Remove] || stamp[e.Keep] != e.SKeep || stamp[e.Remove] != e.SRemove)
                continue;
            if (cost > limit2)
            {
                stoppedAtError = true;
                break;
            }
            var keep = e.Keep;
            var remove = e.Remove;
            Prune(adj[remove], dead);
            Prune(adj[keep], dead);
            // The faces on the edge, and the link condition: the two fans share exactly the edge's two opposite vertices.
            var shared = 0;
            foreach (var t in adj[remove])
                if (W(t, 0) == keep || W(t, 1) == keep || W(t, 2) == keep)
                    shared++;
            if (shared != 2)
                continue;
            Neighbours(adj[remove], remove, neighboursA, W);
            Neighbours(adj[keep], keep, neighboursB, W);
            var common = 0;
            foreach (var u in neighboursA)
                if (neighboursB.Contains(u))
                    common++;
            if (common != 2 || neighboursA.Count + neighboursB.Count - 4 > MaxValence)
                continue;
            if (!FacesHold(adj[remove], remove, keep, pos, W))
                continue;

            var keepAttr = attrOf[keep];
            foreach (var t in adj[remove])
            {
                if (W(t, 0) == keep || W(t, 1) == keep || W(t, 2) == keep)
                {
                    dead[t] = true;
                    live--;
                    continue;
                }
                for (var k = 0; k < 3; k++)
                    if (W(t, k) == remove)
                        faces[t * 3 + k] = keepAttr;
                adj[keep].Add(t);
            }
            adj[remove].Clear();
            gone[remove] = true;
            mergedInto[remove] = keep;
            for (var i = 0; i < 10; i++)
                q[keep * 10 + i] += q[remove * 10 + i];
            stamp[keep]++;
            Prune(adj[keep], dead);
            Neighbours(adj[keep], keep, neighboursB, W);
            foreach (var u in neighboursB)
                Push(Math.Min(keep, u), Math.Max(keep, u));
            if (++collapses % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(Math.Clamp((startLive - live) / (double)total, 0, 1) * 0.8);
            }
        }

        // The remaining faces, re-indexed to the attribute vertices they use.
        var map = new int[nAttr];
        Array.Fill(map, -1);
        var used = new List<int>();
        var idx = new List<uint>(live * 3);
        for (var t = 0; t < before; t++)
        {
            if (dead[t])
                continue;
            for (var k = 0; k < 3; k++)
            {
                var v = faces[t * 3 + k];
                if (map[v] < 0)
                {
                    map[v] = used.Count;
                    used.Add(v);
                }
                idx.Add((uint)map[v]);
            }
        }
        var outPos = new float[used.Count * 3];
        var outNor = mesh.HasNormals ? new float[used.Count * 3] : [];
        var outUv = mesh.HasUvs ? new float[used.Count * 2] : [];
        var outCol = colours.Length == nAttr ? new int[used.Count] : [];
        for (var i = 0; i < used.Count; i++)
        {
            var v = used[i];
            Array.Copy(mesh.Positions, v * 3, outPos, i * 3, 3);
            if (outNor.Length > 0)
                Array.Copy(mesh.Normals, v * 3, outNor, i * 3, 3);
            if (outUv.Length > 0)
                Array.Copy(mesh.Uvs, v * 2, outUv, i * 2, 2);
            if (outCol.Length > 0)
                outCol[i] = colours[v];
        }
        var result = new MeshData(outPos, outNor, outUv, [.. idx]);
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.85);
        var deviation = Measure(mesh, pos, gone, mergedInto, adj, dead, faces, weldOf, result, ct);
        progress?.Report(1);
        var inv = CultureInfo.InvariantCulture;
        var words = string.Create(inv, $"{before:N0} → {result.TriangleCount:N0} triangles, deviation {deviation * 1000:0.##} mm");
        if (stoppedAtError)
            words += string.Create(inv, $" (stopped at the {maxErrorM * 1000:0.#} mm limit before the {targetTriangles:N0} budget)");
        else if (result.TriangleCount > targetTriangles)
            words += string.Create(inv, $" (short of the {targetTriangles:N0} budget: the rest hold boundaries, seams or edges no collapse may take)");
        if (deviation > maxErrorM)
            words += string.Create(inv, $"; the measured deviation is beyond the {maxErrorM * 1000:0.#} mm limit the collapses were stopped by");
        return new DecimationResult(result, outCol, before, result.TriangleCount, deviation, stoppedAtError, words);
    }

    /// <summary>The slack the deviation search may stop within (metres), added to what it reports so the report stays a bound.</summary>
    public const double DeviationSlackM = 1e-5;

    /// <summary>An upper bound (within <see cref="DeviationSlackM"/>) of the distance between the original and the result, measured
    /// at every original vertex (to the result), at every result vertex (an original vertex, so 0) and at every result face's centre
    /// (to the original). A removed vertex is measured first to the remaining faces around the vertex that absorbed it (following
    /// the chain of collapses — part of the result, so at least its true distance), and only when that could raise the worst so far
    /// to the whole result. A search stops at the first point within the slack of the worst so far: such a point is covered by the
    /// report, and the worst is raised only by a distance measured exactly.</summary>
    private static double Measure(MeshData original, Vec3d[] pos, bool[] gone, int[] mergedInto, List<int>[] adj, bool[] dead, int[] faces,
        int[] weldOf, MeshData result, CancellationToken ct)
    {
        MeshDistance? whole = null;
        int Final(int w)
        {
            var root = w;
            while (mergedInto[root] >= 0)
                root = mergedInto[root];
            while (mergedInto[w] >= 0 && mergedInto[w] != root)
                (w, mergedInto[w]) = (mergedInto[w], root);
            return root;
        }
        var worst = 0.0;
        for (var w = 0; w < pos.Length; w++)
        {
            if (!gone[w])
                continue;
            if ((w & 65535) == 0)
                ct.ThrowIfCancellationRequested();
            var best = double.PositiveInfinity;
            foreach (var t in adj[Final(w)])
            {
                if (dead[t])
                    continue;
                var a = pos[weldOf[faces[t * 3]]];
                var b = pos[weldOf[faces[t * 3 + 1]]];
                var c = pos[weldOf[faces[t * 3 + 2]]];
                best = Math.Min(best, (MeshDistance.ClosestOnTriangle(pos[w], a, b, c) - pos[w]).Length);
            }
            if (best <= worst + DeviationSlackM)
                continue;
            whole ??= new MeshDistance(result);
            var exact = whole.Closest(pos[w], worst + DeviationSlackM).Distance;
            if (exact > worst + DeviationSlackM)
                worst = exact;
        }
        var source = new MeshDistance(original);
        for (var t = 0; t < result.TriangleCount; t++)
        {
            if ((t & 65535) == 0)
                ct.ThrowIfCancellationRequested();
            var (a, b, c) = result.Triangle(t);
            var centre = (result.Position(a) + result.Position(b) + result.Position(c)) / 3;
            var d = source.Closest(centre, worst + DeviationSlackM).Distance;
            if (d > worst + DeviationSlackM)
                worst = d;
        }
        return worst + DeviationSlackM;
    }

    /// <summary>Two attribute vertices at one position are one vertex when their UV, normal and colour agree.</summary>
    private static bool SameAttributes(MeshData m, int[] colours, int a, int b)
    {
        if (a == b)
            return true;
        if (m.HasUvs && (m.Uvs[a * 2] != m.Uvs[b * 2] || m.Uvs[a * 2 + 1] != m.Uvs[b * 2 + 1]))
            return false;
        if (m.HasNormals && (m.Normals[a * 3] != m.Normals[b * 3] || m.Normals[a * 3 + 1] != m.Normals[b * 3 + 1] || m.Normals[a * 3 + 2] != m.Normals[b * 3 + 2]))
            return false;
        return colours.Length != m.VertexCount || colours[a] == colours[b];
    }

    private static void AddPlane(double[] q, int v, Vec3d n, double d)
    {
        var o = v * 10;
        q[o] += n.X * n.X;
        q[o + 1] += n.X * n.Y;
        q[o + 2] += n.X * n.Z;
        q[o + 3] += n.X * d;
        q[o + 4] += n.Y * n.Y;
        q[o + 5] += n.Y * n.Z;
        q[o + 6] += n.Y * d;
        q[o + 7] += n.Z * n.Z;
        q[o + 8] += n.Z * d;
        q[o + 9] += d * d;
    }

    /// <summary>vᵀ(Qa + Qb)v at the point p.</summary>
    private static double Cost(double[] q, int a, int b, Vec3d p)
    {
        int oa = a * 10, ob = b * 10;
        double Q(int i) => q[oa + i] + q[ob + i];
        var c = Q(0) * p.X * p.X + 2 * Q(1) * p.X * p.Y + 2 * Q(2) * p.X * p.Z + 2 * Q(3) * p.X
            + Q(4) * p.Y * p.Y + 2 * Q(5) * p.Y * p.Z + 2 * Q(6) * p.Y
            + Q(7) * p.Z * p.Z + 2 * Q(8) * p.Z + Q(9);
        return Math.Max(c, 0);
    }

    private static void Prune(List<int> faces, bool[] dead) => faces.RemoveAll(t => dead[t]);

    private static void Neighbours(List<int> faces, int self, List<int> into, Func<int, int, int> w)
    {
        into.Clear();
        foreach (var t in faces)
            for (var k = 0; k < 3; k++)
            {
                var u = w(t, k);
                if (u != self && !into.Contains(u))
                    into.Add(u);
            }
    }

    /// <summary>Every face of <paramref name="remove"/> that survives the collapse keeps its facing (within 60°) and an area.</summary>
    private static bool FacesHold(List<int> faces, int remove, int keep, Vec3d[] pos, Func<int, int, int> w)
    {
        foreach (var t in faces)
        {
            int a = w(t, 0), b = w(t, 1), c = w(t, 2);
            if (a == keep || b == keep || c == keep)
                continue;
            var before = Vec3d.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            Vec3d P(int v) => v == remove ? pos[keep] : pos[v];
            var after = Vec3d.Cross(P(b) - P(a), P(c) - P(a));
            var lb = before.Length;
            var la = after.Length;
            if (la < 1e-12 * Math.Max(1, lb) || la < 1e-18)
                return false;
            if (lb > 0 && Vec3d.Dot(before, after) < MinNormalCosine * lb * la)
                return false;
        }
        return true;
    }
}
