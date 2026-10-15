using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Sync;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Presentation;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Render.Platform;
using PlayMap.Render.Sync;
using PlayMap.Render.Timing;

namespace PlayMap.Engine.Tests.Presentation;

/// <summary>The package's parts beside its verbs: the preflight check (a fault injected, its fix line), the INDEX section, the
/// context's lock item, and the exe's composition carrying the check.</summary>
public sealed class OutputPartsTests
{
    private static PresentClock Presented(int frames, int lateEvery = 0)
    {
        var clock = new PresentClock(60, measured: false, PresentPath.Offscreen);
        var t = 50_000_000_000L;
        for (var f = 0; f < frames; f++)
        {
            t += lateEvery > 0 && f % lateEvery == 0 ? 33_333_333 : 16_666_667;
            clock.OnPresented(PresentResult.Done(t));
        }
        return clock;
    }

    [Fact]
    public async Task APreflightRowPerOutputAndAFaultInjectedShowsRedWithTheLineThatFixesIt()
    {
        var outputs = new ScriptedOutputs { NowNs = 50_000_000_000L + 600 * 16_666_667L }.WithFreeLocks("op_a", "op_b");
        outputs.Clocks["op_a"] = Presented(600);
        outputs.Clocks["op_b"] = Presented(600, lateEvery: 20);
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a"), PresentRig.Screen("op_b")), outputs);
        Assert.True((await rig.Run("SYNC APPLY")).Ok);

        // The fault: op_a fell out of the lock the group applied; op_b is held out of it.
        outputs.LockViews["op_a"] = outputs.LockViews["op_a"] with { State = LockState.FellOut, Reason = LockReasons.Stall, Words = "the barrier stalled 3 frames", FellOut = 1 };
        outputs.LockViews["op_b"] = outputs.LockViews["op_b"] with { State = LockState.FreeMeasured };
        outputs.Held.Add("op_b");

        var check = new OutputPresentCheck();
        Assert.Equal("outputs", check.Section);
        var rows = await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        var fell = Assert.Single(rows, r => r.Id == "outputs.lock.op_a");
        Assert.Equal(Light.Red, fell.Light);
        Assert.Equal("SYNC REJOIN op_a", fell.Fix);
        Assert.Equal("0badc0de", fell.Node);
        var held = Assert.Single(rows, r => r.Id == "outputs.lock.op_b");
        Assert.Equal(Light.Amber, held.Light);
        Assert.Equal("SYNC REJOIN op_b", held.Fix);
        Assert.Equal(Light.Green, Assert.Single(rows, r => r.Id == "outputs.present.op_a").Light);
        var late = Assert.Single(rows, r => r.Id == "outputs.present.op_b");
        Assert.Equal(Light.Amber, late.Light);
        Assert.Contains("0.5 %", late.Note, StringComparison.Ordinal);

        // The fix lines are lines this node routes.
        foreach (var fix in rows.Select(r => r.Fix).OfType<string>())
            Assert.True(VerbCatalog.Match(Parse(fix)) is { } m && rig.Engine.IsAvailable(m.Spec), fix);

        // Fixed: the row turns green.
        Assert.True((await rig.Run("SYNC REJOIN op_a")).Ok);
        rows = await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        Assert.Equal(Light.Green, Assert.Single(rows, r => r.Id == "outputs.lock.op_a").Light);

        // Free by a refused join, not held out: nothing fell out, so the fix is a new SYNC APPLY (SYNC REJOIN would be refused).
        outputs.Held.Remove("op_b");
        outputs.LockViews["op_b"] = outputs.LockViews["op_b"] with
        {
            State = LockState.FreeMeasured,
            Reason = LockReasons.Armed,
            Words = "armed: joining the lock may step the picture — black out first, or SHOW DISARM",
        };
        rows = await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        var refused = Assert.Single(rows, r => r.Id == "outputs.lock.op_b");
        Assert.Equal(Light.Amber, refused.Light);
        Assert.Equal("SYNC APPLY", refused.Fix);
        Assert.StartsWith("armed:", refused.Note, StringComparison.Ordinal);
        Assert.False((await rig.Run("SYNC REJOIN op_b")).Ok);
        var applied = await rig.Run(refused.Fix!);
        Assert.True(applied.Ok, applied.Payload);
        rows = await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        Assert.Equal(Light.Green, Assert.Single(rows, r => r.Id == "outputs.lock.op_b").Light);
    }

    private static WireLine Parse(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return parsed;
    }

    [Fact]
    public async Task TheExesCompositionCarriesTheCheck()
    {
        Assert.Contains(OutputParts.Factory, EngineParts.Standard().Extensions);
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")));
        await using var part = OutputParts.Factory(null!);
        Assert.Equal("output", part.Name);
        var configured = part.Configure(rig.Engine.Services);
        Assert.Single(configured.PreflightChecks, c => c is OutputPresentCheck);
    }

    [Fact]
    public async Task TheIndexSectionListsEachOutputWithItsPresentAndLockLights()
    {
        var outputs = new ScriptedOutputs { NowNs = 50_000_000_000L + 600 * 16_666_667L }.WithFreeLocks("op_a", "op_b");
        outputs.Clocks["op_a"] = Presented(600);
        outputs.Clocks["op_b"] = Presented(600);
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a"), PresentRig.Screen("op_b")), outputs);
        outputs.LockViews["op_b"] = outputs.LockViews["op_b"] with { State = LockState.FellOut };
        var section = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "present");
        Assert.Equal("output", section.Hint.RowKind);
        Assert.Contains(section.Hint.Keys, k => k.DownLine == "SYNC REJOIN $(id)" && k.Feedback == "outputs.$(id).lock.state");
        Assert.Contains(section.Hint.Keys, k => k.DownLine == "OUTPUT STATS $(id)");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
        using var doc = JsonDocument.Parse(ms.ToArray());
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(["op_a", "op_b"], rows.Select(r => r.GetProperty("id").GetString()));
        Assert.All(rows, r => Assert.Equal(["id", "kind", "label", "parent", "light", "state"], r.EnumerateObject().Select(p => p.Name)));
        Assert.Equal("green", rows[0].GetProperty("light").GetString());
        Assert.Equal("red", rows[1].GetProperty("light").GetString());
        Assert.Equal("fell-out", rows[1].GetProperty("state").GetString());
    }

    [Fact]
    public async Task TheContextCarriesTheFrameLockWhileItIsApplied()
    {
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), new ScriptedOutputs().WithFreeLocks("op_a"));
        using (var before = JsonDocument.Parse(rig.Engine.Context()))
            Assert.DoesNotContain(before.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "sync:lock");
        Assert.True((await rig.Run("SYNC APPLY")).Ok);
        using var doc = JsonDocument.Parse(rig.Engine.Context());
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "sync:lock");
        Assert.Equal("sync", item.GetProperty("kind").GetString());
        Assert.Equal("green", item.GetProperty("light").GetString());
        var link = Assert.Single(doc.RootElement.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("from").GetString() == "output:op_a" && l.GetProperty("to").GetString() == "sync:lock");
        // The Eye drops any other link kind: a locked output follows the lock.
        Assert.Equal(ContextLinkKinds.Follows, link.GetProperty("kind").GetString());

        // Fallen out, it only hears the lock (and is red).
        rig.Outputs.LockViews["op_a"] = rig.Outputs.LockViews["op_a"] with { State = LockState.FellOut, Reason = LockReasons.Stall };
        using var fell = JsonDocument.Parse(rig.Engine.Context());
        var free = Assert.Single(fell.RootElement.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("from").GetString() == "output:op_a" && l.GetProperty("to").GetString() == "sync:lock");
        Assert.Equal(ContextLinkKinds.Hears, free.GetProperty("kind").GetString());
        Assert.Equal("red", free.GetProperty("light").GetString());
        Assert.All(fell.RootElement.GetProperty("links").EnumerateArray(), l => Assert.True(ContextLinkKinds.IsEyeVerb(l.GetProperty("kind").GetString()!)));
    }
}

/// <summary>tests/fixtures/state/output.json and output.cards.json: STATE from a real scenario — two outputs on the headless render host
/// with a scripted sync board, the lock applied and observed — with the numbers that move from run to run set to fixed values; and the
/// desk's card descriptors for every block this package adds. Written with PLAYMAP_WRITE_FIXTURES=1, otherwise checked.</summary>
[Collection("render host")]
public sealed class OutputFixtureTests
{
    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    /// <summary>The run-to-run numbers of presentation set to a steady 60 Hz output that missed nothing.</summary>
    internal static string Normalise(string stateJson)
    {
        var root = JsonNode.Parse(MainStateFixtureTests.Normalise(stateJson))!.AsObject();
        root["machine"]!["renderer"] = "OpenGL 4.5 llvmpipe";
        if (root["machine"]!["present"] is JsonObject machine)
        {
            machine["adapter"] = "OpenGL 4.5 llvmpipe";
            machine["presentMsP95"] = 0.21;
            // The platform's binding (GLX here, WGL on Windows), as the renderer's name is the machine's.
            machine["context"] = "glx";
        }
        if (root["machine"]!["gl"] is not null)
            root["machine"]!["gl"] = "glx";
        foreach (var o in root["outputs"]!.AsArray().OfType<JsonObject>())
        {
            o["fps"] = 60.0;
            o["dropped"] = 0;
            o["frameMsP95"] = 16.67;
            if (o["present"] is JsonObject p)
            {
                p["refreshHz"] = 60;
                p["ppm"] = 0;
                p["missedVblanks"] = 0;
                p["missed60s"] = 0;
                p["held"] = 0;
                p["statsAgeMs"] = 4.2;
                p["presented"] = 600;
                p["late10s"] = 0;
                p["light"] = "green";
                p["words"] = "60 Hz target · offscreen · 0 missed in 60 s";
            }
        }
        return root.ToJsonString(FixtureJson.Indented) + "\n";
    }

    /// <summary>The desk's cards for this package's STATE blocks: title, STATE path, light, words, up to six fields, actions as lines.</summary>
    internal static string Cards()
    {
        var cards = new JsonArray
        {
            Card("Presentation", "outputs[].present", "outputs[].present.light", "outputs[].present.words",
                [("Refresh", "outputs[].present.refreshHz", "Hz"), ("Against", "outputs[].present.nominalHz", "Hz"), ("Missed (60 s)", "outputs[].present.missed60s", ""),
                 ("Late (10 s)", "outputs[].present.late10s", ""), ("Path", "outputs[].present.path", ""), ("Latency", "outputs[].latencyMs", "ms")],
                [("Stats", "OUTPUT STATS $(id)"), ("Reset counters", "PRESENT RESET $(id)")]),
            Card("Frame lock", "outputs[].lock", "outputs[].lock.state", "outputs[].lock.words",
                [("State", "outputs[].lock.state", ""), ("Reason", "outputs[].lock.reason", ""), ("Fell out", "outputs[].lock.fellOut", ""),
                 ("Rejoins", "outputs[].lock.rejoins", "")],
                [("Rejoin", "SYNC REJOIN $(id)"), ("Leave", "SYNC LEAVE $(id)")]),
            Card("Group lock", "sync.lock", "sync.lock.state", "sync.lock.words",
                [("Board", "sync.lock.board", ""), ("Driver", "sync.lock.driver", ""), ("Locked", "sync.lock.locked", ""), ("Show time", "sync.lock.time", ""),
                 ("Latency", "sync.latencyMs", "ms"), ("Refused", "sync.lock.refused.words", "")],
                [("Apply", "SYNC APPLY"), ("Leave", "SYNC LEAVE"), ("Rejoin", "SYNC REJOIN")]),
            Card("Present path", "machine.present", null, "machine.present.why",
                [("API", "machine.present.api", ""), ("Adapter", "machine.present.adapter", ""), ("Path", "machine.present.path", ""),
                 ("Dialect", "machine.present.dialect", ""), ("Context", "machine.present.context", ""), ("Present p95", "machine.present.presentMsP95", "ms")],
                [("Stats", "OUTPUT STATS ALL")]),
        };
        return cards.ToJsonString(FixtureJson.Indented) + "\n";
    }

    private static JsonObject Card(string title, string path, string? light, string words, (string Label, string Path, string Unit)[] fields,
        (string Label, string Line)[] actions)
    {
        var f = new JsonArray();
        foreach (var (label, p, unit) in fields)
            f.Add(new JsonObject { ["label"] = label, ["path"] = p, ["unit"] = unit });
        var a = new JsonArray();
        foreach (var (label, line) in actions)
            a.Add(new JsonObject { ["label"] = label, ["line"] = line });
        return new JsonObject { ["title"] = title, ["path"] = path, ["light"] = light, ["words"] = words, ["fields"] = f, ["actions"] = a };
    }

    /// <summary>Resolves a card path ("outputs[].present.light") in a STATE document (the first row of an array).</summary>
    private static JsonNode? At(JsonNode root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            var name = part.EndsWith("[]", StringComparison.Ordinal) ? part[..^2] : part;
            node = node?[name];
            if (part.EndsWith("[]", StringComparison.Ordinal))
                node = node?.AsArray().FirstOrDefault();
        }
        return node;
    }

    [Fact]
    public async Task TheOutputFixtureAndItsCardsAreWhatTheEngineWrites()
    {
        var driver = new FakeFrameLockDriver();
        var project = HostRig.WithOutputs(
            HostRig.Screen("op_left", SourceRef.Solid("#336699"), 64, 36) with { Name = "Left", LatencyMs = 33 },
            HostRig.Screen("op_right", SourceRef.Solid("#996633"), 64, 36) with { Name = "Right", LatencyMs = 33 });
        await using var rig = HostRig.Start(project, options: RenderHostOptions.Default with { Windows = false, FrameLock = () => driver });
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (rig.Host.Board is null || rig.Host.Locks().Count < 2)
        {
            Assert.True(waited.Elapsed < HostRig.Patience, "the board was never seen");
            await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        }
        var applied = await rig.Run("SYNC APPLY");
        Assert.True(applied.Ok, applied.Payload);
        await rig.Host.FramesAsync(5).WaitAsync(HostRig.Patience);
        rig.Engine.FactsChanged();

        var state = Normalise(rig.Engine.State().Json);
        var cards = Cards();
        var root = JsonNode.Parse(state)!;
        Assert.Equal("framelocked", root["sync"]!["lock"]!["state"]!.GetValue<string>());
        Assert.Equal("offscreen", root["outputs"]![0]!["present"]!["path"]!.GetValue<string>());
        foreach (var card in JsonNode.Parse(cards)!.AsArray())
        {
            Assert.NotNull(At(root, card!["path"]!.GetValue<string>()));
            Assert.NotNull(At(root, card["words"]!.GetValue<string>()));
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(field!["path"]!.GetValue<string>().EndsWith(".words", StringComparison.Ordinal)
                    || At(root, field["path"]!.GetValue<string>()) is not null || field["path"]!.GetValue<string>().StartsWith("sync.lock.refused", StringComparison.Ordinal),
                    field["path"]!.GetValue<string>());
            foreach (var action in card["actions"]!.AsArray())
            {
                var line = action!["line"]!.GetValue<string>().Replace("$(id)", "op_left", StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && rig.Engine.IsAvailable(m.Spec), line);
            }
        }

        var statePath = FixturePath("output.json");
        var cardsPath = FixturePath("output.cards.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(statePath, state);
            await File.WriteAllTextAsync(cardsPath, cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(statePath)).ReplaceLineEndings("\n"), state);
        Assert.Equal((await File.ReadAllTextAsync(cardsPath)).ReplaceLineEndings("\n"), cards);
    }
}
