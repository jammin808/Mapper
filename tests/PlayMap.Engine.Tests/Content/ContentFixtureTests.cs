using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Content;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Render.Generators;

namespace PlayMap.Engine.Tests.Content;

/// <summary>STATE.content from a scenario — a particle flow settling, a Mandelbulb drawing at its measured cost, a reactive scene
/// listening and faulted, a sting surging — is the committed fixture, with the cards that read it. Preflight is green within the
/// budget, amber or red over it with the line that brings the generator down, red with the reset line when one faults, grey with
/// no renderer; the INDEX sections (a Retime key per generator, a Fire key per sting) and the context's content item come from
/// the same views.</summary>
public sealed class ContentFixtureTests
{
    /// <summary>The show: three generators with fixed ids and origins.</summary>
    private static Project Show()
    {
        var sparks = new Generator("gn_sparks", "Sparks", "particles.flow", ParamSet.Empty.With("count", 65536)) { OriginNs = 1_000_000_000 };
        var bulb = new Generator("gn_bulb", "Bulb", "fractal.mandelbulb", ParamSet.Empty.With("renderScale", 0.5)) { OriginNs = 2_000_000_000 };
        var waves = new Generator("gn_waves", "Waves", "reactive.scene", ParamSet.Empty.With("sound", 0.6));
        return Project.CreateDefault("Content show") with { Generators = [sparks, bulb, waves] };
    }

    /// <summary>The renderer's observations, as a render host's frame would hold them on its 100th frame.</summary>
    private static ContentFrame Observed(double bulbMs = 1.2)
    {
        var frame = new ContentFrame();
        frame.Stats.SetFrame(100);
        var sparks = frame.Stats.For("gn_sparks");
        sparks.Drawn(0.4, 65536, 3_000_000_000);
        sparks.Set(GeneratorState.Warming, "", 100);
        var bulb = frame.Stats.For("gn_bulb");
        for (var i = 0; i < 20; i++)
            bulb.Measured((long)(bulbMs * 1e6));
        bulb.Drawn(1, 0, 2_000_000_000);
        bulb.Set(GeneratorState.Ok, "", 99);
        var waves = frame.Stats.For("gn_waves");
        waves.Drawn(1, 0, 4_000_000_000);
        waves.Set(GeneratorState.Faulted, "Shader 'reactive.plasma' did not compile", 98);
        return frame;
    }

    private static async Task<ContentRig> Scenario()
    {
        var rig = new ContentRig(Show());
        rig.Clock.AdvanceMs(4_000);
        rig.Engine.Content.Attach(Observed());
        await rig.Ok("STING Explosion 900 AT +100");
        // The renderer drew a generator with it.
        rig.Engine.Content.Frame!.Stats.ImpulseDrawn(rig.Engine.Content.Stings.Current, 100);
        rig.Clock.AdvanceMs(550);
        rig.Engine.FactsChanged();
        return rig;
    }

    internal static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Content",
            ["path"] = "content",
            ["light"] = new JsonObject
            {
                ["path"] = "content.light",
                ["red"] = new JsonArray("red"),
                ["amber"] = new JsonArray("amber"),
                ["green"] = new JsonArray("green"),
            },
            ["words"] = "content.renderer",
            ["fields"] = new JsonArray(
                Field("Generators", "content.generators.length"),
                Field("Cost (ms, p95)", "content.costMs"),
                Field("Budget (ms)", "content.budgetMs"),
                Field("Governor", "content.governor"),
                Field("Sting", "content.sting.preset"),
                Field("Sting progress", "content.sting.progress")),
            ["actions"] = new JsonArray(Action("Explosion", "STING Explosion"), Action("Flash", "STING Flash"), Action("Stop the sting", "STING CLEAR"),
                Action("Stings", "STING LIST"), Action("Retime every generator", "GENERATOR RETIME ALL")),
        },
        new JsonObject
        {
            ["title"] = "Generator",
            ["path"] = "content.generators[]",
            ["light"] = new JsonObject { ["path"] = "light" },
            ["words"] = "words",
            ["fields"] = new JsonArray(
                Field("Name", "name"),
                Field("Kind", "kind"),
                Field("State", "state"),
                Field("Settled", "progress"),
                Field("Time class", "time"),
                Field("Own time (s)", "timeS"),
                Field("GPU (ms)", "gpuMs"),
                Field("GPU p95 (ms)", "gpuMsP95"),
                Field("Count", "count"),
                Field("On the air", "onAir"),
                Field("Warnings", "warnings")),
            ["actions"] = new JsonArray(Action("Retime", "GENERATOR RETIME $(id)"), Action("Reset", "GENERATOR RESET $(id)"),
                Action("Get", "GENERATOR GET $(id)")),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonObject Action(string label, string line) => new() { ["label"] = label, ["line"] = line };

    private static string FixtureOf(string stateJson)
    {
        var root = JsonNode.Parse(stateJson)!.AsObject();
        var fixture = new JsonObject { ["content"] = root["content"]!.DeepClone() };
        return fixture.ToJsonString(FixtureJson.Indented) + "\n";
    }

    [Fact]
    public async Task TodaysContentStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = await Scenario();
        var fixture = FixtureOf(rig.Engine.State().Json);
        using (var doc = JsonDocument.Parse(fixture))
        {
            var content = doc.RootElement.GetProperty("content");
            Assert.Equal("red", content.GetProperty("light").GetString());
            Assert.Equal(6.0, content.GetProperty("budgetMs").GetDouble());
            Assert.Equal("drawing", content.GetProperty("renderer").GetString());
            Assert.Equal(0.5, content.GetProperty("sting").GetProperty("progress").GetDouble(), 3);
            Assert.True(content.GetProperty("sting").GetProperty("drawn").GetBoolean());
            var g = content.GetProperty("generators").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
            Assert.Equal(("warming", 0.4, "bounded", 65536), (g["gn_sparks"].GetProperty("state").GetString(), g["gn_sparks"].GetProperty("progress").GetDouble(),
                g["gn_sparks"].GetProperty("time").GetString(), g["gn_sparks"].GetProperty("count").GetInt32()));
            Assert.Equal(4.0, g["gn_sparks"].GetProperty("horizonS").GetDouble());
            Assert.Equal(("ok", 1.2, true, "green"), (g["gn_bulb"].GetProperty("state").GetString(), g["gn_bulb"].GetProperty("gpuMsP95").GetDouble(),
                g["gn_bulb"].GetProperty("onAir").GetBoolean(), g["gn_bulb"].GetProperty("light").GetString()));
            Assert.Equal(("faulted", "live", "red"), (g["gn_waves"].GetProperty("state").GetString(), g["gn_waves"].GetProperty("time").GetString(),
                g["gn_waves"].GetProperty("light").GetString()));
        }
        var cards = Cards().ToJsonString(FixtureJson.Indented) + "\n";
        var statePath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "content.json");
        var cardsPath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "content.cards.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            PlayMap.Core.Storage.DurableFile.WriteAllText(statePath, fixture);
            PlayMap.Core.Storage.DurableFile.WriteAllText(cardsPath, cards);
        }
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(statePath)).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(cardsPath)).ReplaceLineEndings("\n"), cards);

        // Every card path reads a value in the fixture, and every action is an available line of the catalogue.
        var state = JsonNode.Parse(fixture)!;
        foreach (var card in Cards().Select(c => c!.AsObject()))
        {
            var path = card["path"]!.GetValue<string>();
            var bases = Resolve(state, path).ToList();
            Assert.NotEmpty(bases);
            Assert.All(bases, b => Assert.NotEmpty(Resolve(path == "content" ? state : b, card["light"]!["path"]!.GetValue<string>())));
            foreach (var field in card["fields"]!.AsArray())
                Assert.All(bases, b => Assert.NotEmpty(Resolve(path == "content" ? state : b, field!["path"]!.GetValue<string>())));
            foreach (var action in card["actions"]!.AsArray())
            {
                var line = action!["line"]!.GetValue<string>().Replace("$(id)", "gn_bulb", StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && rig.Engine.IsAvailable(m.Spec), line);
            }
        }
    }

    [Fact]
    public async Task TheFixtureIsTheSameOnASecondRun()
    {
        await using var a = await Scenario();
        await using var b = await Scenario();
        Assert.Equal(FixtureOf(a.Engine.State().Json), FixtureOf(b.Engine.State().Json));
    }

    /// <summary>The values at a dotted path ("content.generators[].state", "content.generators.length").</summary>
    private static IEnumerable<JsonNode?> Resolve(JsonNode? node, string path)
    {
        IEnumerable<JsonNode?> current = [node];
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var each = part.EndsWith("[]", StringComparison.Ordinal);
            var name = each ? part[..^2] : part;
            current = current.SelectMany(n => n switch
            {
                JsonArray a when name == "length" => [JsonValue.Create(a.Count)],
                JsonObject o when o[name] is JsonArray arr && each => arr.AsEnumerable(),
                JsonObject o when o.ContainsKey(name) => [o[name]],
                _ => Enumerable.Empty<JsonNode?>(),
            }).ToList();
        }
        return current;
    }

    [Fact]
    public async Task PreflightHoldsEveryGeneratorOnTheAirToItsBudget()
    {
        await using var rig = new ContentRig(Show());
        var check = new ContentPreflight();
        async Task<IReadOnlyList<PreflightRow>> Rows() => await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);

        // No renderer: grey, and nothing to fix.
        var none = Assert.Single(await Rows());
        Assert.Equal((Light.Grey, "content"), (none.Light, none.Section));

        // A renderer with nothing on the air: green.
        rig.Engine.Content.Attach(new ContentFrame());
        Assert.Equal(Light.Green, Assert.Single(await Rows()).Light);

        // Within budget: green; the fault injected — over the budget, then twice it — amber, then red, with the line that brings it down.
        rig.Engine.Content.Attach(Observed(bulbMs: 2));
        var rows = await Rows();
        Assert.Equal(Light.Green, rows.Single(r => r.Id == "content:gn_bulb").Light);
        rig.Engine.Content.Attach(Observed(bulbMs: 7));
        var over = (await Rows()).Single(r => r.Id == "content:gn_bulb");
        Assert.Equal((Light.Amber, "over budget"), (over.Light, over.Value));
        Assert.Equal("GENERATOR SET gn_bulb renderScale 0.33", over.Fix);
        rig.Engine.Content.Attach(Observed(bulbMs: 13));
        var red = (await Rows()).Single(r => r.Id == "content:gn_bulb");
        Assert.Equal(Light.Red, red.Light);
        Assert.Contains("13.0 ms of 6 ms", red.Note, StringComparison.Ordinal);

        // The field settling is amber; the faulted scene red with the line that puts its parameters back.
        rows = await Rows();
        Assert.Equal(Light.Amber, rows.Single(r => r.Id == "content:gn_sparks").Light);
        var faulted = rows.Single(r => r.Id == "content:gn_waves");
        Assert.Equal((Light.Red, "GENERATOR RESET gn_waves"), (faulted.Light, faulted.Fix));
        foreach (var fix in rows.Select(r => r.Fix).OfType<string>())
            Assert.True(WireGrammar.TryParse(fix, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && rig.Engine.IsAvailable(m.Spec), fix);

        // The fix works: the generator at half its particles, and a fresh measurement under budget is green.
        Assert.Equal("GENERATOR SET gn_sparks count 32768", ContentPreflight.Relief(rig.Engine.Store.Project.FindGenerator("gn_sparks")!));
        await rig.Ok(over.Fix!);
        Assert.Equal("0.33", rig.Engine.Store.Project.FindGenerator("gn_bulb")!.Params.Get("renderScale"));
    }

    [Fact]
    public async Task TheExesCompositionCarriesTheCheck()
    {
        Assert.Contains(ContentExtension.Factory, EngineParts.Standard().Extensions);
        await using var rig = new ContentRig();
        await using var part = ContentExtension.Factory(null!);
        Assert.Equal("content", part.Name);
        Assert.Single(part.Configure(rig.Engine.Services).PreflightChecks, c => c is ContentPreflight);
    }

    [Fact]
    public async Task TheIndexSectionsAndTheContextItemComeFromTheSameViews()
    {
        await using var rig = new ContentRig(Show());
        var content = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "content");
        var stings = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "sting");
        foreach (var key in content.Hint.Keys.Concat(stings.Hint.Keys))
        {
            var line = key.DownLine.Replace("$(id)", "gn_bulb", StringComparison.Ordinal).Replace("STING gn_bulb", "STING Explosion", StringComparison.Ordinal);
            Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && rig.Engine.IsAvailable(m.Spec), line);
            Assert.Equal(VerbParams.Press(VerbCatalog.Match(parsed)!.Value.Spec), key.Press);
        }
        JsonElement Rows(IIndexContributor section)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
                section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
            using var doc = JsonDocument.Parse(ms.ToArray());
            return doc.RootElement.Clone();
        }
        string? ContextLight()
        {
            using var ctx = JsonDocument.Parse(rig.Engine.Context());
            return ctx.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault(i => i.GetProperty("id").GetString() == "content") is { ValueKind: JsonValueKind.Object } item
                ? item.GetProperty("light").GetString() : null;
        }
        var rows = Rows(content);
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal(["id", "kind", "label", "parent", "light", "state"], rows[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("Sparks · Particle flow", rows[0].GetProperty("label").GetString());
        Assert.Equal("grey", rows[0].GetProperty("light").GetString());
        Assert.Equal(Enum.GetValues<Core.Content.PulsePreset>().Length, Rows(stings).GetArrayLength());
        // Nothing on the air: no context item.
        Assert.Null(ContextLight());

        rig.Engine.Content.Attach(Observed());
        rows = Rows(content);
        Assert.Equal(("amber", "warming"), (rows[0].GetProperty("light").GetString(), rows[0].GetProperty("state").GetString()));
        Assert.Equal("red", rows[2].GetProperty("light").GetString());
        Assert.Equal("red", ContextLight());
        await rig.Ok("STING Flash");
        Assert.Equal("ready", Rows(stings).EnumerateArray().Single(r => r.GetProperty("id").GetString() == "Flash").GetProperty("state").GetString());
        using (var fired = JsonDocument.Parse(rig.Engine.Context()))
            Assert.Contains("sting Flash fired, not drawn yet", fired.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("id").GetString() == "content").GetProperty("state").GetString(), StringComparison.Ordinal);
        // A frame drew a generator with it: now it surges.
        rig.Engine.Content.Frame!.Stats.ImpulseDrawn(rig.Engine.Content.Stings.Current, 100);
        Assert.Equal("surging", Rows(stings).EnumerateArray().Single(r => r.GetProperty("id").GetString() == "Flash").GetProperty("state").GetString());
        using var ctx = JsonDocument.Parse(rig.Engine.Context());
        var state = ctx.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "content").GetProperty("state").GetString()!;
        Assert.Contains("3 generators", state, StringComparison.Ordinal);
        Assert.Contains("Sparks settling 40 %", state, StringComparison.Ordinal);
        Assert.Contains("Waves faulted", state, StringComparison.Ordinal);
        Assert.Contains("sting Flash surging", state, StringComparison.Ordinal);
    }
}
