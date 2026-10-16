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
        var before = StackKit.MisregistrationP95(sim, null, null);

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
        Assert.Null(o1.Measured?.Lattice);
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
