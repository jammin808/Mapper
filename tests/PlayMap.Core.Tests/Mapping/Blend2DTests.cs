using PlayMap.Core.Mapping;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>Hand-set soft edges: every curve's two sides add to one in light at every gamma, a 2 × 2's
/// shared corner adds to one, the black pedestal brings every region to the deepest overlap's floor, and
/// the audit reads each join.</summary>
public class Blend2DTests
{
    private static readonly double[] Gammas = [0.5, 1.0, 1.8, 2.2, 2.6, 3.0];

    [Fact]
    public void TheTwoSidesOfAJoinSumToOneInLightForEveryCurveAndGamma()
    {
        foreach (var curve in Enum.GetValues<BlendCurve>())
            foreach (var gamma in Gammas)
                for (var i = 0; i <= 100; i++)
                {
                    var t = i / 100.0;
                    var a = BlendMath.ToLight(BlendMath.Weight(curve, t, gamma), gamma);
                    var b = BlendMath.ToLight(BlendMath.Weight(curve, 1 - t, gamma), gamma);
                    Assert.Equal(1, a + b, 12);
                }
    }

    [Fact]
    public void EveryCurveRisesFromNothingToEverything()
    {
        foreach (var curve in Enum.GetValues<BlendCurve>())
        {
            Assert.Equal(0, BlendMath.Curve(curve, 0), 12);
            Assert.Equal(1, BlendMath.Curve(curve, 1), 12);
            Assert.Equal(0.5, BlendMath.Curve(curve, 0.5), 12);
            for (var i = 0; i < 100; i++)
                Assert.True(BlendMath.Curve(curve, (i + 1) / 100.0) >= BlendMath.Curve(curve, i / 100.0));
        }
    }

    private static BlendSettings Manual(EdgeBlend edges, BlendCurve curve = BlendCurve.Cosine, double gamma = 2.2, double black = 0) =>
        new(false, edges, curve, gamma, black, false);

    [Fact]
    public void TwoMapsSharingAnOverlapSumToOneInLightAcrossIt()
    {
        // Two 200 px outputs overlapping by 60: A's right zone and B's left zone are the same pixels.
        foreach (var curve in Enum.GetValues<BlendCurve>())
        {
            var a = BlendMath.EdgeBlendMap("a", 200, 4, Manual(new EdgeBlend(0, 0, 60, 0), curve));
            var b = BlendMath.EdgeBlendMap("b", 200, 4, Manual(new EdgeBlend(60, 0, 0, 0), curve));
            for (var x = 0; x < 200; x++)
            {
                var la = BlendMath.ToLight(a.WeightAt(x, 2), 2.2);
                var expected = x < 140 ? 1.0 : la + BlendMath.ToLight(b.WeightAt(x - 140, 2), 2.2);
                Assert.Equal(1, x < 140 ? la : expected, 5);
            }
        }
    }

    [Fact]
    public void TheCornerFourOutputsShareSumsToOne()
    {
        // A 2 × 2 of 100 × 80 outputs overlapping by 30 across and 20 down.
        var tl = BlendMath.EdgeBlendMap("tl", 100, 80, Manual(new EdgeBlend(0, 0, 30, 20)));
        var tr = BlendMath.EdgeBlendMap("tr", 100, 80, Manual(new EdgeBlend(30, 0, 0, 20)));
        var bl = BlendMath.EdgeBlendMap("bl", 100, 80, Manual(new EdgeBlend(0, 20, 30, 0)));
        var br = BlendMath.EdgeBlendMap("br", 100, 80, Manual(new EdgeBlend(30, 20, 0, 0)));
        double L(BlendMap m, int x, int y) => BlendMath.ToLight(m.WeightAt(x, y), 2.2);
        // Canvas pixel (cx, cy); TL's origin (0,0), TR's (70,0), BL's (0,60), BR's (70,60).
        for (var cy = 60; cy < 80; cy++)
            for (var cx = 70; cx < 100; cx++)
            {
                var sum = L(tl, cx, cy) + L(tr, cx - 70, cy) + L(bl, cx, cy - 60) + L(br, cx - 70, cy - 60);
                Assert.Equal(1, sum, 5);
            }
    }

    [Fact]
    public void TheBlackPedestalBringsEveryRegionToTheDeepestFloor()
    {
        const double black = 0.02;
        // The rule's own arithmetic: a 2 × 2 has a floor of four blacks everywhere.
        Assert.Equal(3 * black, BlackLevel.Light(black, 1, 4), 12);     // alone: 1 + 3 = 4
        Assert.Equal(1 * black, BlackLevel.Light(black, 2, 4), 12);     // a band: 2 + 2 × 1 = 4
        Assert.Equal(0, BlackLevel.Light(black, 4, 4), 12);             // the corner: 4
        Assert.Equal(1 * black, BlackLevel.Light(black, 1, 2), 12);     // a row: 1 + 1 = 2

        // And on the maps: every canvas pixel of a 2 × 2 sits on 4 blacks, counting the pedestal's light.
        var s = new[]
        {
            (Map: BlendMath.EdgeBlendMap("tl", 100, 80, Manual(new EdgeBlend(0, 0, 30, 20), black: black)), Ox: 0, Oy: 0),
            (Map: BlendMath.EdgeBlendMap("tr", 100, 80, Manual(new EdgeBlend(30, 0, 0, 20), black: black)), Ox: 70, Oy: 0),
            (Map: BlendMath.EdgeBlendMap("bl", 100, 80, Manual(new EdgeBlend(0, 20, 30, 0), black: black)), Ox: 0, Oy: 60),
            (Map: BlendMath.EdgeBlendMap("br", 100, 80, Manual(new EdgeBlend(30, 20, 0, 0), black: black)), Ox: 70, Oy: 60),
        };
        for (var cy = 0; cy < 140; cy += 3)
            for (var cx = 0; cx < 170; cx += 3)
            {
                var floor = 0.0;
                foreach (var (map, ox, oy) in s)
                {
                    var x = cx - ox;
                    var y = cy - oy;
                    if (x < 0 || y < 0 || x >= 100 || y >= 80)
                        continue;
                    floor += black + BlendMath.ToLight(map.Pedestal[y * 100 + x], 2.2);
                }
                Assert.Equal(4 * black, floor, 1e-7);
            }
        Assert.Equal(9, BlackLevel.Cells(100, 80, new EdgeBlend(30, 20, 30, 20)).Count);
        Assert.Equal(new[] { 2, 1, 2 }, BlackLevel.Cells(200, 100, new EdgeBlend(40, 0, 40, 0)).Select(c => c.Coverage));
    }

    [Fact]
    public void AnOutputWithNoEdgesPassesEverythingAndLiftsNothing()
    {
        var map = BlendMath.EdgeBlendMap("o", 64, 36, Manual(EdgeBlend.None, black: 0.05));
        Assert.All(map.Weights, w => Assert.Equal(1f, w));
        Assert.All(map.Pedestal, p => Assert.Equal(0f, p));
    }

    private static Output Out(string id, RectD plan, EdgeBlend edges, BlendCurve curve = BlendCurve.Cosine) =>
        new(id, id.ToUpperInvariant(), OutputKind.Display, "", (int)plan.W, (int)plan.H, 60, OutputMode.Canvas, null, SourceRef.None,
            new RectD(0, 0, plan.W, plan.H), OutputWarp.None, Manual(edges, curve), ColorTrim.Neutral, [], plan, 0, true);

    [Fact]
    public void TheAuditReadsEachJoin()
    {
        var a = Out("a", new RectD(0, 0, 1920, 1080), new EdgeBlend(0, 0, 320, 0));
        var b = Out("b", new RectD(1600, 0, 1920, 1080), new EdgeBlend(320, 0, 0, 0));
        Assert.Equal(new EdgeBlend(0, 0, 320, 0), BlendAudit.Derive(a, [a, b]));
        var good = BlendAudit.For(a, [a, b]);
        Assert.Single(good);
        Assert.False(good[0].Warning);
        Assert.Contains("Right join with B: 320 px, both fade", good[0].Text, StringComparison.Ordinal);

        var narrow = b with { Blend = b.Blend with { Edges = new EdgeBlend(300, 0, 0, 0) } };
        Assert.True(BlendAudit.For(a, [a, narrow]).Single().Warning);
        var none = b with { Blend = b.Blend with { Edges = EdgeBlend.None } };
        Assert.Contains("does not fade its left edge", BlendAudit.For(a, [a, none]).Single().Text, StringComparison.Ordinal);
        var curve = b with { Blend = b.Blend with { Curve = BlendCurve.Linear } };
        Assert.Contains("same curve", BlendAudit.For(a, [a, curve]).Single().Text, StringComparison.Ordinal);
        var wide = a with { Blend = a.Blend with { Edges = new EdgeBlend(1000, 0, 1000, 0) } };
        Assert.Contains(BlendAudit.For(wide, [wide]), n => n.Warning && n.Text.Contains("no picture between them", StringComparison.Ordinal));
        // A pure-corner overlap (a diagonal neighbour in a 2 × 2) is no join of its own.
        Assert.Equal(BlendEdge.None, BlendAudit.EdgeOf(new RectD(0, 0, 100, 100), new RectD(80, 80, 100, 100), out _));
    }
}
