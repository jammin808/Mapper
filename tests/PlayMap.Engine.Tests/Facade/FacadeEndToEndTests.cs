using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>
/// A facade end to end on the simulator, every step a line through the wire on one engine and its STATE asserted: SURVEY IMPORT of a
/// georeferenced front (the site set from it, named by its EPSG system) → SITE POINTS (six control points) → pins bound to the control
/// points, their pixels where each projector's true optics put them → PROJECTOR PRIOR and PROJECTOR SOLVE (the zoom locked from the lens on
/// the flat front) → STACK NEW, STACK ALIGN and STACK MATCH from the scan of both members (align's capture and decode; the members now at
/// their own solved poses, so the master's picture reaches the member through the surveyed model) → BLEND SOLVE (the stack blends as one
/// projector) → the score; the guide's next step follows along, and once through, STATE says so.
/// </summary>
public sealed class FacadeEndToEndTests(ITestOutputHelper log) : IAsyncDisposable
{
    private const double E0 = 512_340, N0 = 5_401_230, H0 = 120;

    private readonly ScanRunner _scans = new(null);

    public ValueTask DisposeAsync() => _scans.DisposeAsync();

    private static JsonElement Stage(SiteRig rig) => rig.State().GetProperty("stage");

    private static string Next(SiteRig rig) =>
        Stage(rig).GetProperty("site").GetProperty("next") is { ValueKind: JsonValueKind.Object } n ? n.GetProperty("step").GetString()! : "";

    [Fact]
    public async Task SurveyToScoreThroughTheWire()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        await using var rig = new SiteRig(null, s => s with { Scan = _scans });

        // 1. The surveyed front: a 12 × 12 m wall facing grid south, in ETRS89 / UTM 32N, the simulator's screen at its middle.
        var obj = string.Create(CultureInfo.InvariantCulture,
            $"v {E0 - 6} {N0} {H0 - 6}\nv {E0 + 6} {N0} {H0 - 6}\nv {E0 + 6} {N0} {H0 + 6}\nv {E0 - 6} {N0} {H0 + 6}\nf 1 2 3\nf 1 3 4\n");
        await File.WriteAllTextAsync(rig.File("front.obj"), obj);
        var job = await rig.Import($"{rig.File("front.obj")} CRS EPSG:25832 NAME Front");
        Assert.Equal("ready", job.State);
        var site = Stage(rig).GetProperty("site");
        Assert.Equal(("EPSG:25832", "ETRS89 / UTM zone 32N"), (site.GetProperty("crs").GetString(), site.GetProperty("name").GetString()));
        Assert.Equal([E0, N0, H0], site.GetProperty("origin").EnumerateArray().Select(v => v.GetDouble()));
        Assert.Equal("ready", Stage(rig).GetProperty("surveys")[0].GetProperty("state").GetString());
        Assert.Equal("controls", Next(rig));

        // 2. Six control points on the front, where both projectors light it.
        var points = new StringBuilder("Point,Easting,Northing,Elevation,Code\n");
        var stagePoints = new List<Vec3d>();
        for (var i = 0; i < 6; i++)
        {
            var x = -1.4 + 1.4 * (i % 3);
            var y = i < 3 ? -0.7 : 0.7;
            points.Append(CultureInfo.InvariantCulture, $"CP{i + 1},{E0 + x:0.000},{N0:0.000},{H0 + y:0.000},facade\n");
            stagePoints.Add(new Vec3d(x, y, 0));
        }
        await File.WriteAllTextAsync(rig.File("points.csv"), points.ToString());
        Assert.Equal(6, SiteRig.Json(await rig.Ok($"SITE POINTS {rig.File("points.csv")} ORDER PENZD")).GetProperty("added").GetInt32());
        Assert.Equal(6, Stage(rig).GetProperty("controls").GetInt32());
        Assert.Equal("pins", Next(rig));

        // 3. Two projectors placed by hand where the first really is, each on an output; pins on the control points.
        var k = sim.Projectors[0].Intrinsics;
        Projector Model(string id, string name) => new(id, name, k, LensDistortion.None, sim.Projectors[0].Pose, 10_000, "#FFFFFF", false, [],
            SolveLocks.Shift | SolveLocks.Distortion, 0);
        Assert.True((await rig.Engine.EditAsync(SiteRig.Desk, "the stack's projectors", p => p.WithStage(st => st with
        {
            Projectors = [Model(StackKit.P1, "P1"), Model(StackKit.P2, "P2")],
            Scans = StackKit.Project(sim).Stage.Scans,
        }) with
        {
            Outputs = [StackKit.OutputFor("op00000001", StackKit.P1, k.Width, k.Height), StackKit.OutputFor("op00000002", StackKit.P2, k.Width, k.Height)],
        })).Changed);
        var controls = rig.Engine.Store.Project.Stage.Controls;
        for (var j = 0; j < 2; j++)
        {
            var truth = sim.Projectors[j];
            for (var i = 0; i < 6; i++)
            {
                var w = stagePoints[i];
                var px = CameraModel.Project(w, truth.Intrinsics, LensDistortion.None, truth.Pose)!.Value;
                var cp = controls.First(c => c.Name == $"CP{i + 1}");
                await rig.Ok(string.Create(CultureInfo.InvariantCulture,
                    $"ADD pin {(j == 0 ? StackKit.P1 : StackKit.P2)} {{\"controlId\":\"{cp.Id}\",\"world\":{{\"x\":{w.X:R},\"y\":{w.Y:R},\"z\":{w.Z:R}}},\"pixel\":{{\"x\":{px.X:R},\"y\":{px.Y:R}}},\"enabled\":true}}"));
            }
        }
        Assert.Equal("solve", Next(rig));

        // 4. The lens's throw as a prior, then the solves: the flat front cannot tell zoom from distance, so the zoom is locked from the lens.
        foreach (var pj in new[] { StackKit.P1, StackKit.P2 })
        {
            await rig.Ok($"PROJECTOR PRIOR {pj} THROW 1.5 ±2");
            var solve = SiteRig.Json(await rig.Ok($"PROJECTOR SOLVE {pj}"));
            log.WriteLine(solve.GetProperty("words").GetString());
            Assert.Contains("throw", solve.GetProperty("priorsUsed").EnumerateArray().Select(x => x.GetString()));
        }
        var projectors = Stage(rig).GetProperty("projectors").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(JsonValueKind.Object, projectors[StackKit.P1].GetProperty("uncertainty").ValueKind);
        Assert.Equal("TR 1.5 ±2 %", projectors[StackKit.P2].GetProperty("priors").GetString());
        // No stack yet counts as done: the two blended outputs are next.
        Assert.Equal("blend", Next(rig));

        // 5. The stack: grouped, aligned to its master and colour-matched from the scan of both.
        var stack = SiteRig.Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        Assert.Equal("stacks", Next(rig));
        var aligned = SiteRig.Json(await rig.Ok($"STACK ALIGN {stack} FROM {StackKit.Scan}"));
        log.WriteLine(aligned.GetProperty("words").GetString());
        Assert.InRange(aligned.GetProperty("residualPx").GetDouble(), 0, 0.25);
        var matched = SiteRig.Json(await rig.Ok($"STACK MATCH {stack} FROM {StackKit.Scan}"));
        Assert.All(matched.GetProperty("members").EnumerateArray().SelectMany(m => m.GetProperty("gain").EnumerateArray()), g => Assert.InRange(g.GetDouble(), 0, 1));
        var s1 = Stage(rig).GetProperty("stacks")[0];
        Assert.True(s1.GetProperty("aligned").GetBoolean());
        Assert.True(s1.GetProperty("matched").GetBoolean());
        Assert.Equal(stack, Stage(rig).GetProperty("projectors")[1].GetProperty("stack").GetString());
        var p2 = rig.Engine.Store.Project.FindOutput("op00000002")!.Measured!;
        Assert.NotNull(p2.Lattice);
        Assert.NotNull(p2.Color);
        Assert.Equal("blend", Next(rig));

        // 6. The blend: the stack blends as one projector, each member with the group's share.
        await rig.Ok("BLEND SOLVE");
        Assert.Equal("done", (await rig.Engine.WhenSolved.WaitAsync(TimeSpan.FromMinutes(2)))!.State);
        var solved = rig.Engine.LastBlendSolve!.Solution;
        var a = solved.For("op00000001")!.Map.Weights;
        var b = solved.For("op00000002")!.Map.Weights;
        Assert.Equal(a.Length, b.Length);
        Assert.True(a.Zip(b).Count(x => Math.Abs(x.First - x.Second) < 0.02) > 0.95 * a.Length, "the members share the group's weights");
        Assert.Contains(Next(rig), new[] { "score", "" });

        // 7. The score: every projector's current result graded; the guide is through.
        foreach (var pj in new[] { StackKit.P1, StackKit.P2 })
        {
            var status = await rig.Ok($"CALIBRATION STATUS {pj}");
            log.WriteLine(status);
        }
        var guide = SiteRig.Json(await rig.Ok("SITE GUIDE"));
        log.WriteLine(guide.GetProperty("words").GetString());
        Assert.All(guide.GetProperty("steps").EnumerateArray(), s => Assert.Equal("done", s.GetProperty("state").GetString()));
        Assert.Equal(JsonValueKind.Null, guide.GetProperty("next").ValueKind);
        Assert.Equal(JsonValueKind.Null, Stage(rig).GetProperty("site").GetProperty("next").ValueKind);
        Assert.Equal(guide.GetRawText(), SiteRig.Json(await rig.Ok("FACADE GUIDE")).GetRawText());
    }
}
