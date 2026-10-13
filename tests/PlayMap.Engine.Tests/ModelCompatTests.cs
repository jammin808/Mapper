using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.Engine.Tests;

/// <summary>Every round-2 entity kind through the generic edits a desk, Patterns or an AI uses: GET, SET, ADD, MOVE and REMOVE;
/// the kinds only their own verbs make are refused by ADD with the verb to use; a calibration result is never edited.</summary>
public sealed class ModelCompatTests
{
    private static readonly SessionInfo Paired = new("t1", "wire", "10.0.0.9", false, "Test", true);

    private static async Task<CommandReply> Run(EngineCore engine, string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return await engine.HandleAsync(line, Paired, CancellationToken.None);
    }

    private static async Task<string> Ok(EngineCore engine, string text)
    {
        var r = await Run(engine, text);
        Assert.True(r.Ok, $"{text} → {r.Payload}");
        return r.Payload ?? "";
    }

    private static async Task<JsonElement> Get(EngineCore engine, string id) => JsonDocument.Parse(await Ok(engine, "GET " + id)).RootElement.Clone();

    /// <summary>A project holding the round-2 kinds that only their own verbs make, and a projector whose ledger names a result.</summary>
    private static Project Seeded()
    {
        var p = Project.CreateDefault("Show");
        var projector = new Projector("pj0a0b0c01", "Left", Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0, 0.5), LensDistortion.None,
            Pose.LookAt(new Vec3d(0, 1.5, -6), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 12000, "#FF8800", false, [], SolveLocks.None, 0)
        { CalibrationId = "cs0a0b0c01" };
        var result = new CalibrationSnapshot("cs0a0b0c01", "First light", CalibrationTarget.Projector, projector.Id, 1, CalibrationSource.Pins,
            null, null, QualityScore.None, "desk", false);
        var spare = result with { Id = "cs0a0b0c02", Name = "Spare" };
        var media = new MediaItem("md0a0b0c01", "Clip", MediaKind.Video, "clip.mov", "", 0, MediaInfo.Unknown, [])
        {
            Versions = [new MediaVersion("mv0a0b0c01", "hap", "{}", "ok:1", "sha256:1", 1, MediaInfo.Unknown, VersionQuality.None, "e", "n", 1)],
        };
        return p with
        {
            Media = [media],
            Stage = p.Stage with
            {
                Projectors = [projector],
                Calibrations = [result, spare],
                Surveys = [new SurveySource("sv0a0b0c01", "Facade", SurveyFormat.Ply, "a.ply", "", "", 0, 0, null, "", 0, 0, SurveyState.Ready, 1, "")],
                Aligns = [new AlignGroup("al0a0b0c01", "Screens", "", [], "", ScreenShape.Flat, null, AlignTarget.Default, AlignOptions.Default, "")],
            },
            FxAssets = [new FxAsset("fa0a0b0c01", "Glow", FxFormat.Isf, "glow.fs", "sha256:2")],
            GeneratorPresets = [new GeneratorPreset("gp0a0b0c01", "Deep", "fractal.julia", ParamSet.Empty)],
            Fleet = FleetPlan.Default with { Members = [new FleetMember("fm0a0b0c01", "nd2", "Backup", FleetRole.Backup, "nd1", BackupMode.Hot, 1500)] },
        };
    }

    [Theory]
    [InlineData("binding", "bd", "{\"name\":\"Fader\",\"input\":\"OSC /fader/1\",\"target\":\"SET sf1 opacity *\"}", "input", "OSC /fader/2")]
    [InlineData("controlsurface", "ct", "{\"name\":\"APC40\",\"kind\":\"midi\"}", "kind", "osc")]
    [InlineData("timecodesource", "tc", "{\"name\":\"LTC\",\"kind\":\"ltc\",\"channel\":2}", "channel", "3")]
    [InlineData("send", "sd", "{\"name\":\"Program\",\"protocol\":\"ndi\",\"tap\":\"output:op_1\"}", "quality", "high")]
    [InlineData("lednode", "ln", "{\"name\":\"Truss\",\"protocol\":\"sacn\"}", "sacnPriority", "150")]
    [InlineData("fixturetype", "lt", "{\"name\":\"RGBW\"}", "wattsPerPixel", "0.3")]
    [InlineData("fixture", "lf", "{\"name\":\"Strip\",\"space\":\"output\",\"spaceId\":\"op_1\"}", "footprint", "2")]
    [InlineData("controlpoint", "cp", "{\"name\":\"CP1\",\"grid\":{\"x\":1,\"y\":2,\"z\":3}}", "sigmaM", "0.002")]
    [InlineData("stack", "sk", "{\"name\":\"Double\",\"projectorIds\":[\"pj0a0b0c01\"],\"masterId\":\"pj0a0b0c01\"}", "failover", "\"match\"")]
    public async Task ATopLevelKindIsAddedReadSetMovedAndRemoved(string kind, string prefix, string json, string field, string value)
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Seeded());

        var id = await Ok(engine, $"ADD {kind} {json}");
        var other = await Ok(engine, $"ADD {kind} {{}}");

        Assert.StartsWith(prefix, id, StringComparison.Ordinal);
        Assert.True(Ids.IsValid(id));
        var got = await Get(engine, id);
        Assert.Equal(id, got.GetProperty("id").GetString());
        await Ok(engine, $"SET {id} {field} {value}");
        var set = await Get(engine, id);
        Assert.Equal(value.Trim('"'), set.GetProperty(field).ToString());
        await Ok(engine, $"MOVE {id} 1");
        Assert.Contains(id, engine.Store.Project.AllIds());
        await Ok(engine, $"REMOVE {other}");
        Assert.DoesNotContain(other, engine.Store.Project.AllIds());

        var file = ProjectJson.Serialize(engine.Store.Project);
        Assert.Equal(file, ProjectJson.Serialize(ProjectJson.Deserialize(file)));
        Assert.Contains(id, ProjectJson.Deserialize(file).AllIds());
    }

    [Fact]
    public async Task AnEffectSlotIsAddedToItsOwnersChainAndEditedThere()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Seeded());

        var onOutput = await Ok(engine, "ADD fx op_1 {\"kind\":\"fx.blur\",\"name\":\"Soft\"}");
        var onLayer = await Ok(engine, "ADD fx {\"parent\":\"ly_base\",\"kind\":\"fx.glow\"}");
        var second = await Ok(engine, "ADD fx op_1 {\"kind\":\"fx.tint\"}");

        Assert.StartsWith(Ids.Fx, onOutput, StringComparison.Ordinal);
        Assert.Equal(new[] { onOutput, second }, engine.Store.Project.Outputs[0].Fx.Slots.Select(s => s.Id));
        Assert.Equal(onLayer, Assert.Single(engine.Store.Project.Canvases[0].Layers[0].Fx.Slots).Id);
        Assert.Equal("Soft", (await Get(engine, onOutput)).GetProperty("name").GetString());
        await Ok(engine, $"SET {onOutput} mix 0.5");
        Assert.Equal(0.5, engine.Store.Project.FindFx(onOutput)!.Value.Slot.Mix);
        await Ok(engine, $"MOVE {second} 0");
        Assert.Equal(new[] { second, onOutput }, engine.Store.Project.Outputs[0].Fx.Slots.Select(s => s.Id));
        await Ok(engine, $"REMOVE {onLayer}");
        Assert.Empty(engine.Store.Project.Canvases[0].Layers[0].Fx.Slots);

        Assert.Contains("needs a kind", (await Run(engine, "ADD fx op_1 {}")).Payload, StringComparison.Ordinal);
        Assert.Contains("mix is 0 to 1", (await Run(engine, $"SET {second} mix 2")).Payload, StringComparison.Ordinal);
        Assert.Contains("none of them", (await Run(engine, "ADD fx pj0a0b0c01 {\"kind\":\"fx.blur\"}")).Payload, StringComparison.Ordinal);
        Assert.False((await Run(engine, "ADD fx {\"kind\":\"fx.blur\"}")).Ok);
    }

    [Theory]
    [InlineData("fxasset", "use FX IMPORT")]
    [InlineData("generatorpreset", "use GENERATOR SAVE")]
    [InlineData("survey", "use SURVEY IMPORT")]
    [InlineData("align", "use ALIGN NEW")]
    [InlineData("calibration", "calibration results are made by solves")]
    [InlineData("version", "use MEDIA OPTIMISE")]
    [InlineData("fleetmember", "use FLEET BACKUP on the backup")]
    public async Task AKindOnlyItsOwnVerbMakesIsRefusedByAddWithThatVerb(string kind, string words)
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Seeded());
        var revision = engine.Store.Current.Revision;

        var r = await Run(engine, $"ADD {kind} {{\"name\":\"x\"}}");

        Assert.False(r.Ok);
        Assert.Equal(words, r.Payload);
        Assert.Equal(revision, engine.Store.Current.Revision);
    }

    [Theory]
    [InlineData("fa0a0b0c01", "Glow")]
    [InlineData("gp0a0b0c01", "Deep")]
    [InlineData("sv0a0b0c01", "Facade")]
    [InlineData("al0a0b0c01", "Screens")]
    [InlineData("cs0a0b0c02", "Spare")]
    [InlineData("fm0a0b0c01", "Backup")]
    public async Task AKindMadeByItsOwnVerbIsStillReadAndRenamedById(string id, string name)
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Seeded());

        Assert.Equal(name, (await Get(engine, id)).GetProperty("name").GetString());
        await Ok(engine, $"SET {id} name Renamed");
        Assert.Equal("Renamed", (await Get(engine, id)).GetProperty("name").GetString());
        await Ok(engine, "UNDO");
        Assert.Equal(name, (await Get(engine, id)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task ACalibrationResultIsNeverEditedAndOneInUseIsNeverRemoved()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Seeded());
        var revision = engine.Store.Current.Revision;

        var score = await Run(engine, "SET cs0a0b0c01 score.value 0.1");
        var target = await Run(engine, "SET cs0a0b0c01 targetId pj_other");

        Assert.Equal("a calibration result is never edited — CALIBRATION RECALL makes a new one", score.Payload);
        Assert.False(target.Ok);
        Assert.Equal(revision, engine.Store.Current.Revision);
        await Ok(engine, "SET cs0a0b0c01 pinned true");
        Assert.True(engine.Store.Project.FindCalibration("cs0a0b0c01")!.Pinned);

        var remove = await Run(engine, "REMOVE cs0a0b0c01");
        Assert.False(remove.Ok);
        Assert.Contains("in use by Left", remove.Payload, StringComparison.Ordinal);
        await Ok(engine, "REMOVE cs0a0b0c02");
        Assert.Null(engine.Store.Project.FindCalibration("cs0a0b0c02"));

        // A version is found inside its clip; the clip that plays it keeps it.
        Assert.Equal("hap", (await Get(engine, "mv0a0b0c01")).GetProperty("to").GetString());
        await Ok(engine, "SET md0a0b0c01 use mv0a0b0c01");
        Assert.Contains("in use by Clip", (await Run(engine, "REMOVE mv0a0b0c01")).Payload, StringComparison.Ordinal);
    }
}
