using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests;

/// <summary>The k-d tree answers exactly what a brute-force search answers.</summary>
public class KdTreeTests
{
    [Fact]
    public void NearestKNearestAndRadiusMatchBruteForce()
    {
        var rng = new Pcg32(11);
        var pts = Enumerable.Range(0, 2000).Select(_ => new Vec3d(rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 0.2)).ToList();
        pts.Add(PointCloud.Invalid);
        var tree = new KdTree3(pts);
        Assert.Equal(2000, tree.Count);
        for (var t = 0; t < 50; t++)
        {
            var q = new Vec3d(rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 0.2);
            var brute = Enumerable.Range(0, 2000).OrderBy(i => (pts[i] - q).LengthSquared).ToArray();
            Assert.Equal(brute[0], tree.Nearest(q, out _));
            Assert.Equal(brute.Take(8), tree.KNearest(q, 8));
            var inR = Enumerable.Range(0, 2000).Where(i => (pts[i] - q).Length <= 0.05).OrderBy(i => i);
            Assert.Equal(inR, tree.Radius(q, 0.05).OrderBy(i => i));
        }
    }
}
