using System.Text.Json.Nodes;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Show;

namespace PlayMap.Core.Tests.Model;

/// <summary>Round 2's model against round 1's files: a file written before it loads unchanged, re-saves with every new key, reads
/// a null new key as its default, and every new entity kind survives the file.</summary>
public sealed class ModelCompatTests
{
    /// <summary>Every key round 2 adds, as a path ("[]" for every element of a list).</summary>
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
        "$.stage.projectors[].priors", "$.stage.projectors[].uncertainty", "$.stage.projectors[].calibrationId",
        "$.stage.projectors[].fallbackId", "$.stage.projectors[].stagedId", "$.stage.projectors[].pins[].controlId",
        "$.stage.cameras[].phone", "$.stage.cameras[].folder",
        "$.stage.devices[].snapshot",
        "$.stage.site", "$.stage.controls", "$.stage.surveys", "$.stage.stacks", "$.stage.aligns", "$.stage.calibrations",
        "$.outputs[].latencyMs", "$.outputs[].fx", "$.outputs[].measured", "$.outputs[].calibrationId", "$.outputs[].fallbackId",
        "$.outputs[].stagedId",
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
