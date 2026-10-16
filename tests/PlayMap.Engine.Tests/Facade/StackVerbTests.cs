using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Show;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>The stack verbs through the wire on one engine: STACK NEW groups and refuses (a member in another stack, named twice, or
/// off the master's picture); STACK ALIGN fits the member to the master from the scan of both and lands on its output's measured
/// correction through the ledger — the truth's misregistration drops under 0.2 px, STATE and the context say aligned, a second run is
/// refused as no change, UNDO takes it back; STACK MATCH lands gains ≤ 1; a lock on the output, a stopping node and a scan without a
/// member refuse with words; from a blind draft both stage.</summary>
public sealed class StackVerbTests(ITestOutputHelper log) : IAsyncDisposable
{
    private readonly ScanRunner _scans = new(null);

    public ValueTask DisposeAsync() => _scans.DisposeAsync();

    private ShowRig Rig(Core.Align.MultiProjectorSim sim) => new(StackKit.Project(sim), s => s with { Scan = _scans });

    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement.Clone();

    private static JsonElement Stage(ShowRig rig) => Json(rig.Engine.State().Json).GetProperty("stage");

    [Fact]
    public async Task StackNewGroupsTheMembersAndRefusesWhatCannotStack()
    {
        await using var rig = Rig(StackKit.Sim());
        Assert.Contains("named twice", await rig.Refused($"STACK NEW S1 {StackKit.P1} P1"), StringComparison.Ordinal);
        Assert.Contains("a stack shares one area", await rig.Refused($"STACK NEW S1 P1 P3"), StringComparison.Ordinal);
        Assert.Contains("the master must be one of the members", await rig.Refused("STACK NEW S1 P1 P2 MASTER P3"), StringComparison.Ordinal);

        var made = Json(await rig.Ok("STACK NEW S1 P1 P2 MASTER P2"));
        var id = made.GetProperty("id").GetString()!;
        Assert.StartsWith("sk", id, StringComparison.Ordinal);
        Assert.Equal(StackKit.P2, made.GetProperty("master").GetString());
        Assert.False(made.GetProperty("staged").GetBoolean());
        var stack = Assert.Single(rig.Engine.Store.Project.Stage.Stacks);
        Assert.Equal([StackKit.P1, StackKit.P2], stack.ProjectorIds.ToArray());
        Assert.Equal((StackKit.P2, StackFailover.Hold), (stack.MasterId, stack.Failover));

        var state = Stage(rig).GetProperty("stacks")[0];
        Assert.Equal(id, state.GetProperty("id").GetString());
        Assert.Equal("S1", state.GetProperty("name").GetString());
        Assert.Equal([StackKit.P1, StackKit.P2], state.GetProperty("members").EnumerateArray().Select(m => m.GetString()));
        Assert.False(state.GetProperty("aligned").GetBoolean());
        Assert.Equal("hold", state.GetProperty("failover").GetString());
        Assert.Equal(0, state.GetProperty("degraded").GetArrayLength());
        Assert.Equal(1.0, state.GetProperty("gain").GetDouble());
        var projectors = Stage(rig).GetProperty("projectors").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(id, projectors[StackKit.P1].GetProperty("stack").GetString());
        Assert.Equal(JsonValueKind.Null, projectors[StackKit.P3].GetProperty("stack").ValueKind);
        var item = Assert.Single(ContextBuilder.Build(rig.Engine).Items, i => i.Id == "stack:" + id);
        Assert.Equal(Core.Bus.Light.Amber, item.Light);
        Assert.Contains($"STACK ALIGN {id} FROM", item.State, StringComparison.Ordinal);

        Assert.Contains("is in stack S1 already", await rig.Refused("STACK NEW S2 P1 P3"), StringComparison.Ordinal);
        Assert.Contains("there is a stack S1 already", await rig.Refused("STACK NEW S1 P1 P2"), StringComparison.Ordinal);
        Assert.Contains("is in stack S1 already", await rig.Refused($$"""ADD stack {"name":"S9","projectorIds":["{{StackKit.P1}}","{{StackKit.P3}}"],"masterId":"{{StackKit.P1}}","failover":"hold"}"""), StringComparison.Ordinal);

        var list = Json(await rig.Ok("STACK LIST")).GetProperty("stacks")[0];
        Assert.Equal(id, list.GetProperty("id").GetString());
        Assert.Equal(2, list.GetProperty("gains").GetArrayLength());

        await rig.Ok($"REMOVE {id}");
        Assert.Empty(rig.Engine.Store.Project.Stage.Stacks);
    }

    [Fact]
    public async Task StackAlignFitsTheMemberToTheMasterFromTheScanThroughTheLedger()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        await using var rig = Rig(sim);
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        var before = StackKit.MisregistrationP95(sim, (WarpLattice?)null, null);

        var reply = Json(await rig.Ok($"STACK ALIGN {id} FROM {StackKit.Scan}"));
        log.WriteLine(reply.GetProperty("words").GetString());
        var members = reply.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("projector").GetString()!);
        Assert.True(members[StackKit.P1].GetProperty("master").GetBoolean());
        var member = members[StackKit.P2];
        Assert.False(member.GetProperty("master").GetBoolean());
        Assert.InRange(member.GetProperty("residualPx").GetDouble(), 0, 0.25);
        Assert.True(member.GetProperty("beforePx").GetDouble() > 0.8);
        Assert.True(member.GetProperty("heldOut").GetInt32() > 1000);

        var p = rig.Engine.Store.Project;
        var o1 = p.FindOutput("op00000001")!;
        var o2 = p.FindOutput("op00000002")!;
        // The master is the target: its output is left as it was.
        Assert.Null(o1.Measured);
        Assert.Null(o1.CalibrationId);
        var m = o2.Measured!;
        Assert.NotNull(m.Lattice);
        Assert.Equal((id, StackKit.Scan), (m.StackId, m.ScanId));
        var snap = p.FindCalibration(o2.CalibrationId!)!;
        Assert.Equal((CalibrationSource.Stack, CalibrationTarget.Output, "stack"), (snap.Source, snap.Target, snap.Score.Source));
        Assert.Equal(Core.Model.ScoreTermCodes.StackConvergence, Assert.Single(snap.Score.Terms).Code);
        Assert.NotNull(o2.FallbackId);
        var after = StackKit.MisregistrationP95(sim, o1.Measured?.Lattice, m.Lattice);
        log.WriteLine($"misregistration p95 {before:0.000} px before, {after:0.000} px after");
        Assert.True(before > 0.8, $"before {before}");
        Assert.True(after <= 0.2, $"after {after}");

        var state = Stage(rig).GetProperty("stacks")[0];
        Assert.True(state.GetProperty("aligned").GetBoolean());
        Assert.InRange(state.GetProperty("residualPx").GetDouble(), 0, 0.25);
        Assert.Equal(Core.Bus.Light.Green, ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id == "stack:" + id).Light);

        Assert.Contains("would change nothing", await rig.Refused($"STACK ALIGN {id} FROM {StackKit.Scan}"), StringComparison.Ordinal);
        await rig.Ok("UNDO");
        Assert.Null(rig.Engine.Store.Project.FindOutput("op00000002")!.Measured);
    }

    [Fact]
    public async Task StackMatchBringsTheMembersWhiteToTheMastersWithGainsOfAtMostOne()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        await using var rig = Rig(sim);
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        var reply = Json(await rig.Ok($"STACK MATCH S1 FROM {StackKit.Scan}"));
        log.WriteLine(reply.GetProperty("words").GetString());
        foreach (var m in reply.GetProperty("members").EnumerateArray())
            Assert.All(m.GetProperty("gain").EnumerateArray(), g => Assert.InRange(g.GetDouble(), 0, 1));
        var member = reply.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("projector").GetString() == StackKit.P2);
        Assert.True(member.GetProperty("duv").GetDouble() > 0.006);
        var c = rig.Engine.Store.Project.FindOutput("op00000002")!.Measured!.Color!;
        Assert.True(c.GainR <= 1 && c.GainG <= 1 && c.GainB <= 1 && Math.Max(c.GainR, Math.Max(c.GainG, c.GainB)) > 0.999);
        Assert.True(Math.Max(c.GainR, Math.Max(c.GainG, c.GainB)) - Math.Min(c.GainR, Math.Min(c.GainG, c.GainB)) > 0.01, "the member's white is moved");
        Assert.True(Stage(rig).GetProperty("stacks")[0].GetProperty("matched").GetBoolean());
        Assert.Equal(id, rig.Engine.Store.Project.FindOutput("op00000002")!.Measured!.StackId);

        // Observed: each member shown through the gains the verb wrote, its white where both light lands within Δu′v′ 0.003 of the
        // other's.
        var gains = new[] { "op00000001", "op00000002" }.Select(o => rig.Engine.Store.Project.FindOutput(o)!.Measured!.Color!).ToArray();
        var before = StackKit.Duv(StackKit.WhiteThrough(sim, 0, 1, 1, 1), StackKit.WhiteThrough(sim, 1, 1, 1, 1));
        var after = StackKit.Duv(StackKit.WhiteThrough(sim, 0, gains[0].GainR, gains[0].GainG, gains[0].GainB),
            StackKit.WhiteThrough(sim, 1, gains[1].GainR, gains[1].GainG, gains[1].GainB));
        log.WriteLine($"Δu′v′ {before:0.0000} before, {after:0.0000} after");
        Assert.True(before > 0.006, $"before {before}");
        Assert.True(after <= 0.003, $"after {after}");
    }

    [Fact]
    public async Task UnderAnOperatorsCornerPinTheMemberStillLandsOnTheMaster()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        var pin = new OutputWarp(true, CornerPin.None with { TopLeft = new Core.Numerics.Vec2d(0.04, 0.03), BottomRight = new Core.Numerics.Vec2d(-0.03, -0.02) }, null);
        var project = StackKit.Project(sim);
        project = project with { Outputs = project.Outputs.Update("op00000002", o => o with { Warp = pin }) };
        await using var rig = new ShowRig(project, s => s with { Scan = _scans });
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        await rig.Ok($"STACK ALIGN {id} FROM {StackKit.Scan}");
        var o2 = rig.Engine.Store.Project.FindOutput("op00000002")!;
        var drawn = new Core.Mapping.WarpField(o2.Warp, null, o2.Measured!.Lattice);
        var pinned = StackKit.MisregistrationP95(sim, Core.Mapping.WarpField.Identity, new Core.Mapping.WarpField(o2.Warp));
        var after = StackKit.MisregistrationP95(sim, Core.Mapping.WarpField.Identity, drawn);
        log.WriteLine($"misregistration p95 {pinned:0.000} px with the pin alone, {after:0.000} px with the lattice under it");
        Assert.True(pinned > 2, $"the pin moves the member {pinned}");
        Assert.True(after <= 0.2, $"after {after}");
    }

    /// <summary>The master's picture is what its output draws: under a corner pin on the master (and another on the member), the
    /// member lands where the master's pinned picture is, not where its raw raster is.</summary>
    [Fact]
    public async Task UnderACornerPinOnTheMasterTheMemberLandsOnTheMastersPinnedPicture()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        var masterPin = new OutputWarp(true, CornerPin.None with { TopLeft = new Core.Numerics.Vec2d(0.03, 0.02), BottomLeft = new Core.Numerics.Vec2d(0.02, -0.03) }, null);
        var memberPin = new OutputWarp(true, CornerPin.None with { TopRight = new Core.Numerics.Vec2d(-0.02, 0.01) }, null);
        var project = StackKit.Project(sim);
        project = project with
        {
            Outputs = project.Outputs.Update("op00000001", o => o with { Warp = masterPin }).Update("op00000002", o => o with { Warp = memberPin }),
        };
        await using var rig = new ShowRig(project, s => s with { Scan = _scans });
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        await rig.Ok($"STACK ALIGN {id} FROM {StackKit.Scan}");
        var p = rig.Engine.Store.Project;
        var o1 = p.FindOutput("op00000001")!;
        var o2 = p.FindOutput("op00000002")!;
        // The master is the target: its own pin stays and it takes no lattice.
        Assert.Null(o1.Measured);
        var master = new Core.Mapping.WarpField(o1.Warp);
        var unfitted = StackKit.MisregistrationP95(sim, master, new Core.Mapping.WarpField(o2.Warp));
        var after = StackKit.MisregistrationP95(sim, master, new Core.Mapping.WarpField(o2.Warp, null, o2.Measured!.Lattice));
        log.WriteLine($"misregistration p95 against the master's pinned picture {unfitted:0.000} px with the member's pin alone, {after:0.000} px with the lattice under it");
        Assert.True(unfitted > 2, $"the pins move the pictures apart {unfitted}");
        Assert.True(after <= 0.2, $"after {after}");
    }

    /// <summary>A band of the member's decodes that comes back mirrored (glass, a reflection: consistent, so the fit's reweighting
    /// cannot set it aside) would fold the member's picture over itself: the alignment is refused with where, and nothing lands.</summary>
    [Fact]
    public async Task AFitThatWouldFoldTheMembersPictureIsRefusedAndNothingLands()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim, alter: (j, map) =>
        {
            if (j != 1)
                return;
            var w = map.ProjectorWidth;
            for (var k = 0; k < map.ProjectorX.Length; k++)
                if ((map.Flags[k] & Core.Scan.DecodeStatus.Valid) != 0 && map.ProjectorX[k] > 0.42f * w && map.ProjectorX[k] < 0.58f * w)
                    map.ProjectorX[k] = w - map.ProjectorX[k];
        }));
        await using var rig = Rig(sim);
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        var revision = rig.Engine.Store.Current.Revision;
        var refused = await rig.Refused($"STACK ALIGN {id} FROM {StackKit.Scan}");
        log.WriteLine(refused);
        Assert.Contains("P2: the fit would fold its picture near (", refused, StringComparison.Ordinal);
        Assert.Contains("nothing was applied", refused, StringComparison.Ordinal);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Null(rig.Engine.Store.Project.FindOutput("op00000002")!.Measured);
        Assert.False(Stage(rig).GetProperty("stacks")[0].GetProperty("aligned").GetBoolean());
    }

    /// <summary>The fit runs off the lane: a member's warp moved before its result lands (the lattice was fitted through the warp it
    /// had) refuses the result, and the member keeps what it had.</summary>
    [Fact]
    public async Task AMemberChangedWhileTheStackIsFittedIsNotOverwritten()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        await using var rig = Rig(sim);
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        // The lane is held: the alignment reads the stack first, the warp moves next, then the fit's result comes back to the lane.
        var gate = new TaskCompletionSource();
        var held = rig.Engine.Lane.RunAsync(() => gate.Task.Wait());
        var align = rig.Run($"STACK ALIGN {id} FROM {StackKit.Scan}");
        var moved = rig.Run("WARP NODE op00000002 1 1 0.01 0");
        gate.SetResult();
        await held;
        Assert.True((await moved).Ok);
        var r = await align;
        Assert.False(r.Ok, r.Payload);
        Assert.Contains("P2 was changed while S1 was being fitted — nothing was applied", r.Payload, StringComparison.Ordinal);
        var o2 = rig.Engine.Store.Project.FindOutput("op00000002")!;
        Assert.Null(o2.Measured);
        Assert.NotNull(o2.Warp.Lattice);
        Assert.False(Stage(rig).GetProperty("stacks")[0].GetProperty("aligned").GetBoolean());
    }

    [Fact]
    public async Task AStackOfOneMadeByAGenericAddIsNeverCountedAligned()
    {
        await using var rig = Rig(StackKit.Sim());
        await rig.Ok($$"""ADD stack {"name":"Lone","projectorIds":["{{StackKit.P3}}"],"masterId":"{{StackKit.P3}}"}""");
        var state = Stage(rig).GetProperty("stacks")[0];
        Assert.False(state.GetProperty("aligned").GetBoolean());
        Assert.Equal(Core.Bus.Light.Amber, ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id.StartsWith("stack:", StringComparison.Ordinal)).Light);
    }

    [Fact]
    public async Task ALockAStoppingNodeAndAScanWithoutAMemberRefuseWithWords()
    {
        var sim = StackKit.Sim();
        var outcome = await StackKit.OutcomeAsync(sim, colour: false);
        _scans.Remember(outcome with { Id = "sc00000002", Views = [outcome.Views[0]] });
        _scans.Remember(outcome);
        var project = StackKit.Project(sim);
        project = project.WithStage(st => st with { Scans = st.Scans.Add(st.Scans[0] with { Id = "sc00000002", Name = "Half scan" }) });
        await using var rig = new ShowRig(project, s => s with { Scan = _scans });
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        var revision = rig.Engine.Store.Current.Revision;

        Assert.Contains("did not scan P2", await rig.Refused($"STACK ALIGN {id} FROM sc00000002"), StringComparison.Ordinal);
        Assert.Contains("no scan nowhere", await rig.Refused($"STACK ALIGN {id} FROM nowhere"), StringComparison.Ordinal);
        Assert.Contains("no colour white frame", await rig.Refused($"STACK MATCH {id} FROM {StackKit.Scan}"), StringComparison.Ordinal);
        Assert.Contains("GRID is", await rig.Refused($"STACK ALIGN {id} FROM {StackKit.Scan} GRID 99x2"), StringComparison.Ordinal);

        await rig.Ok("LOCK op00000002 aligned for the night");
        var locked = await rig.Refused($"STACK ALIGN {id} FROM {StackKit.Scan}");
        Assert.Contains("aligned for the night", locked, StringComparison.Ordinal);
        Assert.Equal(revision + 1, rig.Engine.Store.Current.Revision);
        Assert.Null(rig.Engine.Store.Project.FindOutput("op00000002")!.Measured);

        rig.Engine.BeginStop();
        Assert.Contains("stopping", await rig.Refused($"STACK ALIGN {id} FROM {StackKit.Scan}"), StringComparison.Ordinal);
    }

    /// <summary>A scan of many views (multiscan): its smart scans come each from where it decoded most — here P1's from a second camera
    /// place that saw only P1 — so STACK ALIGN takes its pairs from the one view the last solve used that saw every member; a scan whose
    /// only such view was dropped is refused with the line that adds one.</summary>
    [Fact]
    public async Task AScanOfManyViewsAlignsFromTheOneViewThatSawEveryMember()
    {
        var sim = StackKit.Sim();
        var elsewhere = StackKit.Moved(sim, new Core.Numerics.Vec3d(0.3, 0.05, 0));
        var here = await StackKit.MapsAsync(sim);
        var there = await StackKit.MapsAsync(elsewhere);
        var outcome = await StackKit.OutcomeAsync(sim);
        outcome = outcome with
        {
            Views = [(StackKit.P1, Core.Scan.ProjectorView.Build(there[0])), (StackKit.P2, Core.Scan.ProjectorView.Build(here[1]))],
            Captured =
            [
                StackKit.View("v1", elsewhere, new Dictionary<string, Core.Scan.CorrespondenceMap> { [StackKit.P1] = there[0] }),
                StackKit.View("v2", sim, new Dictionary<string, Core.Scan.CorrespondenceMap> { [StackKit.P1] = here[0], [StackKit.P2] = here[1] }),
            ],
        };
        _scans.Remember(outcome);
        _scans.Remember(outcome with { Id = "sc00000002", Captured = [outcome.Captured[0], outcome.Captured[1] with { State = "dropped" }] });
        var project = StackKit.Project(sim);
        project = project.WithStage(st => st with { Scans = st.Scans.Add(st.Scans[0] with { Id = "sc00000002", Name = "Dropped scan" }) });
        await using var rig = new ShowRig(project, s => s with { Scan = _scans });
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;

        var reply = Json(await rig.Ok($"STACK ALIGN {id} FROM {StackKit.Scan}"));
        log.WriteLine(reply.GetProperty("words").GetString());
        Assert.Equal("v2", reply.GetProperty("view").GetString());
        var lattice = rig.Engine.Store.Project.FindOutput("op00000002")!.Measured!.Lattice;
        var after = StackKit.MisregistrationP95(sim, null, lattice);
        log.WriteLine($"misregistration p95 {after:0.000} px");
        Assert.True(after <= 0.2, $"after {after}");

        var refused = await rig.Refused($"STACK ALIGN {id} FROM sc00000002");
        Assert.Contains("no view of Dropped scan saw every member of S1", refused, StringComparison.Ordinal);
        Assert.Contains($"SCAN START sim {StackKit.P1} {StackKit.P2} INTO sc00000002", refused, StringComparison.Ordinal);
    }

    /// <summary>The pairs are camera pixels against camera pixels: a move of the whole site between the scan and STACK ALIGN fits the
    /// same lattice, and the stack stays aligned through another move after it.</summary>
    [Fact]
    public async Task ASiteMoveBetweenTheScanAndTheAlignmentFitsTheSameLattice()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        await using var still = Rig(sim);
        var a = Json(await still.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        await still.Ok($"STACK ALIGN {a} FROM {StackKit.Scan}");
        var unmoved = still.Engine.Store.Project.FindOutput("op00000002")!.Measured!.Lattice!;

        await using var rig = Rig(sim);
        await rig.Ok("SITE ORIGIN 512340 5401230 110 CRS EPSG:25832");
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        Assert.Contains("the stage moved", Json(await rig.Ok("SITE ORIGIN 512352.5 5401218 111 ROTATE 23")).GetProperty("words").GetString(), StringComparison.Ordinal);
        await rig.Ok($"STACK ALIGN {id} FROM {StackKit.Scan}");
        var moved = rig.Engine.Store.Project.FindOutput("op00000002")!.Measured!.Lattice!;
        Assert.Equal(unmoved.Offsets.ToArray(), moved.Offsets.ToArray());

        await rig.Ok("SITE ORIGIN 512300 5401200 109 ROTATE 140");
        Assert.True(Stage(rig).GetProperty("stacks")[0].GetProperty("aligned").GetBoolean());
        Assert.Equal(Core.Bus.Light.Green, ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id == "stack:" + id).Light);
    }

    /// <summary>The lattice is the measured light less the modelled pictures: once a member moves against its master (solved again, or
    /// set by hand) STATE, the context, preflight and the guide say the stack is no longer aligned, with the line that fits it again; a
    /// result recorded as it stands moves nothing, and nor does the master moving with it (the stack moved as one).</summary>
    [Fact]
    public async Task AMemberThatMovesSinceItWasAlignedUnalignsItsStackUntilItIsFittedAgain()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        await using var rig = Rig(sim);
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        await rig.Ok($"STACK ALIGN {id} FROM {StackKit.Scan}");
        Assert.True(Stage(rig).GetProperty("stacks")[0].GetProperty("aligned").GetBoolean());

        // A result recorded as P2 stands moves nothing.
        await rig.Ok($"CALIBRATION SAVE {StackKit.P2}");
        Assert.True(Stage(rig).GetProperty("stacks")[0].GetProperty("aligned").GetBoolean());

        // P2 moved 4 cm against the master.
        var pose = rig.Engine.Store.Project.FindProjector(StackKit.P2)!.Pose;
        await rig.Ok($"SET {StackKit.P2} pose {ProjectJson.SerializeEntity(pose with { Position = pose.Position + new Core.Numerics.Vec3d(0.04, 0, 0) })}");
        Assert.False(Stage(rig).GetProperty("stacks")[0].GetProperty("aligned").GetBoolean());
        var item = ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id == "stack:" + id);
        Assert.Equal(Core.Bus.Light.Amber, item.Light);
        Assert.Contains("P2 or P1 moved since S1 was aligned", item.State, StringComparison.Ordinal);
        var row = Assert.Single(await new Site.SitePreflight().RunAsync(new PreflightContext(rig.Engine, Core.Preflight.PreflightDepth.Quick, false),
            CancellationToken.None), r => r.Id == "stack:" + id);
        Assert.Equal((Core.Bus.Light.Amber, $"STACK ALIGN {id} FROM {StackKit.Scan}"), (row.Light, row.Fix));
        var step = Core.Site.SiteGuide.Steps(rig.Engine.Store.Project, rig.Engine.GuideFactsFor(rig.Engine.Store.Project)).Single(s => s.Step == "stacks");
        Assert.Equal(("waiting", $"STACK ALIGN {id} FROM {StackKit.Scan}"), (step.State, step.Line));

        // The master moves with it: the member stands against its master as its lattice was fitted.
        var master = rig.Engine.Store.Project.FindProjector(StackKit.P1)!.Pose;
        await rig.Ok($"SET {StackKit.P1} pose {ProjectJson.SerializeEntity(master with { Position = master.Position + new Core.Numerics.Vec3d(0.04, 0, 0) })}");
        Assert.True(Stage(rig).GetProperty("stacks")[0].GetProperty("aligned").GetBoolean());
    }

    /// <summary>An output an alignment draws through its measured warp grid shows the canvas it was registered to: a stack lattice on it
    /// would register it twice, so STACK ALIGN refuses it with the line that takes the warp off. A lock on the stack names it.</summary>
    [Fact]
    public async Task AMemberAnAlignmentDrawsThroughItsMeasuredWarpIsRefusedAndALockedStackIsNamed()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        var project = StackKit.Project(sim);
        var aligned = new MeasuredCorrection(new MeasuredWarp(33, 19, "align/al00000001/warp-op00000002.pfm", "0123456789abcdef", 0.3), null, null, null,
            0.3, "", "", "al00000001", 1_760_000_000_000);
        project = project with { Outputs = project.Outputs.Update("op00000002", o => o with { Measured = aligned }) };
        await using var rig = new ShowRig(project, s => s with { Scan = _scans });
        var id = Json(await rig.Ok("STACK NEW S1 P1 P2")).GetProperty("id").GetString()!;
        var refused = await rig.Refused($"STACK ALIGN {id} FROM {StackKit.Scan}");
        Assert.Contains("measured warp", refused, StringComparison.Ordinal);
        Assert.Contains("ALIGN CLEAR op00000002", refused, StringComparison.Ordinal);
        Assert.Same(aligned, rig.Engine.Store.Project.FindOutput("op00000002")!.Measured);

        await rig.Ok($"LOCK {id} the stack is set");
        Assert.Contains("S1 (stack) is locked", await rig.Refused($"STACK FAILOVER {id} MATCH"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromABlindDraftTheStackAndItsAlignmentAreStaged()
    {
        var sim = StackKit.Sim();
        _scans.Remember(await StackKit.OutcomeAsync(sim));
        await using var rig = Rig(sim);
        var revision = rig.Engine.Store.Current.Revision;
        await rig.Ok("BLIND ON");
        var made = Json(await rig.Ok("STACK NEW S1 P1 P2"));
        Assert.True(made.GetProperty("staged").GetBoolean());
        var id = made.GetProperty("id").GetString()!;
        var aligned = Json(await rig.Ok($"STACK ALIGN {id} FROM {StackKit.Scan}"));
        Assert.True(aligned.GetProperty("staged").GetBoolean());
        Assert.StartsWith("staged in the blind draft:", aligned.GetProperty("words").GetString(), StringComparison.Ordinal);

        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Empty(rig.Engine.Store.Project.Stage.Stacks);
        Assert.Equal(0, Stage(rig).GetProperty("stacks").GetArrayLength());
        var draft = rig.Engine.Snapshot.Draft!.Project;
        Assert.Single(draft.Stage.Stacks);
        Assert.NotNull(draft.FindOutput("op00000002")!.Measured!.Lattice);
    }
}
