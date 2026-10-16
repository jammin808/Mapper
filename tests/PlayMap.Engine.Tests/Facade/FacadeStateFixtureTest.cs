using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>tests/fixtures/state/facade.json is the facade's STATE keys from a real scenario — a georeferenced model imported, 14 control
/// points surveyed and checked, a far projector given a lens prior and solved from flat pins (the zoom locked from the lens), a twin
/// stacked on it (not yet aligned), and the guide's next step — and
/// facade.cards.json the cards the desk draws from them. This test writes both (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the
/// committed files are what the engine writes now, ids made stable; every card's path and fields resolve in the fixture and every
/// action is a line of the catalogue.</summary>
public sealed partial class FacadeStateFixtureTest
{
    private static readonly JsonSerializerOptions Indented = FixtureJson.Indented;

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static JsonObject Rule(string kind, string path, string light) => new() { [kind] = path, ["light"] = light };

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Site",
            ["path"] = "stage.site",
            ["light"] = new JsonArray(Rule("if", "crs", "green"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{crs} {name} · mains {mainsHz} Hz",
            ["fields"] = new JsonArray(Field("System", "crs"), Field("Name", "name"), Field("Origin", "origin"), Field("Rotation", "rotationDeg"),
                Field("Grid scale", "gridScale"), Field("Mains", "mainsHz")),
            ["actions"] = new JsonArray("SITE", "SITE CHECK", "SITE MAINS 50", "SITE MAINS 60"),
        },
        new JsonObject
        {
            ["title"] = "Survey",
            ["path"] = "stage.surveys.0",
            ["light"] = new JsonArray(Rule("if", "mesh", "green"), new JsonObject { ["light"] = "amber" }),
            ["words"] = "{name} ({format}) · {state}",
            ["fields"] = new JsonArray(Field("Name", "name"), Field("Format", "format"), Field("State", "state"), Field("Points", "points"),
                Field("Triangles", "triangles"), Field("Mesh", "mesh")),
            ["actions"] = new JsonArray("SURVEY LIST", "SITE CHECK $(id)"),
        },
        new JsonObject
        {
            ["title"] = "Solve uncertainty",
            ["path"] = "stage.projectors.0.uncertainty",
            ["light"] = new JsonArray(Rule("if", "weak", "amber"), new JsonObject { ["light"] = "green" }),
            ["words"] = "on the wall ±{wallPx} px · distance ±{axisM} m · zoom ±{focalPct} % {weak}",
            ["fields"] = new JsonArray(Field("On the wall (px)", "wallPx"), Field("Along the axis (m)", "axisM"), Field("Zoom (%)", "focalPct"),
                Field("Weak", "weak")),
            ["actions"] = new JsonArray("PROJECTOR SOLVE $(id)", "PROJECTOR PRIOR $(id) THROW 3 ±2", "PROJECTOR PRIOR $(id) CLEAR"),
        },
        new JsonObject
        {
            ["title"] = "Next step",
            ["path"] = "stage.site.next",
            ["light"] = new JsonArray(Rule("if", "line", "amber"), new JsonObject { ["light"] = "green" }),
            ["words"] = "{step}: {words} — {line}",
            ["fields"] = new JsonArray(Field("Step", "step"), Field("State", "state"), Field("What", "words"), Field("Line", "line")),
            ["actions"] = new JsonArray("SITE GUIDE"),
        },
        new JsonObject
        {
            ["title"] = "Stack",
            ["path"] = "stage.stacks.0",
            ["light"] = new JsonArray(Rule("if", "aligned", "green"), new JsonObject { ["light"] = "amber" }),
            ["words"] = "{name}: {members} on {master} · {residualPx} px · failover {failover}",
            ["fields"] = new JsonArray(Field("Name", "name"), Field("Members", "members"), Field("Master", "master"), Field("Aligned", "aligned"),
                Field("Matched", "matched"), Field("Residual (px)", "residualPx"), Field("Failover", "failover"), Field("Degraded", "degraded"), Field("Gain", "gain")),
            ["actions"] = new JsonArray("STACK LIST", "STACK ALIGN $(id) FROM <scan>", "STACK MATCH $(id) FROM <scan>", "REMOVE $(id)"),
        },
    ];

    [GeneratedRegex(@"\b(sv|ms|sf|cp|pj|pn|cs|sk)[0-9a-f]{8}\b")]
    private static partial Regex RandomIds();

    private static string Fixture(JsonElement state)
    {
        var stage = state.GetProperty("stage");
        var projectors = new JsonArray();
        foreach (var pj in stage.GetProperty("projectors").EnumerateArray())
        {
            projectors.Add(new JsonObject
            {
                ["id"] = pj.GetProperty("id").GetString(),
                ["uncertainty"] = JsonNode.Parse(pj.GetProperty("uncertainty").GetRawText()),
                ["priors"] = pj.GetProperty("priors").GetString(),
                ["stack"] = JsonNode.Parse(pj.GetProperty("stack").GetRawText()),
            });
        }
        var root = new JsonObject
        {
            ["stage"] = new JsonObject
            {
                ["site"] = JsonNode.Parse(stage.GetProperty("site").GetRawText()),
                ["controls"] = stage.GetProperty("controls").GetInt32(),
                ["surveys"] = JsonNode.Parse(stage.GetProperty("surveys").GetRawText()),
                ["stacks"] = JsonNode.Parse(stage.GetProperty("stacks").GetRawText()),
                ["projectors"] = projectors,
            },
        };
        var text = root.ToJsonString(Indented);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        text = RandomIds().Replace(text, m =>
        {
            if (!seen.TryGetValue(m.Value, out var stable))
            {
                stable = $"{m.Groups[1].Value}_fixture{seen.Count(kv => kv.Key.StartsWith(m.Groups[1].Value, StringComparison.Ordinal)) + 1}";
                seen[m.Value] = stable;
            }
            return stable;
        });
        return text + "\n";
    }

    private static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out var i) && i < a.Count => a[i],
                _ => null,
            };
        }
        return node;
    }

    [Fact]
    public async Task TheFacadesStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = await ControlPointVerbTests.Surveyed();
        await rig.Ok($"SITE POINTS {rig.File("points.csv")}");
        await rig.Ok("SITE MAINS 50");
        await rig.Ok("SITE CHECK");
        var far = LongThrowVerbTestsScene.Projector();
        var r = await rig.Engine.EditAsync(SiteRig.Desk, "add the far projector", p => p.WithStage(st => st with { Projectors = st.Projectors.Add(far) }));
        Assert.True(r.Changed);
        await rig.Ok($"PROJECTOR PRIOR {far.Id} THROW 3.02 ±2");
        var solve = SiteRig.Json(await rig.Ok($"PROJECTOR SOLVE {far.Id}"));
        Assert.Equal("focal", solve.GetProperty("locked")[0].GetString());
        // A second projector stacked on the far one: the stack waits for its scan, and the guide says what comes next.
        var twin = far with { Id = "pj0a0b0c0e", Name = "Far twin" };
        Assert.True((await rig.Engine.EditAsync(SiteRig.Desk, "add its twin", p => p.WithStage(st => st with { Projectors = st.Projectors.Add(twin) }))).Changed);
        await rig.Ok($"STACK NEW Front {far.Id} {twin.Id}");

        var state = rig.State();
        var fixture = Fixture(state);
        var cards = Cards().ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(FixturePath("facade.json"), fixture);
            await File.WriteAllTextAsync(FixturePath("facade.cards.json"), cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("facade.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("facade.cards.json"))).ReplaceLineEndings("\n"), cards);

        var root = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(root, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(at.AsObject().ContainsKey(field!["path"]!.GetValue<string>()), $"{card["title"]}: {field["path"]}");
            foreach (var action in card["actions"]!.AsArray())
            {
                Assert.True(WireGrammar.TryParse(action!.GetValue<string>().Replace("$(id)", "pj_test1", StringComparison.Ordinal), out var line, out _));
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }
}

/// <summary>The far projector the fixture solves: the long-throw tests' flat head-on facade.</summary>
internal static class LongThrowVerbTestsScene
{
    public static Core.Model.Projector Projector()
    {
        var k = Core.Model.Intrinsics.FromThrowRatio(3840, 2160, 3);
        var truth = Core.Model.Pose.LookAt(new Core.Numerics.Vec3d(0, 10, 100), new Core.Numerics.Vec3d(0, 10, 0), Core.Numerics.Vec3d.UnitY);
        var world = new List<Core.Numerics.Vec3d>();
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 4; c++)
            {
                var px = new Core.Numerics.Vec2d((0.1 + 0.8 * c / 3) * k.Width, (0.1 + 0.8 * r / 2) * k.Height);
                var dir = truth.CameraToWorld * Core.Geometry.CameraModel.PixelRayCamera(px, k, Core.Model.LensDistortion.None);
                world.Add(truth.Position + dir * (-truth.Position.Z / dir.Z));
            }
        return SiteRig.ProjectorWithPins("pj0a0b0c0d", k, truth, world, sigmaPx: 0.8, seed: 3) with
        {
            Intrinsics = Core.Model.Intrinsics.FromThrowRatio(3840, 2160, 3.12),
            Pose = new Core.Model.Pose(truth.Position + new Core.Numerics.Vec3d(0.5, -0.3, 2), truth.Rotation),
        };
    }
}
