using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Audio;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Audio;

/// <summary>STATE.audio and STATE.modulation from a real scenario are the committed fixture, with the card descriptor that reads
/// them; the preflight check turns red with its fix line when bindings listen and nothing is heard; the tempo INDEX section; the
/// context's audio and tempo items.</summary>
public sealed class AudioFixtureTests
{
    /// <summary>The fixture's show: the drum loop heard at 128 BPM in 3/4, a layer's opacity on the bass, its x on the beat, a
    /// generator's speed on a beat-long LFO, and the layer held at its base.</summary>
    private static async Task<AudioRig> Scenario()
    {
        var rig = new AudioRig(AudioRig.Show());
        await rig.Ok("TEMPO SET 128");
        await rig.Ok("TEMPO METER 3 4");
        rig.AdvanceMs(100);
        await rig.Ok("AUDIO LISTEN TEST DRUMS CHANNELS 1+2");
        await rig.Ok("MOD BIND ly_base opacity audio:bass AMOUNT 1 SMOOTH 10/250");
        await rig.Ok("MOD BIND ly_base rect.x beat AMOUNT 0.1");
        await rig.Ok("MOD BIND gn_julia speed lfo:sine/1b AMOUNT 0.25");
        await rig.Ok("MOD BYPASS ON gn_julia");
        for (var i = 0; i < 300; i++)
        {
            rig.AdvanceMs(10);
            rig.Engine.AudioRt.Synthetic.Pump();
        }
        await rig.Engine.Lane.RunAsync(rig.Engine.RefreshAudioFacts);
        return rig;
    }

    /// <summary>The cards the desk draws from STATE.audio and STATE.modulation: what each reads and the lines its actions send.</summary>
    internal static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Audio",
            ["path"] = "audio",
            ["light"] = new JsonObject
            {
                ["path"] = "audio.input.state",
                ["red"] = new JsonArray("retrying"),
                ["amber"] = new JsonArray("opening"),
                ["green"] = new JsonArray("listening"),
            },
            ["words"] = "audio.input.words",
            ["fields"] = new JsonArray(
                Field("Input", "audio.input.name"),
                Field("Channels", "audio.input.channels"),
                Field("Rate (Hz)", "audio.input.rateHz"),
                Field("Level", "audio.level"),
                Field("Bass", "audio.bass"),
                Field("Mid", "audio.mid"),
                Field("High", "audio.high"),
                Field("Peak (dBFS)", "audio.peakDb"),
                Field("Silent", "audio.silent"),
                Field("Behind the sound (ms)", "audio.latencyMs.total")),
            ["actions"] = new JsonArray(Action("Inputs", "AUDIO INPUTS"), Action("Listen to the default input", "AUDIO LISTEN DEFAULT"),
                Action("Test click", "AUDIO LISTEN TEST CLICK"), Action("Stop listening", "AUDIO LISTEN OFF"), Action("Auto gain", "AUDIO GAIN AUTO")),
        },
        new JsonObject
        {
            ["title"] = "Tempo",
            ["path"] = "audio.tempo",
            ["light"] = new JsonObject
            {
                ["path"] = "audio.tempo.lock",
                ["red"] = new JsonArray("lost"),
                ["amber"] = new JsonArray("searching", "holding"),
                ["green"] = new JsonArray("manual", "locked"),
            },
            ["words"] = "audio.tempo.source",
            ["fields"] = new JsonArray(
                Field("BPM", "audio.tempo.bpm"),
                Field("Beat", "audio.beat"),
                Field("Bar", "audio.bar"),
                Field("Beats a bar", "audio.tempo.beatsPerBar"),
                Field("Bars a phrase", "audio.tempo.barsPerPhrase"),
                Field("Source", "audio.tempo.source")),
            ["actions"] = new JsonArray(Action("Tap", "TEMPO TAP"), Action("Resync the bar", "TEMPO RESYNC BAR"), Action("Half", "TEMPO HALF"),
                Action("Double", "TEMPO DOUBLE"), Action("BPM +1", "TEMPO SET +1"), Action("BPM −1", "TEMPO SET -1")),
        },
        new JsonObject
        {
            ["title"] = "Modulation",
            ["path"] = "modulation",
            ["light"] = new JsonObject { ["path"] = "modulation.bypassed", ["amber"] = new JsonArray(true), ["green"] = new JsonArray(false) },
            ["words"] = "modulation.unbound",
            ["fields"] = new JsonArray(
                Field("Bindings", "modulation.bindings"),
                Field("Live (this node's sound)", "modulation.live"),
                Field("Pure (the same everywhere)", "modulation.pure"),
                Field("Held", "modulation.held"),
                Field("Not applied", "modulation.unbound")),
            ["actions"] = new JsonArray(Action("Hold every number", "MOD BYPASS ON"), Action("Modulate again", "MOD BYPASS OFF"),
                Action("Sources", "MOD SOURCES")),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonObject Action(string label, string line) => new() { ["label"] = label, ["line"] = line };

    private static string FixtureOf(string stateJson)
    {
        var root = JsonNode.Parse(stateJson)!.AsObject();
        var fixture = new JsonObject { ["audio"] = root["audio"]!.DeepClone(), ["modulation"] = root["modulation"]!.DeepClone() };
        return fixture.ToJsonString(FixtureJson.Indented) + "\n";
    }

    [Fact]
    public async Task TodaysAudioStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = await Scenario();
        var fixture = FixtureOf(rig.Engine.State().Json);
        using (var doc = JsonDocument.Parse(fixture))
        {
            var audio = doc.RootElement.GetProperty("audio");
            Assert.Equal("listening", audio.GetProperty("input").GetProperty("state").GetString());
            Assert.Equal("test:drums", audio.GetProperty("input").GetProperty("key").GetString());
            Assert.Equal(128, audio.GetProperty("tempo").GetProperty("bpm").GetDouble());
            Assert.Equal(3, audio.GetProperty("tempo").GetProperty("beatsPerBar").GetInt32());
            Assert.True(audio.GetProperty("tempo").GetProperty("inUse").GetBoolean());
            Assert.InRange(audio.GetProperty("beat").GetInt32(), 1, 3);
            Assert.False(audio.GetProperty("silent").GetBoolean());
            var modulation = doc.RootElement.GetProperty("modulation");
            Assert.Equal(3, modulation.GetProperty("bindings").GetInt32());
            Assert.Equal(1, modulation.GetProperty("live").GetInt32());
            Assert.Equal(2, modulation.GetProperty("pure").GetInt32());
            Assert.Equal("gn_julia", modulation.GetProperty("held")[0].GetString());
        }
        var cards = Cards().ToJsonString(FixtureJson.Indented) + "\n";
        var statePath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "audio.json");
        var cardsPath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "audio.cards.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            PlayMap.Core.Storage.DurableFile.WriteAllText(statePath, fixture);
            PlayMap.Core.Storage.DurableFile.WriteAllText(cardsPath, cards);
        }
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(statePath)).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(cardsPath)).ReplaceLineEndings("\n"), cards);

        // Every card path reads a value in the fixture, and every action is a line of the catalogue.
        var state = JsonNode.Parse(fixture)!;
        foreach (var card in Cards().Select(c => c!.AsObject()))
        {
            Assert.NotEmpty(Resolve(state, card["path"]!.GetValue<string>()));
            Assert.NotEmpty(Resolve(state, card["light"]!["path"]!.GetValue<string>()));
            foreach (var field in card["fields"]!.AsArray())
                Assert.NotEmpty(Resolve(state, field!["path"]!.GetValue<string>()));
            foreach (var action in card["actions"]!.AsArray())
            {
                var line = action!["line"]!.GetValue<string>();
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

    private static IEnumerable<JsonNode?> Resolve(JsonNode? node, string path)
    {
        IEnumerable<JsonNode?> current = [node];
        foreach (var name in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            current = current.SelectMany(n => n is JsonObject o && o.ContainsKey(name) ? [o[name]] : Enumerable.Empty<JsonNode?>()).ToList();
        return current;
    }

    [Fact]
    public async Task PreflightIsRedWithAFixWhenBindingsListenAndNothingIsHeard()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        var check = new AudioPreflight();
        async Task<PreflightRow> Row() => Assert.Single(await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None));

        // Nothing reads the sound: green, not needed.
        var calm = await Row();
        Assert.Equal(Light.Green, calm.Light);
        Assert.Null(calm.Fix);
        await rig.Ok("MOD BIND ly_base rect.x beat");
        Assert.Equal(Light.Green, (await Row()).Light);

        // The fault: a binding listens and the show listens to nothing.
        await rig.Ok("MOD BIND ly_base opacity audio:bass");
        var off = await Row();
        Assert.Equal(Light.Red, off.Light);
        Assert.Equal("AUDIO LISTEN DEFAULT", off.Fix);
        Assert.Equal("audio", off.Section);
        Assert.True(WireGrammar.TryParse(off.Fix!, out var fix, out _) && VerbCatalog.Match(fix) is not null);

        // An input that cannot open: red with its words.
        await rig.Ok("AUDIO LISTEN \"Stage Mic\"");
        var missing = await Row();
        Assert.Equal(Light.Red, missing.Light);
        Assert.Contains("Stage Mic", missing.Note, StringComparison.Ordinal);

        // Heard: green.
        await rig.Ok("AUDIO LISTEN TEST DRUMS");
        rig.AdvanceMs(200);
        rig.Engine.AudioRt.Synthetic.Pump();
        await rig.Engine.Lane.RunAsync(rig.Engine.RefreshAudioFacts);
        var heard = await Row();
        Assert.Equal(Light.Green, heard.Light);
        Assert.Equal("listening", heard.Value);
    }

    /// <summary>A device that stalls without a fault (it stays open and delivers nothing) is silent like one that delivers silence:
    /// after 10 s preflight is amber and STATE counts the silence.</summary>
    [Fact]
    public async Task AnInputThatStopsDeliveringCountsAsSilent()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        var check = new AudioPreflight();
        async Task<PreflightRow> Row() => Assert.Single(await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None));
        await rig.Ok("MOD BIND ly_base opacity audio:bass");
        await rig.Ok("AUDIO LISTEN fake:desk CHANNELS 1");
        var capture = rig.Fake.Captures.Single();
        var tone = Sounds.BassBurst(9_600, 0);
        for (var at = 0; at < tone.Length; at += 480)
        {
            rig.AdvanceMs(10);
            capture.Push(tone.AsSpan(at, 480), rig.Clock.NowNs);
        }
        await rig.Engine.Lane.RunAsync(rig.Engine.RefreshAudioFacts);
        Assert.Equal(Light.Green, (await Row()).Light);

        // Nothing more arrives: after a second the newest frame is stale, and the silence counts from there.
        rig.AdvanceMs(2_000);
        await rig.Engine.Lane.RunAsync(rig.Engine.RefreshAudioFacts);
        rig.AdvanceMs(11_000);
        await rig.Engine.Lane.RunAsync(rig.Engine.RefreshAudioFacts);
        var row = await Row();
        Assert.Equal(Light.Amber, row.Light);
        Assert.Equal("silent", row.Value);
        var audio = rig.State().GetProperty("audio");
        Assert.True(audio.GetProperty("silent").GetBoolean());
        Assert.True(audio.GetProperty("input").GetProperty("silentForS").GetDouble() >= 10);
        Assert.Equal("listening", audio.GetProperty("input").GetProperty("state").GetString());
    }

    [Fact]
    public async Task TheExesCompositionCarriesTheCheckAndTheHub()
    {
        Assert.Contains(AudioExtension.Factory, EngineParts.Standard().Extensions);
        await using var rig = new AudioRig();
        // The part reads nothing of the host's setup until it saves this machine's choices.
        await using var part = AudioExtension.Factory(null!);
        Assert.Equal("audio", part.Name);
        var configured = part.Configure(rig.Engine.Services);
        Assert.Single(configured.PreflightChecks, c => c is AudioPreflight);
        Assert.NotNull(configured.AudioRuntime);
        Assert.Same(configured.AudioRuntime!.Hub, configured.AudioInputs);
        Assert.Same(configured.AudioRuntime.RenderBus, configured.Modulation);
        Assert.Contains(configured.AudioRuntime.Hub.Inputs(), i => i.Key == "test:click");
    }

    [Fact]
    public async Task TheTempoIndexSectionAndTheContextItems()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        var section = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "tempo");
        Assert.Equal("tempo", section.Hint.RowKind);
        foreach (var key in section.Hint.Keys)
        {
            Assert.True(WireGrammar.TryParse(key.DownLine, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && rig.Engine.IsAvailable(m.Spec), key.DownLine);
            // A key presses as the catalogue says its verb is pressed (a controller holds only what the catalogue holds).
            Assert.Equal(VerbParams.Press(VerbCatalog.Match(parsed)!.Value.Spec), key.Press);
        }
        Assert.Contains(section.Hint.Keys, k => k.DownLine == "TEMPO TAP");
        Assert.Contains(section.Hint.Keys, k => k.DownLine == "TEMPO RESYNC BAR");
        JsonElement Row()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
                section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
            using var doc = JsonDocument.Parse(ms.ToArray());
            return Assert.Single(doc.RootElement.EnumerateArray()).Clone();
        }
        string ContextLight(string id)
        {
            using var ctx = JsonDocument.Parse(rig.Engine.Context());
            return ctx.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id)
                .GetProperty("light").GetString()!;
        }
        var row = Row();
        Assert.Equal(["id", "kind", "label", "parent", "light", "state"], row.EnumerateObject().Select(p => p.Name));
        Assert.Equal("120 BPM", row.GetProperty("label").GetString());
        // The row is lit as the context lights the clock: grey until something uses it.
        Assert.Equal("grey", row.GetProperty("light").GetString());
        Assert.Equal("grey", ContextLight("tempo"));
        // BPM on an encoder: a turn moves it by one.
        await rig.Ok("TEMPO SET +1");
        Assert.Equal(121, rig.Engine.Store.Project.Tempo.Bpm);
        await rig.Ok("TEMPO SET -1");
        Assert.Equal(120, rig.Engine.Store.Project.Tempo.Bpm);

        Assert.Equal("grey", ContextLight("audio"));
        Assert.Equal("green", ContextLight("tempo"));
        Assert.Equal("green", Row().GetProperty("light").GetString());
        await rig.Ok("MOD BIND ly_base opacity audio:bass");
        Assert.Equal("red", ContextLight("audio"));
        // Detection searching for a beat is amber on both.
        await rig.Ok("TEMPO SOURCE DETECT");
        Assert.Equal("amber", ContextLight("tempo"));
        Assert.Equal("amber", Row().GetProperty("light").GetString());
        Assert.Equal("detect", Row().GetProperty("state").GetString());
    }
}
