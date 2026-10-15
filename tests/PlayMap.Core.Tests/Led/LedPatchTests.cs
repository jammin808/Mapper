using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Led;

/// <summary>Layouts, where a fixture's channels land, the auto-patch (deterministic patches for a fixture set), overlaps reported
/// with both fixtures, and the bandwidth estimate and guard.</summary>
public sealed class LedPatchTests
{
    internal const string Canvas = "cv00000001";

    internal static LedNode Node(string id, LedProtocol protocol = LedProtocol.ArtNet, string host = "10.0.0.21", string vendor = "") =>
        new(id, "Node " + id[^1], protocol, host, 0, true, 0, 0, LedSync.Latch, 510, "#FFFFFF") { Vendor = vendor };

    internal static LedFixture Line(string id, string type, int count, LedPatch? patch = null, double y = 0, LedSpace space = LedSpace.Canvas,
        string spaceId = Canvas) =>
        new(id, "Fixture " + id[^2..], type, space, spaceId, Transform3.Identity with { Position = new Vec3d(0, y, 0) },
            new LedLayout(LedShape.Line, count, 1, new Vec3d(count - 1, 0, 0), LedStart.TopLeft, false, false, []), patch, LedSampling.Point, 0,
            LedColor.Neutral, true, false, false, "#FF8800", []);

    internal static Project Show(IEnumerable<LedNode> nodes, IEnumerable<LedFixture> fixtures, int w = 640, int h = 360)
    {
        var p = Project.CreateDefault("LED");
        return p with
        {
            Canvases = [new Canvas(Canvas, "Wall", w, h, "#000000", [])],
            Leds = p.Leds with { Nodes = [.. nodes], Fixtures = [.. fixtures] },
        };
    }

    [Fact]
    public void ALineRunsEndToEndAndAGridSnakesFromItsCorner()
    {
        var line = LedLayouts.Local(new LedLayout(LedShape.Line, 5, 1, new Vec3d(8, 0, 0), LedStart.TopLeft, false, false, []));
        Assert.Equal([0.0, 2, 4, 6, 8], line.Select(p => p.X));
        var grid = LedLayouts.Local(new LedLayout(LedShape.Grid, 3, 2, new Vec3d(2, 1, 0), LedStart.BottomRight, false, true, []));
        // From the bottom right, rows first, the second row back the other way.
        Assert.Equal([(2.0, 1.0), (1, 1), (0, 1), (0, 0), (1, 0), (2, 0)], grid.Select(p => (p.X, p.Y)));
        var columns = LedLayouts.Local(new LedLayout(LedShape.Grid, 2, 2, new Vec3d(1, 1, 0), LedStart.TopLeft, true, false, []));
        Assert.Equal([(0.0, 0.0), (0, 1), (1, 0), (1, 1)], columns.Select(p => (p.X, p.Y)));
        var ring = LedLayouts.Local(new LedLayout(LedShape.Ring, 4, 1, new Vec3d(2, 0, 0), LedStart.TopLeft, false, false, []));
        // Clockwise with y down: from angle 0 towards +y.
        Assert.Equal([(1.0, 0.0), (0, 1), (-1, 0), (0, -1)], ring.Select(p => (Math.Round(p.X, 9), Math.Round(p.Y, 9))));
        var placed = LedLayouts.Placed(Line("lf00000001", "builtin:rgb", 3, y: 10) with { Reverse = true });
        Assert.Equal([(2.0, 10.0), (1, 10), (0, 10)], placed.Select(p => (p.X, p.Y)));
    }

    [Fact]
    public void APixelNeverStraddlesAUniverse()
    {
        var node = Node("ln00000001");
        var uses = new List<LedSlotUse>();
        Assert.True(LedPatcher.TryPlace(node, LedBuiltinTypes.Find("rgb")!, 171, 0, 1, uses, out var u, out var a, out _));
        Assert.Equal((0, 510), (uses[509].Universe, uses[509].Address));
        Assert.Equal((1, 1, 170, LedChannel.R), (uses[510].Universe, uses[510].Address, uses[510].Pixel, uses[510].Channel));
        Assert.Equal((1, 4), (u, a));
        uses.Clear();
        Assert.True(LedPatcher.TryPlace(node, LedBuiltinTypes.Find("rgbw")!, 128, 0, 1, uses, out _, out _, out _));
        // 127 RGBW pixels fill 508 slots; the 128th starts the next universe.
        Assert.Equal((0, 508), (uses[507].Universe, uses[507].Address));
        Assert.Equal((1, 1, 127), (uses[508].Universe, uses[508].Address, uses[508].Pixel));
        uses.Clear();
        Assert.True(LedPatcher.TryPlace(node, LedBuiltinTypes.Find("rgb16")!, 1, 0, 1, uses, out _, out _, out _));
        Assert.Equal([(LedChannel.R, 1), (LedChannel.R, 2), (LedChannel.G, 1), (LedChannel.G, 2), (LedChannel.B, 1), (LedChannel.B, 2)],
            uses.Select(x => (x.Channel, x.Part)));
        Assert.False(LedPatcher.TryPlace(node with { UniverseChannels = 2 }, LedBuiltinTypes.Find("rgb")!, 1, 0, 1, null, out _, out _, out var why));
        Assert.Equal("3 channels do not fit a universe of 2", why);
        Assert.False(LedPatcher.TryPlace(node, LedBuiltinTypes.Find("rgb")!, 171, 32767, 1, null, out _, out _, out why));
        Assert.Equal("the fixture runs past universe 32767", why);
    }

    [Fact]
    public void ADdpDeviceIsOneBufferInRowsOfFiveHundredAndTwelve()
    {
        var node = Node("ln00000001", LedProtocol.Ddp, "10.0.0.30");
        var uses = new List<LedSlotUse>();
        Assert.True(LedPatcher.TryPlace(node, LedBuiltinTypes.Find("rgb")!, 200, 0, 1, uses, out _, out var next, out _));
        Assert.Equal(600, uses.Count);
        // Byte 513 is row 1, slot 1; pixels straddle rows freely (a DDP buffer has no universes).
        Assert.Equal((1, 1), (uses[512].Universe, uses[512].Address));
        Assert.Equal(601, next);
    }

    [Fact]
    public void TheAutoPatchIsDeterministic()
    {
        var nodes = new[] { Node("ln00000001") };
        var fixtures = new[]
        {
            Line("lf00000001", "builtin:rgb", 170),
            Line("lf00000002", "builtin:rgbw", 100),
            Line("lf00000003", "builtin:rgb16", 50),
            Line("lf00000004", "builtin:grb", 10),
        };
        var rig = Show(nodes, fixtures).Leds;
        Assert.True(LedPatcher.TryAutoPatch(rig, fixtures, nodes, 0, 1, false, out var patches, out var why), why);
        Assert.Equal(
            [("lf00000001", new LedPatch("ln00000001", 0, 1)), ("lf00000002", new LedPatch("ln00000001", 1, 1)),
             ("lf00000003", new LedPatch("ln00000001", 1, 401)), ("lf00000004", new LedPatch("ln00000001", 2, 193))],
            patches.ToArray());
        Assert.True(LedPatcher.TryAutoPatch(rig, fixtures, nodes, 0, 1, true, out patches, out why), why);
        Assert.Equal([0, 1, 2, 3], patches.Select(x => x.Patch.Universe));
        Assert.All(patches, x => Assert.Equal(1, x.Patch.Address));
        // The same set patched twice gives the same patches, and the plan places them with no overlap.
        var p = Show(nodes, fixtures.Select(f => f with { Patch = patches.First(x => x.FixtureId == f.Id).Patch }));
        var plan = LedPlanBuilder.Build(p, 200, 1);
        Assert.Empty(plan.Conflicts);
        Assert.Equal([0, 1, 2, 3], plan.Rows.Select(r => r.Universe));
        Assert.Equal([510, 400, 300, 30], plan.Rows.Select(r => r.Used));
    }

    [Fact]
    public void AFixtureThatRunsPastANodeMovesToTheNextOrIsRefused()
    {
        var nodes = new[] { Node("ln00000001"), Node("ln00000002", host: "10.0.0.22") };
        var fixtures = new[] { Line("lf00000001", "builtin:rgb", 170), Line("lf00000002", "builtin:rgb", 170) };
        var rig = Show(nodes, fixtures).Leds;
        Assert.True(LedPatcher.TryAutoPatch(rig, fixtures, nodes, 32767, 1, false, out var patches, out var why), why);
        Assert.Equal([new LedPatch("ln00000001", 32767, 1), new LedPatch("ln00000002", 0, 1)], patches.Select(x => x.Patch));
        Assert.False(LedPatcher.TryAutoPatch(rig, fixtures, nodes[..1], 32767, 1, false, out _, out why));
        Assert.Equal("Fixture 02 does not fit on Node 1 from 32767.511 (the fixture runs past universe 32767) and there is no next node — name another with NODE", why);
    }

    [Fact]
    public void AWledDeviceTakesEightBitTypesOnly()
    {
        var wled = Node("ln00000001", LedProtocol.Ddp, "10.0.0.40", vendor: "wled");
        var deep = Line("lf00000001", "builtin:rgb16", 10, new LedPatch(wled.Id, 0, 1));
        Assert.False(LedPatcher.TryAutoPatch(Show([wled], [deep]).Leds, [deep], [wled], 0, 1, false, out _, out var why));
        Assert.Contains("WLED", why, StringComparison.Ordinal);
        var plan = LedPlanBuilder.Build(Show([wled], [deep]), 200, 1);
        Assert.Empty(plan.Rows);
        Assert.Contains(plan.Warnings, w => w.Contains("8 bits a channel", StringComparison.Ordinal));
        var shallow = Line("lf00000002", "builtin:rgbw", 10, new LedPatch(wled.Id, 0, 1));
        Assert.Equal(Ddp.Rgbw8, LedPlanBuilder.Build(Show([wled], [shallow]), 200, 1).Nodes[0].DdpType);
    }

    [Fact]
    public void AnOverlapIsReportedWithBothFixtures()
    {
        var node = Node("ln00000001");
        var p = Show([node], [Line("lf00000001", "builtin:rgb", 16, new LedPatch(node.Id, 3, 1)), Line("lf00000002", "builtin:rgb", 16, new LedPatch(node.Id, 3, 25), 5)]);
        var plan = LedPlanBuilder.Build(p, 200, 1);
        var c = Assert.Single(plan.Conflicts);
        Assert.Equal((node.Id, 3, 25, 48), (c.NodeId, c.Universe, c.From, c.To));
        Assert.Equal(["lf00000001", "lf00000002"], c.Fixtures.ToArray());
        // The first claim keeps the slots: the second fixture's first eight pixels are not sent there.
        Assert.Equal(0f, plan.Patch[(0 * 512 + 24) * 4 + 2]);
    }

    [Fact]
    public void TheBandwidthEstimateCountsEveryHeaderAndTheGuardRefuses()
    {
        var node = Node("ln00000001");
        var p = Show([node], [Line("lf00000001", "builtin:rgb", 170, new LedPatch(node.Id, 0, 1))]);
        var plan = LedPlanBuilder.Build(p, 200, 1);
        // (18 + 510 + 42) bytes × 8 × 44 Hz.
        Assert.Equal(Math.Round(570 * 8 * 44 / 1e6, 3), plan.Mbps);
        Assert.Equal("", plan.Refused);
        var refused = LedPlanBuilder.Build(p, 0, 2);
        Assert.StartsWith("0.2 Mb/s is over this machine's 0 Mb/s guard (1 universes at 44 Hz to Node 1)", refused.Refused, StringComparison.Ordinal);
        var sacn = Node("ln00000002", LedProtocol.Sacn, "");
        Assert.Equal(Math.Round((126 + 3 + 42) * 8 * 44 / 1e6, 3),
            LedPlanBuilder.Build(Show([sacn], [Line("lf00000001", "builtin:rgb", 1, new LedPatch(sacn.Id, 1, 1))]), 200, 1).Mbps);
    }

    [Fact]
    public void PixelsAreGroupedByWhatTheySampleAndFixturesStayWhole()
    {
        var a = Line("lf00000001", "builtin:rgb", 3);
        var b = Line("lf00000002", "builtin:rgb", 2, space: LedSpace.Source, spaceId: "color:#FF0000");
        var c = Line("lf00000003", "builtin:rgb", 4, y: 2);
        var plan = LedPlanBuilder.Build(Show([], [a, b, c]), 200, 1);
        Assert.Equal(2, plan.Groups.Length);
        Assert.Equal((LedBinding.Canvas, 0, 7), (plan.Groups[0].Binding, plan.Groups[0].First, plan.Groups[0].Count));
        Assert.Equal((LedBinding.Source, 7, 2), (plan.Groups[1].Binding, plan.Groups[1].First, plan.Groups[1].Count));
        Assert.Equal([(0, 0, 3), (1, 7, 2), (2, 3, 4)], plan.Fixtures.Select(f => (f.Index, f.FirstPixel, f.Pixels)));
        // A 2D position names a pixel's centre: x = 2 on a 640-wide canvas samples at (2.5 / 640).
        Assert.Equal(2.5f / 640, plan.Attributes[2 * 4], 7);
        Assert.Equal((2 + 0.5f) / 360, plan.Attributes[3 * 4 + 1], 7);
    }

    [Fact]
    public void AFixtureThatCannotBeSampledSaysWhy()
    {
        var plan = LedPlanBuilder.Build(Show([], [Line("lf00000001", "builtin:rgb", 3, spaceId: "cv0000000f"),
            Line("lf00000002", "builtin:nope", 3)]), 200, 1);
        Assert.Contains(plan.Warnings, w => w.Contains("samples a canvas the show does not have (cv0000000f) — black", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("names a type this show does not have (builtin:nope)", StringComparison.Ordinal));
        Assert.True(plan.Attributes[0] < 0);
    }

    [Fact]
    public void FramesAreDecimatedOnTheLeaderGridTheSameEverywhere()
    {
        // 44 Hz: 60 frames a second at leader instants k/60 s send on 44 distinct steps.
        var steps = Enumerable.Range(0, 60).Select(k => LedPrograms.StepOf(1_760_000_000_000_000_000L + k * 1_000_000_000L / 60, 44_000)).ToList();
        var sent = steps.Where((s, i) => i == 0 || s != steps[i - 1]).Count();
        Assert.InRange(sent, 44, 45);
        Assert.Equal(-1, LedPrograms.StepOf(-1, 1000));
    }
}
