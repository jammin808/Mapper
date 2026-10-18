using System.Text.Json.Nodes;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Show;

namespace PlayMap.Core.Tests.Model;

/// <summary>The model against earlier rounds' files: a file written before it loads unchanged (round 3 reading only its blend γ
/// differently), re-saves with every new key, reads a null new key as its default, and every new entity kind and field survives the
/// file.</summary>
public sealed class ModelCompatTests
{
    /// <summary>Every key round 3 adds, as a path (declared first: the list below takes it in).</summary>
    private static readonly string[] RoundThreeKeys =
    [
        "$.guides", "$.canvases[].span", "$.stage.viewpoints[].fit",
        "$.outputs[].colorBits", "$.outputs[].hdr", "$.outputs[].nitTarget", "$.outputs[].framesInFlight", "$.outputs[].watermark",
        "$.outputs[].passThrough",
    ];

    /// <summary>Every key round 2 and round 3 add, as a path ("[]" for every element of a list).</summary>
    private static readonly string[] NewKeys =
    [
        "$.show", "$.fleet", "$.control", "$.timecode", "$.sends", "$.fxAssets", "$.generatorPresets", "$.audio", "$.tempo", "$.leds",
        "$.optimise",
        "$.settings.syncLatencyMs", "$.settings.calibrationKeep", "$.settings.calibrationGate",
        "$.media[].color", "$.media[].versions", "$.media[].use", "$.media[].live",
        "$.playlists[].chase", "$.playlists[].items[].tcIn", "$.playlists[].items[].fx",
        "$.generators[].originNs", "$.generators[].bindings", "$.generators[].morph", "$.generators[].inputs",
        "$.canvases[].fx", "$.canvases[].layers[].fx", "$.canvases[].layers[].bindings",
        "$.stage.surfaces[].fx", "$.stage.surfaces[].bindings", "$.stage.surfaces[].pixel",
        "$.stage.projectors[].priors", "$.stage.projectors[].uncertainty", "$.stage.projectors[].focalLock", "$.stage.projectors[].calibrationId",
        "$.stage.projectors[].fallbackId", "$.stage.projectors[].stagedId", "$.stage.projectors[].pins[].controlId",
        "$.stage.cameras[].phone", "$.stage.cameras[].folder",
        "$.stage.devices[].snapshot",
        "$.stage.site", "$.stage.controls", "$.stage.surveys", "$.stage.stacks", "$.stage.aligns", "$.stage.calibrations",
        "$.outputs[].latencyMs", "$.outputs[].fx", "$.outputs[].measured", "$.outputs[].calibrationId", "$.outputs[].fallbackId",
        "$.outputs[].stagedId",
        .. RoundThreeKeys,
    ];

    [Fact]
    public void ARoundOneFileLoadsUnchangedAndReSavesWithEveryNewKey()
    {
        var text = RoundTwoSample.RoundOneFile();
        var p = ProjectJson.Deserialize(text);
        var resaved = ProjectJson.Serialize(p);

        var extras = new SortedSet<string>(StringComparer.Ordinal);
        AssertHolds(JsonNode.Parse(text)!, JsonNode.Parse(resaved)!, "$", extras);
        Assert.Equal(NewKeys.Order(StringComparer.Ordinal), extras);

        // The new keys hold their defaults, and the re-saved file reads back to itself.
        Assert.Equal(Json(ShowPlan.Default), Json(p.Show));
        Assert.Equal(Json(FleetPlan.Default), Json(p.Fleet));
        Assert.Equal(Json(ControlSet.Empty), Json(p.Control));
        Assert.Equal(Json(LedRig.Empty), Json(p.Leds));
        Assert.Equal(Json(SiteFrame.None), Json(p.Stage.Site));
        Assert.Equal(Json(ColorOverride.Auto), Json(p.Media[0].Color));
        Assert.Equal(Json(FxChain.Empty), Json(p.Outputs[0].Fx));
        Assert.Equal(-1, p.Settings.SyncLatencyMs);
        Assert.Equal("auto", p.Media[0].Use);
        Assert.Null(p.Media[0].Live);
        Assert.Null(p.Stage.Surfaces[0].Pixel);
        Assert.Null(p.Playlists[0].Chase);
        Assert.Empty(p.Generators[1].Inputs);
        Assert.Equal(resaved, ProjectJson.Serialize(ProjectJson.Deserialize(resaved)));
    }

    [Fact]
    public void AFileWithEveryNewKeyNullReadsTheDefaults()
    {
        var resaved = ProjectJson.Serialize(ProjectJson.Deserialize(RoundTwoSample.RoundOneFile()));
        var root = JsonNode.Parse(resaved)!;
        foreach (var key in NewKeys)
            SetNull(root, key["$.".Length..].Split('.'));
        var nulled = root.ToJsonString();
        Assert.Contains("\"show\":null", nulled, StringComparison.Ordinal);

        var p = ProjectJson.Deserialize(nulled);

        Assert.Equal(resaved, ProjectJson.Serialize(p));
        Assert.Same(ShowPlan.Default, p.Show);
        Assert.Same(SolvePriors.None, p.Stage.Projectors[0].Priors);
        Assert.Equal(FocalLockSource.Operator, p.Stage.Projectors[0].FocalLock);
        Assert.Same(ColorOverride.Auto, p.Media[0].Color);
        Assert.False(p.Sends.IsDefault);
        Assert.Null(p.Outputs[0].Measured);

        // Nested: a null inside a new record reads its own default; a nullable one stays null.
        var inner = JsonNode.Parse(resaved)!;
        inner["show"]!["rules"] = null;
        inner["generators"]![1]!["params"] = null;
        var q = ProjectJson.Deserialize(inner.ToJsonString());
        Assert.Same(ShowRules.Default, q.Show.Rules);
        Assert.Same(ParamSet.Empty, q.Generators[1].Params);
    }

    [Fact]
    public void AKeyGivenTwiceOrInAnotherCaseKeepsItsLastValue()
    {
        var text = ProjectJson.Serialize(Project.CreateDefault("A"));
        var twice = text.Replace("\"name\": \"A\"", "\"name\": \"A\", \"name\": \"B\"", StringComparison.Ordinal);
        var cased = text.Replace("\"name\": \"A\"", "\"name\": \"A\", \"Name\": \"C\"", StringComparison.Ordinal);
        Assert.NotEqual(text, twice);

        Assert.Equal("B", ProjectJson.Deserialize(twice).Name);
        Assert.Equal("C", ProjectJson.Deserialize(cased).Name);
        Assert.Equal(2, ProjectJson.DeserializeEntity<ShowRules>("{\"minGoMsArmed\":1, /* the later wins */ \"MinGoMsArmed\":2,}").MinGoMsArmed);
        var output = Project.CreateDefault().Outputs[0];
        Assert.Equal(7, ProjectJson.SetPath(output, "plan", "{\"x\":1,\"y\":0,\"w\":10,\"h\":10,\"X\":7}").Plan.X);
    }

    [Fact]
    public void ASetOfNullIsRefusedForAFieldThatCannotHoldIt()
    {
        var slot = new FxSlot("fx0a0b0c01", "fx.blur", "Soft", ParamSet.Empty, false, 1, FxBlend.Normal, 1, 0);
        var output = Project.CreateDefault().Outputs[0] with { LatencyMs = 40, CalibrationId = "cs0a0b0c01", Fx = new FxChain([slot]) };
        var chain = "{\"slots\":[" + ProjectJson.SerializeEntity(slot).Replace("\"bindings\":[]", "\"bindings\":null", StringComparison.Ordinal) + "]}";

        var latency = Assert.Throws<EditRefusedException>(() => ProjectJson.SetPath(output, "latencyMs", "null"));
        var nested = Assert.Throws<EditRefusedException>(() => ProjectJson.SetPath(output, "fx", chain));

        Assert.Equal("'latencyMs' does not take null", latency.Message);
        Assert.Equal("'bindings' does not take null", nested.Message);
        Assert.Null(ProjectJson.SetPath(output, "calibrationId", "null").CalibrationId);
        Assert.Same(FxChain.Empty, ProjectJson.SetPath(output, "fx", "null").Fx);
        // A file reads the same null as the field's own default.
        Assert.Equal(0, ProjectJson.DeserializeEntity<Output>(ProjectJson.SerializeEntity(output).Replace("\"latencyMs\":40", "\"latencyMs\":null", StringComparison.Ordinal)).LatencyMs);
    }

    [Fact]
    public void EveryNewEntityKindRoundTripsThroughTheFile()
    {
        var p = RoundTwoSample.Build();
        var json = ProjectJson.Serialize(p);
        var back = ProjectJson.Deserialize(json);

        Assert.Equal(json, ProjectJson.Serialize(back));
        var ids = back.AllIds().ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var id in RoundTwoSample.NewIds)
            Assert.Contains(id, ids);

        Assert.Equal("Program", back.FindSend("sd0a0b0c01")!.Name);
        Assert.Equal("Fader 1", back.FindBinding("bd0a0b0c01")!.Name);
        Assert.Equal("APC40", back.FindControlSurface("ct0a0b0c01")!.Name);
        Assert.Equal(TimecodeKind.Ltc, back.FindTimecodeSource("tc0a0b0c01")!.Kind);
        Assert.Equal("Deep", back.FindGeneratorPreset("gp0a0b0c01")!.Name);
        Assert.Equal(StackFailover.Match, back.FindStack("sk0a0b0c01")!.Failover);
        Assert.Equal(ScreenShape.Cylinder, back.FindAlign("al0a0b0c01")!.Shape);
        Assert.Equal(CalibrationTarget.Output, back.FindCalibration("cs0a0b0c02")!.Target);
        var (slot, owner, index) = back.FindFx("fx0a0b0c03")!.Value;
        Assert.Equal(("Blur fx0a0b0c03", "ly0a0b0c01", 0), (slot.Name, owner, index));
        Assert.Equal("op_1", back.FindFx("fx0a0b0c05")!.Value.OwnerId);
        Assert.Null(back.FindFx("fx_absent"));
        Assert.Equal(BindingMode.Scale, back.FindFx("fx0a0b0c01")!.Value.Slot.Bindings[0].Mode);
        Assert.Equal(SourceRef.Media("md0a0b0c02"), back.Generators[1].Inputs["source"]);
        Assert.Equal(2_000_000_000, back.Generators[1].Morph!.LengthNs);
        Assert.Equal(PanicOutcome.Safe, back.Show.Panic[0].Outcome);
        Assert.Equal(LockKind.Area, back.Show.Locks[1].Kind);
        Assert.Equal(0.1, back.Control.Bindings[0].Shape.OutMin);
        Assert.Null(back.Control.Bindings[0].Shape.OutMax);
        Assert.Equal(SendFit.Fill, back.Sends[0].Fit);
        Assert.Equal(LedChannel.W, back.Leds.Types[0].PerPixel[3].Channel);
        Assert.True(back.Media[0].Versions[0].Review);
        Assert.Equal("cp0a0b0c01", back.Stage.Projectors[0].Pins[0].ControlId);
        Assert.Equal("0", back.Stage.Devices[0].Snapshot!.Values["keystone"]);
        Assert.Equal(0.4, back.Outputs[0].Measured!.Warp!.ResidualPx);
        Assert.Equal(RoundTwoSample.Measured().FittedTo, back.Outputs[0].Measured!.FittedTo);
        Assert.Equal(FocalLockSource.Lens, back.Stage.Projectors[0].FocalLock);
        Assert.Equal(FocalLockSource.Lens, back.FindCalibration("cs0a0b0c01")!.Projector!.FocalLock);
        Assert.Contains("\"focalLock\": \"lens\"", json, StringComparison.Ordinal);
        Assert.Equal(new Vec2d(0, -0.1), back.Stage.Surfaces[0].Pixel!.Lattice!.Offsets[3]);
        Assert.Equal("iPhone", back.Stage.Cameras[0].Phone!.Model);
        Assert.Equal(EncodeQuality.Archive, back.Optimise.Rules[0].Quality);
        Assert.Equal(new[] { MediaKind.Video }, back.Optimise.Rules[0].When.Kinds);
        Assert.Equal(LiveLoss.Fallback, back.Media[0].Live!.OnLoss);
    }

    [Fact]
    public void EveryIdPrefixIsTwoLettersAndNoRuntimePrefixNamesAProjectEntity()
    {
        var prefixes = typeof(Ids).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(prefixes.Count, prefixes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(prefixes, x => Assert.Matches("^[a-z]{2}$", x));
        Assert.Empty(prefixes.Intersect(Ids.RuntimePrefixes, StringComparer.Ordinal));
        Assert.Equal(Ids.RuntimePrefixes.Count, Ids.RuntimePrefixes.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("vp", prefixes);
        Assert.Contains("pn", prefixes);
    }

    [Fact]
    public void TheSiteFrameRebasesAFacadeFarFromTheGridOriginToTheMillimetre()
    {
        var site = new SiteFrame("EPSG:25832", "UTM 32N", new Vec3d(500_000.123, 5_400_000.456, 120.5), 30, 0.9996, 50);
        var grid = new Vec3d(500_123.789, 5_400_045.012, 133.25);

        var stage = site.ToStage(grid);
        var back = site.ToGrid(stage);

        Assert.Equal(grid.X, back.X, 1e-6);
        Assert.Equal(grid.Y, back.Y, 1e-6);
        Assert.Equal(grid.Z, back.Z, 1e-9);
        Assert.Equal(133.25 - 120.5, stage.Y, 1e-9);
        // Distances on the ground are grid distances over the scale; height is not scaled.
        var d = Math.Sqrt(Math.Pow(grid.X - site.Origin.X, 2) + Math.Pow(grid.Y - site.Origin.Y, 2)) / 0.9996;
        Assert.Equal(d, Math.Sqrt(stage.X * stage.X + stage.Z * stage.Z), 1e-6);
        // Unrotated and unscaled, grid east is stage +X and grid north is stage −Z.
        var plain = SiteFrame.None with { Crs = "LOCAL" };
        Assert.Equal(new Vec3d(1, 0, -2), plain.ToStage(new Vec3d(1, 2, 0)));
        Assert.True(plain.IsSet);
        Assert.False(SiteFrame.None.IsSet);
    }

    [Theory]
    [InlineData(0.9, "px", false, "A")]
    [InlineData(1.0, "px", false, "A")]
    [InlineData(1.2, "px", false, "B")]
    [InlineData(3.9, "px", false, "C")]
    [InlineData(4.1, "px", false, "F")]
    [InlineData(0.5, "px", true, "F")]
    [InlineData(90, "points", false, "A")]
    [InlineData(70, "points", false, "B")]
    [InlineData(55, "points", false, "C")]
    [InlineData(49, "points", false, "F")]
    public void AScoreIsGradedOnItsUnitsScale(double value, string unit, bool failed, string grade) =>
        Assert.Equal(grade, QualityScore.GradeOf(value, unit, failed));

    [Fact]
    public void ScoreTermCodesAreOneVocabulary()
    {
        Assert.Equal(15, ScoreTermCodes.All.Count);
        Assert.Contains(ScoreTermCodes.DenseRms, (IReadOnlySet<string>)ScoreTermCodes.All);
        Assert.Equal(LockAreas.All.Count, LockAreas.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AnAirFadeIsAPureFunctionOfTheLeaderInstant()
    {
        var fade = new AirFade(1, 0, 1_000, 1_000);
        Assert.Equal(1f, fade.At(0));
        Assert.Equal(1f, fade.At(1_000));
        Assert.Equal(0.5f, fade.At(1_500), 5);
        Assert.Equal(0f, fade.At(2_000));
        Assert.Equal(0f, fade.At(9_000));
        Assert.False(fade.Settled(1_999));
        Assert.True(fade.Settled(2_000));
        Assert.Equal(0f, new AirFade(1, 0, 1_000, 0).At(1_001));
        Assert.Equal(1f, AirFade.Full.At(123));
    }

    [Fact]
    public void TheGuardRefusesEditsUndoAndRedoWithItsWords()
    {
        var store = new ProjectStore(Project.CreateDefault());
        store.Apply("rename", p => p with { Name = "A" });
        store.Guard = (before, after) => after.Name == "Locked" || (before.Name == "A" && after.Name == "Untitled") ? "the name is locked" : null;

        var refused = store.Apply("rename", p => p with { Name = "Locked" });
        Assert.False(refused.Changed);
        Assert.Equal("the name is locked", refused.Effect);
        var revision = store.Current.Revision;
        var undo = store.Undo();
        Assert.False(undo.Changed);
        Assert.Equal("the name is locked", undo.Effect);
        Assert.Equal(revision, store.Current.Revision);
        Assert.Equal("A", store.Project.Name);
        Assert.True(store.CanUndo);

        store.Guard = null;
        Assert.True(store.Undo().Changed);
        store.Guard = (_, after) => after.Name == "A" ? "no" : null;
        Assert.False(store.Redo().Changed);
        Assert.Equal("Untitled", store.Project.Name);
        Assert.True(store.CanRedo);
    }

    [Fact]
    public void AnUnrecordedEditMakesNoUndoStepAndAnUndoKeepsIt()
    {
        var store = new ProjectStore(Project.CreateDefault());
        store.Apply("rename", p => p with { Name = "A" });
        var keepLocks = (Func<Project, Project, Project>)((older, now) => older with { Show = older.Show with { Locks = now.Show.Locks } });
        var lockIt = new ShowLock("op_1", LockKind.Entity, "desk", 1, "");

        var r = store.ApplyUnrecorded("lock op_1", p => p with { Show = p.Show with { Locks = [lockIt] } }, keepLocks);

        Assert.True(r.Changed);
        Assert.Equal("rename", store.UndoLabel);
        Assert.True(store.Undo().Changed);
        Assert.Equal("Untitled", store.Project.Name);
        Assert.Equal(new[] { lockIt }, store.Project.Show.Locks);
        Assert.True(store.Redo().Changed);
        Assert.Equal(new[] { lockIt }, store.Project.Show.Locks);
        Assert.False(store.ApplyUnrecorded("lock op_1", p => p).Changed);
    }

    [Fact]
    public void AGroupIsOneUndoStepAndOneRefusalRefusesItAll()
    {
        var store = new ProjectStore(Project.CreateDefault());
        var changes = 0;
        store.Changed += _ => changes++;

        var r = store.ApplyGroup("two", [p => p with { Name = "A" }, p => p with { Name = p.Name + "B" }]);

        Assert.True(r.Changed);
        Assert.Equal(1, changes);
        Assert.Equal("AB", store.Project.Name);
        var refused = store.ApplyGroup("bad", [p => p with { Name = "C" }, _ => throw new EditRefusedException("no")]);
        Assert.False(refused.Changed);
        Assert.Equal("no", refused.Effect);
        Assert.Equal("AB", store.Project.Name);
        Assert.True(store.Undo().Changed);
        Assert.Equal("Untitled", store.Project.Name);
    }

    /// <summary>Each project file an earlier round wrote, frozen as that round's model wrote it: the outputs whose blend γ round 3
    /// reads as 0, and the keys a re-save adds.</summary>
    public static TheoryData<string, string[], string[]> OlderFiles => new()
    {
        // Round 1: both outputs stored the default 2.2 and have no measured layer.
        { "round1.playmap.json", ["op_1", "op0a0b0c02"], NewKeys },
        // Round 2: op_1 stores the γ its current result recorded (an accept wrote it, the measured γ 2.31); op0a0b0c02 a hand's 1.8;
        // op0a0b0c03 the default 2.2; op0a0b0c04 a 2.2 typed over a measured 2.4, which 0 would not draw.
        { "round2.playmap.json", ["op_1", "op0a0b0c03"], RoundThreeKeys },
    };

    [Theory]
    [MemberData(nameof(OlderFiles))]
    public void AnOlderFileReadsUnchangedButForItsBlendGammaAndItsLoadNoteNamesEveryOutputMoved(string file, string[] moved, string[] added)
    {
        var text = File.ReadAllText(Path.Combine(RoundTwoSample.RepoRoot(), "tests", "fixtures", "projects", file));
        var stored = ProjectJson.Deserialize(text);
        Assert.Equal(1, stored.Schema);

        var read = ProjectJson.Read(text);

        Assert.Equal(moved, read.MovedOutputs);
        // Every key and value of the file is held, but the schema, raised, and each moved output's γ, now 0.
        var expected = JsonNode.Parse(text)!;
        expected["schema"] = Project.CurrentSchema;
        foreach (var o in expected["outputs"]!.AsArray())
        {
            if (moved.Contains((string)o!["id"]!))
                o["blend"]!["gamma"] = 0;
        }
        var extras = new SortedSet<string>(StringComparer.Ordinal);
        AssertHolds(expected, JsonNode.Parse(ProjectJson.Serialize(read.Project))!, "$", extras);
        Assert.Equal(added.Order(StringComparer.Ordinal), extras);
        // No output draws another γ than the file drew.
        Assert.Equal(stored.Outputs.Select(o => o.Blend.Gamma), read.Project.Outputs.Select(o => o.DrawnBlendGamma()));
        Assert.All(read.Project.Outputs, o => Assert.Equal(moved.Contains(o.Id), o.Blend.Gamma == 0));
        // The load note names every output moved, and only those.
        foreach (var o in read.Project.Outputs)
            Assert.Equal(moved.Contains(o.Id), read.Note.Contains($"{o.Name} ({o.Id})", StringComparison.Ordinal));
        Assert.Contains("reads as 0", read.Note, StringComparison.Ordinal);

        // What this build saves it reads as it wrote it: nothing moves again.
        var again = ProjectJson.Read(ProjectJson.Serialize(read.Project));
        Assert.Empty(again.MovedOutputs);
        Assert.Equal("", again.Note);
        Assert.Equal(ProjectJson.Serialize(read.Project), ProjectJson.Serialize(again.Project));
    }

    [Fact]
    public void ARoundThreeFileIsReadAsItIsWrittenAndA22TypedByHandStays()
    {
        var p = Project.CreateDefault("Hand");
        var one = p.Outputs[0];
        p = p with
        {
            Outputs =
            [
                one with { Blend = one.Blend with { Gamma = 2.2 } },
                one with { Id = "op_2", Name = "Output 2" },
                one with { Id = "op_3", Name = "Output 3", Blend = one.Blend with { Gamma = 1.8 } },
            ],
        };
        var text = ProjectJson.Serialize(p);

        var read = ProjectJson.Read(text);

        Assert.Equal(Project.CurrentSchema, read.Project.Schema);
        Assert.Empty(read.MovedOutputs);
        Assert.Equal("", read.Note);
        Assert.Equal(text, ProjectJson.Serialize(read.Project));
        Assert.Equal(new[] { 2.2, 0, 1.8 }, read.Project.Outputs.Select(o => o.Blend.Gamma));
        Assert.Equal(new[] { 2.2, 2.2, 1.8 }, read.Project.Outputs.Select(o => o.DrawnBlendGamma()));
    }

    [Fact]
    public void TheGammaRuleMovesAStored22OrTheRecordedGammaOnlyWhereZeroDrawsTheSame()
    {
        var p = Project.CreateDefault("Gamma");
        var o = p.Outputs[0];
        static MeasuredCorrection Measured(double gamma) =>
            new(null, null, null, new MeasuredColor(1, 1, 1, gamma, 1), 0, "", "", "", 1);
        var result = new CalibrationSnapshot("cs00000001", "Align", CalibrationTarget.Output, "op_b", 1, CalibrationSource.Align, null, null,
            QualityScore.None, "desk", false) { Output = Measured(2.31), BlendGamma = 2.31 };
        Output Out(string id, double gamma, MeasuredCorrection? measured = null, string? calibration = null) =>
            o with { Id = id, Name = id, Blend = o.Blend with { Gamma = gamma }, Measured = measured, CalibrationId = calibration };
        p = p with
        {
            Outputs =
            [
                Out("op_a", 2.2),                                              // the old default, nothing measured → 0
                Out("op_b", 2.31, Measured(2.31), result.Id),                  // the γ its result recorded, drawn by 0 → 0
                Out("op_c", 2.31, Measured(2.2), result.Id),                   // the recorded γ, but 0 would draw 2.2 → stays
                Out("op_d", 1.8, Measured(2.31), result.Id),                   // the hand's → stays
                Out("op_e", 2.31, Measured(2.31)),                             // no current result: not a recorded γ → stays
                Out("op_f", 2.2, Measured(2.4)),                               // 2.2 typed over a measured 2.4 → stays
                Out("op_g", 2.2, Measured(2.2) with { Color = null }),         // a measured layer with no γ: 0 draws 2.2 → 0
                Out("op_h", 0),                                                // 0 already → not moved
            ],
            Stage = p.Stage with { Calibrations = [result] },
        };

        var (read, moved) = ProjectJson.ReadGammaAsTheHands(p);

        Assert.Equal(new[] { "op_a", "op_b", "op_g" }, moved);
        Assert.Equal(new[] { 0, 0, 2.31, 1.8, 2.31, 2.2, 0, 0 }, read.Outputs.Select(x => x.Blend.Gamma));
        Assert.Equal(p.Outputs.Select(x => x.DrawnBlendGamma()), read.Outputs.Select(x => x.DrawnBlendGamma()));
        // A project with nothing to move comes back as it was.
        var untouched = p with { Outputs = [p.Outputs[3], p.Outputs[7]] };
        Assert.Same(untouched, ProjectJson.ReadGammaAsTheHands(untouched).Project);
        Assert.Empty(ProjectJson.ReadGammaAsTheHands(untouched).Moved);
    }

    [Fact]
    public void TheDrawnGammaIsTheHandsElseTheMeasuredElseTwoPointTwo()
    {
        Assert.Equal(0, BlendSettings.Default.Gamma);
        var measured = new MeasuredCorrection(null, null, null, new MeasuredColor(1, 1, 1, 2.4, 1), 0, "", "", "", 1);
        Assert.Equal(2.2, BlendSettings.Default.DrawnGamma(null));
        Assert.Equal(2.4, BlendSettings.Default.DrawnGamma(measured));
        Assert.Equal(2.2, BlendSettings.Default.DrawnGamma(measured with { Color = new MeasuredColor(1, 1, 1, 0, 1) }));
        Assert.Equal(1.8, (BlendSettings.Default with { Gamma = 1.8 }).DrawnGamma(measured));
        var output = Project.CreateDefault().Outputs[0] with { Measured = measured };
        Assert.Equal(2.4, output.DrawnBlendGamma());
    }

    [Fact]
    public void EveryRoundThreeFieldHoldsItsDefaultAndRoundTripsThroughTheFile()
    {
        var p = ProjectJson.Read(RoundTwoSample.RoundOneFile()).Project;
        var o = p.Outputs[0];
        Assert.Equal((8, HdrMode.Off, 203.0, 0, false), (o.ColorBits, o.Hdr, o.NitTarget, o.FramesInFlight, o.PassThrough));
        Assert.Null(o.Watermark);
        Assert.Null(p.Canvases[0].Span);
        Assert.Null(p.Stage.Viewpoints[0].Fit);
        Assert.Empty(p.Guides);

        var span = new CanvasSpan(["op_1", "op0a0b0c02"], 2, 1, 1, 15, SpanOverlapUnit.Percent, 3552, 1080,
            [new SpanPlacement("op_1", new RectD(0, 0, 1920, 1080), new EdgeBlend(0, 0, 288, 0), new RectD(0, 0, 1920, 1080)),
             new SpanPlacement("op0a0b0c02", new RectD(1632, 0, 1920, 1080), new EdgeBlend(288, 0, 0, 0), new RectD(1920, 0, 1920, 1080))]);
        var view = p.Stage.Viewpoints[0];
        var fit = new ViewpointFit(["sf0a0b0c01"], 5, new Vec3d(0, 0.1, 3.1), view.Intrinsics with { Fx = 1400, Fy = 1400 });
        var guide = new GuideRecord(GuideKind.Flat, "cv_main", "desk", 1_760_000_000_000,
            [new GuideCheck("corners", "sha256:" + new string('e', 64), "desk", 1_760_000_000_001)]);
        var set = p with
        {
            Outputs = p.Outputs.SetItem(0, o with
            {
                ColorBits = 10, Hdr = HdrMode.Hdr10, NitTarget = 300, FramesInFlight = 1, PassThrough = true,
                Watermark = new OutputWatermark(WatermarkPlace.Both, 16, 8, 12),
            }),
            Canvases = p.Canvases.SetItem(0, p.Canvases[0] with { Span = span }),
            Stage = p.Stage with { Viewpoints = p.Stage.Viewpoints.SetItem(0, view with { Fit = fit }) },
            Guides = [guide],
        };
        var json = ProjectJson.Serialize(set);
        var back = ProjectJson.Read(json).Project;

        Assert.Equal(json, ProjectJson.Serialize(back));
        var b = back.Outputs[0];
        Assert.Equal((10, HdrMode.Hdr10, 300.0, 1, true), (b.ColorBits, b.Hdr, b.NitTarget, b.FramesInFlight, b.PassThrough));
        Assert.Equal(new OutputWatermark(WatermarkPlace.Both, 16, 8, 12), b.Watermark);
        Assert.Contains("\"hdr\": \"hdr10\"", json, StringComparison.Ordinal);
        Assert.Contains("\"place\": \"both\"", json, StringComparison.Ordinal);
        Assert.Equal(span.Wrote.AsEnumerable(), back.Canvases[0].Span!.Wrote);
        Assert.Equal((2, 1, 1, 15.0, SpanOverlapUnit.Percent, 3552, 1080),
            (back.Canvases[0].Span!.Columns, back.Canvases[0].Span!.Rows, back.Canvases[0].Span!.Stack, back.Canvases[0].Span!.Overlap,
             back.Canvases[0].Span!.OverlapUnit, back.Canvases[0].Span!.Width, back.Canvases[0].Span!.Height));
        Assert.Equal(span.Outputs.AsEnumerable(), back.Canvases[0].Span!.Outputs);
        Assert.Equal(fit.Surfaces.AsEnumerable(), back.Stage.Viewpoints[0].Fit!.Surfaces);
        Assert.Equal((fit.MarginPercent, fit.Aim, fit.FieldOfView), (back.Stage.Viewpoints[0].Fit!.MarginPercent, back.Stage.Viewpoints[0].Fit!.Aim,
            back.Stage.Viewpoints[0].Fit!.FieldOfView));
        Assert.Equal((GuideKind.Flat, "cv_main", "desk", 1_760_000_000_000L), (back.Guides[0].Kind, back.Guides[0].Subject, back.Guides[0].Who,
            back.Guides[0].BegunUnixMs));
        Assert.Equal(guide.Checks.AsEnumerable(), back.Guides[0].Checks);
        // A field's path is the one a SET line names.
        Assert.Equal(10, ProjectJson.SetPath(o, "colorBits", "10").ColorBits);
        Assert.Equal(HdrMode.Scrgb, ProjectJson.SetPath(o, "hdr", "\"scrgb\"").Hdr);
        Assert.Equal(2.4, ProjectJson.SetPath(o, "blend.gamma", "2.4").Blend.Gamma);
    }

    private static string Json<T>(T value) => ProjectJson.SerializeEntity(value);

    /// <summary>Every key and value of the original is in the re-saved file, in the same place; the re-saved file's other keys
    /// are collected by path.</summary>
    private static void AssertHolds(JsonNode original, JsonNode resaved, string path, ISet<string> extras)
    {
        switch (original)
        {
            case JsonObject o:
                var r = Assert.IsType<JsonObject>(resaved);
                foreach (var (key, value) in o)
                {
                    Assert.True(r.ContainsKey(key), $"{path}.{key} was dropped");
                    if (value is null)
                        Assert.Null(r[key]);
                    else
                        AssertHolds(value, r[key]!, path + "." + key, extras);
                }
                foreach (var (key, _) in r)
                    if (!o.ContainsKey(key))
                        extras.Add(path + "." + key);
                break;
            case JsonArray a:
                var ra = Assert.IsType<JsonArray>(resaved);
                Assert.Equal(a.Count, ra.Count);
                for (var i = 0; i < a.Count; i++)
                    AssertHolds(a[i]!, ra[i]!, path + "[]", extras);
                break;
            default:
                Assert.True(JsonNode.DeepEquals(original, resaved), $"{path}: {original.ToJsonString()} became {resaved.ToJsonString()}");
                break;
        }
    }

    private static void SetNull(JsonNode node, string[] parts)
    {
        var head = parts[0];
        var list = head.EndsWith("[]", StringComparison.Ordinal);
        var key = list ? head[..^2] : head;
        if (parts.Length == 1)
        {
            node[key] = null;
            return;
        }
        if (list)
        {
            foreach (var element in node[key]!.AsArray())
                SetNull(element!, parts[1..]);
        }
        else
        {
            SetNull(node[key]!, parts[1..]);
        }
    }
}
