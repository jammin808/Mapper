using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Core.Wire;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>tests/fixtures/state/phonescan.json is the phone package's STATE keys from a real scenario — two phones invited, one
/// scanning a projector through the lock-step capture (with a rolling-shutter mix and a false picture caught on the way), the other
/// gone — and phonescan.cards.json the cards the desk draws from them. This test writes both (PLAYMAP_WRITE_FIXTURES=1) and otherwise
/// checks the committed files are what the engine writes now, with ids and clock numbers made stable; every card's path and fields
/// resolve in the fixture and every action is a line of the catalogue.</summary>
public sealed partial class PhoneStateFixtureTest
{
    private static readonly JsonSerializerOptions Indented = FixtureJson.Indented;

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Phone",
            ["path"] = "phones.0",
            ["light"] = new JsonArray(Rule("if", "connected", "green"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{words}",
            ["fields"] = new JsonArray(Field("Name", "name"), Field("Camera", "rig"), Field("Platform", "platform"), Field("Model", "model"),
                Field("Connected", "connected"), Field("Address", "address"), Field("Motion", "motion"), Field("Last seen", "lastSeenUnixMs")),
            ["actions"] = new JsonArray("PHONE INVITE", "PHONE LIST", "PHONE FORGET $(id)"),
        },
        new JsonObject
        {
            ["title"] = "Phone camera",
            ["path"] = "phones.0.camera",
            ["light"] = new JsonArray(Rule("if", "exposure", "green"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{label} · exposure {exposure} · focus {focus}",
            ["fields"] = new JsonArray(Field("Lens", "label"), Field("Width", "width"), Field("Height", "height"), Field("Exposure", "exposure"),
                Field("Exposure ms", "exposureMs"), Field("ISO", "iso"), Field("Focus", "focus"), Field("White balance", "whiteBalance")),
            ["actions"] = new JsonArray("PHONE LIST"),
        },
        new JsonObject
        {
            ["title"] = "Scan view",
            ["path"] = "scan.views.0",
            ["light"] = new JsonArray(Rule("if", "words", "amber"), new JsonObject { ["light"] = "green" }),
            ["words"] = "{id} · {state} · {rmsPx} px over {tracks} tracks",
            ["fields"] = new JsonArray(Field("View", "id"), Field("Camera", "camera"), Field("State", "state"), Field("RMS px", "rmsPx"),
                Field("Tracks", "tracks"), Field("Paired by", "pairing"), Field("Exposure", "exposure")),
            ["actions"] = new JsonArray("SCAN VIEWS $(scan)", "SCAN DROP $(scan) $(id)", "SCAN SOLVE $(scan)"),
        },
        new JsonObject
        {
            ["title"] = "Scan coverage",
            ["path"] = "scan.coverage.0",
            ["light"] = new JsonArray(new JsonObject { ["light"] = "green" }),
            ["words"] = "{projector}: seen {seen}, twice {twice}",
            ["fields"] = new JsonArray(Field("Projector", "projector"), Field("Seen", "seen"), Field("Seen twice", "twice")),
            ["actions"] = new JsonArray("SCAN VIEWS $(scan)"),
        },
        new JsonObject
        {
            ["title"] = "Scan region",
            ["path"] = "scan.regions.0",
            ["light"] = new JsonArray(new JsonObject { ["light"] = "green" }),
            ["words"] = "{label} · lit by {projectors}",
            ["fields"] = new JsonArray(Field("Region", "id"), Field("Name", "label"), Field("Projector", "projector"), Field("Projectors", "projectors"),
                Field("Confidence", "confidence")),
            ["actions"] = new JsonArray("SCAN SURFACE $(scan) REGION $(id)", "SCAN SUGGEST $(scan)"),
        },
        new JsonObject
        {
            ["title"] = "Surface suggestion",
            ["path"] = "scan.suggestions.0",
            ["light"] = new JsonArray(Rule("if", "measured", "amber"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{label} · {state}",
            ["fields"] = new JsonArray(Field("Name", "label"), Field("Kind", "kind"), Field("Confidence", "confidence"), Field("Reasons", "reasons"),
                Field("Measured", "measured"), Field("Proposals", "proposals"), Field("State", "state")),
            ["actions"] = new JsonArray("APPROVE $(proposal)", "REJECT $(proposal)", "SCAN SUGGEST $(scan) ASSISTANT"),
        },
        new JsonObject
        {
            ["title"] = "Scan pairing",
            ["path"] = "scan.pairing",
            ["light"] = new JsonArray(Rule("if", "timingDisagrees", "amber"), new JsonObject { ["light"] = "green" }),
            ["words"] = "{words} · {retakes} shown again · {caught} caught",
            ["fields"] = new JsonArray(Field("Paired by", "mode"), Field("Tag places", "sites"), Field("Rolling-shutter mixes", "rollingRejects"),
                Field("Shown again", "retakes"), Field("Caught", "caught"), Field("Timing disagrees", "timingDisagrees"),
                Field("Drift %", "aeDriftPct"), Field("Exposure", "exposure")),
            ["actions"] = new JsonArray("SCAN NEXT", "SCAN CANCEL", "SCAN STATUS"),
        },
    ];

    private static JsonObject Rule(string kind, string path, string light) => new() { [kind] = path, ["light"] = light };

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    [GeneratedRegex(@"\b(ph|cm|sc)[0-9a-f]{8}\b")]
    private static partial Regex RandomIds();

    /// <summary>The phone keys of STATE (phones, scan.pairing), ids random per run numbered in order of appearance and the clock's
    /// and the link's measured numbers fixed.</summary>
    private static string Fixture(JsonElement state, JsonElement multi)
    {
        var phones = JsonNode.Parse(state.GetProperty("phones").GetRawText())!.AsArray();
        foreach (var p in phones)
        {
            p!["lastSeenUnixMs"] = 1_760_000_000_000;
            // Measured on this run's link: present when connected, stable in the fixture.
            foreach (var key in new[] { "rttMs", "offsetMs", "uncertaintyMs", "uploadMBps" })
                p["link"]![key] = 0;
        }
        var scan = new JsonObject { ["pairing"] = JsonNode.Parse(state.GetProperty("scan").GetProperty("pairing").GetRawText()) };
        // The multi-view keys come from their own scenario: measured numbers and the reasons' words (they carry numbers) made stable.
        foreach (var key in new[] { "view", "views", "coverage", "regions", "suggestions" })
            scan[key] = Stable(JsonNode.Parse(multi.GetProperty(key).GetRawText()));
        var root = new JsonObject
        {
            ["phones"] = phones,
            ["scan"] = scan,
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

    /// <summary>Measured numbers to 0 and a list of words to one word, all through: what is kept is the shape.</summary>
    private static JsonNode? Stable(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    o[key] = key == "reasons" ? new JsonArray("reason")
                        : value is JsonValue v && v.TryGetValue<double>(out _) ? 0
                        : Stable(value?.DeepClone());
                }
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    a[i] = a[i] is JsonValue v && v.TryGetValue<double>(out _) ? 0 : Stable(a[i]?.DeepClone());
                return a;
            default:
                return node;
        }
    }

    /// <summary>STATE.scan of a multi-view scan with suggestions: two views INTO one scan of the simulator's multi-view scene, then
    /// SCAN SUGGEST.</summary>
    private static async Task<JsonElement> MultiViewScanStateAsync()
    {
        var rig = new OpsRig(project: MultiViewScanKit.Project());
        try
        {
            var line = $"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2}";
            var id = await MultiViewScanKit.ScanAsync(rig, line);
            await MultiViewScanKit.ScanAsync(rig, line + " INTO " + id);
            await rig.Ok("SCAN SUGGEST " + id);
            return rig.State().GetProperty("scan");
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
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
    public async Task ThePhonesStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        // The scan's patience runs on a clock this test owns and never moves: a phone slow on a loaded machine is waited for, never
        // shown its pattern again, so the retakes are the scenario's own (the false picture caught), one value on every run.
        await using var rig = await PhoneScanRig.StartAsync(new ScanRunnerOptions { PatternTimeout = TimeSpan.FromSeconds(2), Time = new Fleet.ManualTime() });
        await using var phone = await rig.JoinAsync();
        phone.Half.Add(6);
        phone.Lie.Add(9);
        await using (var gone = await rig.JoinAsync(locks: false, name: "iPhone"))
        {
            Assert.True(await OpsRig.Eventually(() => rig.Hub.Phones().Count(p => p.Connected) == 2));
        }
        Assert.True(await OpsRig.Eventually(() => rig.Hub.Phones().Count(p => p.Connected) == 1));
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE FAST")).Ok);
        var view = await rig.EndedAsync();
        Assert.True(view.State == "done", view.Message);

        var state = rig.State();
        Assert.Equal(2, state.GetProperty("phones").GetArrayLength());
        var pairing = state.GetProperty("scan").GetProperty("pairing");
        Assert.Equal(1, pairing.GetProperty("caught").GetInt32());
        Assert.True(pairing.GetProperty("rollingRejects").GetInt32() >= 1);

        var fixture = Fixture(state, await MultiViewScanStateAsync());
        var cards = Cards().ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(FixturePath("phonescan.json"), fixture);
            await File.WriteAllTextAsync(FixturePath("phonescan.cards.json"), cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("phonescan.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("phonescan.cards.json"))).ReplaceLineEndings("\n"), cards);

        var root = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(root, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(at.AsObject().ContainsKey(field!["path"]!.GetValue<string>()), $"{card["title"]}: {field["path"]}");
            foreach (var action in card["actions"]!.AsArray())
            {
                var text = action!.GetValue<string>().Replace("$(id)", "ph00000001", StringComparison.Ordinal)
                    .Replace("$(scan)", "sc00000001", StringComparison.Ordinal).Replace("$(proposal)", "ap1", StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(text, out var line, out _), text);
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }
}
