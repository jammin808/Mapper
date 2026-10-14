using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>Every document edit handler reads and writes through <see cref="EngineCore.Edits"/>: with a session's edits sent to a draft
/// (what the show's gate does for a blind session), every edit verb — on the lane or answered off it — changes that draft and the
/// live project's revision never moves; another session's edits stay live.</summary>
public sealed class EditsRedirectTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private static WireLine Line(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var parsed, out var error), error?.Reason);
        return parsed;
    }

    private static async Task<string> Live(EngineCore engine, string text)
    {
        var r = await engine.HandleAsync(Line(text), Desk, CancellationToken.None);
        Assert.True(r.Ok, $"{text} → {r.Payload}");
        return r.Payload ?? "";
    }

    /// <summary>A projector with ten pins that solve (on a box and a wall, from a known lens and pose).</summary>
    private static async Task<string> PinnedProjector(EngineCore engine)
    {
        var truthK = Intrinsics.FromThrowRatio(1920, 1080, 1.3, 0, 0.4);
        var truthPose = Pose.LookAt(new PlayMap.Core.Numerics.Vec3d(0.4, 1.6, -6), new PlayMap.Core.Numerics.Vec3d(0, 1.2, 0), PlayMap.Core.Numerics.Vec3d.UnitY);
        var pj = await Live(engine, "ADD projector {\"name\":\"Left\"}");
        var world = new[]
        {
            (0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.0), (0.0, 1.0, 0.0), (-1.5, 0.0, 1.0), (-1.5, 2.5, 1.0),
            (1.8, 2.4, 1.0), (1.8, 0.2, 1.0), (0.5, 0.5, -0.5), (0.2, 1.8, 0.6),
        };
        foreach (var (x, y, z) in world)
        {
            var px = PlayMap.Core.Geometry.CameraModel.Project(new PlayMap.Core.Numerics.Vec3d(x, y, z), truthK, LensDistortion.None, truthPose)!.Value;
            await Live(engine, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"ADD pin {pj} {{\"world\":{{\"x\":{x},\"y\":{y},\"z\":{z}}},\"pixel\":{{\"x\":{px.X},\"y\":{px.Y}}}}}"));
        }
        return pj;
    }

    [Fact]
    public async Task EveryEditVerbGoesToTheSessionsDraftAndTheLiveRevisionNeverMoves()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        // What the lines below refer to, made live first; the draft starts as the live project.
        var list = await Live(engine, "PLAYLIST NEW Show");
        var spare = await Live(engine, "PLAYLIST NEW Spare");
        var gen = await Live(engine, "GENERATOR NEW grid Grid");
        var card = await Live(engine, "GENERATOR NEW testcard Card");
        var unused = await Live(engine, "GENERATOR NEW checker Unused");
        var item = await Live(engine, $"PLAYLIST ADD {list} generator:{gen} Opening");
        await Live(engine, $"PLAYLIST ADD {list} generator:{card} Main");
        var surface = await Live(engine, "ADD surface {\"name\":\"Wall\"}");
        var clip = await Live(engine, "ADD media {\"name\":\"Clip\",\"kind\":\"video\",\"location\":\"/nowhere/clip.mov\"}");
        var pj = await PinnedProjector(engine);
        var output = engine.Store.Project.Outputs[0].Id;
        var live = engine.Store.Current.Revision;
        var draft = new ProjectStore(engine.Store.Project);
        engine.DraftForTests = s => s.SessionId == Desk.SessionId ? draft : null;

        string[] lines =
        [
            "GENERATOR NEW julia Fractal",
            $"GENERATOR SET {gen} cell 77",
            "PLAYLIST NEW Draft",
            $"PLAYLIST ADD {list} generator:{card} Encore",
            $"PLAYLIST MOVE {list} {item} 1",
            $"PLAYLIST REMOVE {list} {item}",
            $"PLAYLIST LOOP {list} ON",
            $"PLAYLIST SYNC {list} wall",
            $"PLAYLIST DELETE {spare}",
            $"OUTPUT ENABLE {output} OFF",
            $"OUTPUT SOURCE {output} generator:{gen}",
            $"WARP NODE {output} 1 1 0.05 0",
            $"WARP RESET {output}",
            $"SURFACE SOURCE {surface} generator:{card}",
            $"SURFACE SHOW {surface} OFF",
            $"PROJECTOR SOLVE {pj}",
            $"MEDIA REMOVE {clip}",
            $"SET {output} name \"Draft name\"",
            "ADD canvas {\"name\":\"Draft canvas\"}",
            $"MOVE {unused} 0",
            $"REMOVE {unused}",
            "UNDO",
            "REDO",
            // Answered off the lane (the operations path): the lane part reads and writes the same draft.
            "DEVICE ADD Left pjlink 10.0.0.31 PROJECTOR Left",
            "DEVICE REMOVE Left",
        ];
        foreach (var text in lines)
        {
            var before = draft.Current.Revision;
            var reply = await engine.HandleAsync(Line(text), Desk, CancellationToken.None);
            Assert.True(reply.Ok, $"{text} → {reply.Payload}");
            Assert.NotEqual(before, draft.Current.Revision);
            Assert.Equal(live, engine.Store.Current.Revision);
        }

        Assert.Equal("Draft name", draft.Project.FindOutput(output)!.Name);
        Assert.Equal("Output 1", engine.Store.Project.FindOutput(output)!.Name);
        Assert.Null(draft.Project.FindPlaylist(spare));
        Assert.NotNull(engine.Store.Project.FindPlaylist(spare));
        Assert.Null(draft.Project.FindGenerator(unused));
        Assert.NotNull(engine.Store.Project.FindGenerator(unused));
        Assert.Null(draft.Project.FindMedia(clip));
        Assert.NotNull(engine.Store.Project.FindMedia(clip));
        Assert.True(draft.Project.FindProjector(pj)!.SolveResidualPx > 0);
        Assert.Equal(0, engine.Store.Project.FindProjector(pj)!.SolveResidualPx);
        Assert.Empty(draft.Project.Stage.Devices);
        Assert.Empty(engine.Store.Project.Stage.Devices);

        // An off-lane verb's edit goes to the same draft; another session's goes live.
        var drafted = await engine.EditAsync(Desk, "renamed in the draft", p => p with { Name = "Drafted" });
        Assert.True(drafted.Changed, drafted.Effect);
        Assert.Equal("Drafted", draft.Project.Name);
        Assert.Equal(live, engine.Store.Current.Revision);
        Assert.True((await engine.EditAsync(SessionInfo.Desk("other"), "renamed live", p => p with { Name = "Live" })).Changed);
        Assert.Equal("Live", engine.Store.Project.Name);
        Assert.Equal("Drafted", draft.Project.Name);
    }

    [Fact]
    public async Task ADeviceOnlyTheDraftHoldsIsFoundThereAndNeverLive()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var draft = new ProjectStore(engine.Store.Project);
        engine.DraftForTests = s => s.SessionId == Desk.SessionId ? draft : null;
        Assert.True((await engine.HandleAsync(Line("ADD projector {\"name\":\"Only in the draft\"}"), Desk, CancellationToken.None)).Ok);

        // The operations path checks what it names in the draft it writes: the draft's projector is found there, never live.
        var added = await engine.HandleAsync(Line("DEVICE ADD Draft pjlink 10.0.0.32 PROJECTOR \"Only in the draft\""), Desk, CancellationToken.None);
        Assert.True(added.Ok, added.Payload);
        Assert.Single(draft.Project.Stage.Devices);
        var live = await engine.HandleAsync(Line("DEVICE REMOVE Draft"), SessionInfo.Desk("other"), CancellationToken.None);
        Assert.False(live.Ok);
        Assert.Equal("no projector link Draft", live.Payload);
        Assert.True((await engine.HandleAsync(Line("DEVICE REMOVE Draft"), Desk, CancellationToken.None)).Ok);
        Assert.Empty(draft.Project.Stage.Devices);
    }

    [Fact]
    public async Task AnOffLaneEditGoesToTheLiveStoreWhenTheSessionHasNoDraft()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var before = engine.Store.Current.Revision;

        var r = await engine.EditAsync(Desk, "renamed", p => p with { Name = "Renamed" });

        Assert.True(r.Changed, r.Effect);
        Assert.Equal(before + 1, engine.Store.Current.Revision);
        Assert.Equal("Renamed", engine.Store.Project.Name);
        var again = await engine.EditAsync(Desk, "renamed", p => p with { Name = "Renamed" });
        Assert.False(again.Changed);
        Assert.Contains("nothing to change", again.Effect, StringComparison.Ordinal);
    }
}
