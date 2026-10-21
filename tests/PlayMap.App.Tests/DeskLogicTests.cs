using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.Scene;
using PlayMap.App.State;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Tests;

/// <summary>The desk's logic without a window: STATE reads leniently in WIRE.md's shape, lines are written
/// in the invariant culture, a drag's edits coalesce, the engine supervisor backs off, the pen tool's
/// gestures make the path they should, and the generic edits refuse what they cannot do with the reason.</summary>
public class DeskLogicTests
{
    private const string WireSample = """
        {"v":1,"seq":412,"id":"a1b2c3d4","t":"playmap","name":"Stage left","version":"0.2.0","at":1760101234567,
         "light":"green","headline":"Show · Intro 0:42 left · 3 outputs green · synced to FOH (0.2 ms)",
         "air":{"blackout":false,"frozen":false,"pattern":null,"identify":false},
         "video":{"file":"intro.mp4","role":"playlist","tag":"Show","position":62,"length":210,"remaining":148,
           "positionText":"1:02","lengthText":"3:30","remainingText":"2:28","text":"Show intro.mp4 · 1:02 / 3:30 · 2:28 left",
           "chip":"Show 2:28","playing":true,"ended":false,"loops":false,"out":false,"call":"",
           "key":"md1a2b3c4d","positionMs":62000,"lengthMs":210000,"remainingMs":148000,"paused":false,"rate":1,
           "seekable":true,"seq":7},
         "playlists":[{"id":"pl1","name":"Show","focused":true,"state":"playing","index":0,"item":{"id":"it1","label":"Intro",
           "source":"media:md1"},"positionMs":62000,"lengthMs":210000,"remainingMs":148000,"rate":1,"loop":false,
           "syncGroup":null,"leader":null,"seq":7,"next":{"id":"it2","label":"Main"}}],
         "outputs":[{"id":"op_1","name":"Projector 1","mode":"projector","display":"DISPLAY2","width":1920,"height":1080,
           "refreshHz":60,"open":true,"fps":60.0,"dropped":0,"frameMsP95":4.1,"pattern":null,"light":"green"}],
         "media":{"items":12,"missing":0,"ready":true,"transfers":[]},
         "stage":{"surfaces":6,"meshes":2,"projectors":[{"id":"pj1","name":"Left","solved":true,"residualPx":0.6}],"scans":1},
         "scan":null,
         "sync":{"role":"follower","leader":"9f8e7d6c","offsetMs":0.21,"rttMs":0.35,"driftPpm":3.2,"groups":["show"]},
         "links":{"patterns":{"state":"live","host":"10.0.0.5:9697","paired":true,"clip":"VT 2:28"},
           "stagewatch":{"state":"live","host":"10.0.0.6:9924","timer":{"state":"running","text":"12:30","seconds":750,
           "phase":"warning","color":"#F59E0B","title":"Keynote","message":""}}},
         "peers":[{"t":"patterns","id":"x","name":"FOH","light":"green","live":true}],
         "machine":{"renderer":"OpenGL 4.6","cpuPct":12,"memMb":840,"gcPauseMsMax":0.4},
         "project":{"id":"pr1","name":"Show","revision":118,"path":"D:/Shows/show.playmap","dirty":false},
         "future":{"anything":[1,2,3]}}
        """;

    [Fact]
    public void StateReadsTheWireDocumentsShapeAndIgnoresKeysItDoesNotKnow()
    {
        var s = StateSnapshot.Parse(WireSample);
        Assert.Equal(412, s.Seq);
        Assert.Equal("Show 2:28", s.Video!.Chip);
        Assert.Equal(62000, s.Video.PositionMs);
        var pl = Assert.Single(s.Playlists);
        Assert.True(pl.IsPlaying);
        Assert.Equal("Main", pl.Next!.Label);
        Assert.Same(pl, s.FocusedPlaylist);
        Assert.Equal("projector", s.Outputs[0].Mode);
        Assert.Equal(0.6, s.Stage.Projectors[0].ResidualPx);
        Assert.Null(s.Scan);
        Assert.Equal(0.21, s.Sync.OffsetMs);
        Assert.Equal("live", s.Patterns.State);
        Assert.Equal("12:30", s.Stagewatch.Timer!.Text);
        Assert.Equal("FOH", s.Peers[0].Name);
        Assert.False(s.Project.Dirty);
        Assert.Equal(AiState.Off, s.Ai);
        Assert.Same(StateSnapshot.Empty, StateSnapshot.Parse(""));
        var sparse = StateSnapshot.Parse("{\"seq\":3,\"air\":{\"blackout\":true}}");
        Assert.True(sparse.Air.Blackout);
        Assert.Empty(sparse.Playlists);
        Assert.Null(sparse.FocusedPlaylist);
        // The engine writes the pattern as its kind and the outputs it covers.
        var patterned = StateSnapshot.Parse("{\"seq\":4,\"air\":{\"pattern\":{\"kind\":\"pattern.grid\",\"outputs\":[]}}}");
        Assert.Equal("pattern.grid", patterned.Air.Pattern);
    }

    [Fact]
    public void LinesAreWrittenInTheInvariantCultureWhateverTheDesksOwn()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.Equal("-0.5", VerbText.Num(-0.5));
            Assert.Equal("SET sf1 opacity 0.25", VerbText.SetNum("sf1", "opacity", 0.25));
            Assert.Equal("SET sf1 name \"Lobby wall\"", VerbText.SetText("sf1", "name", "Lobby wall"));
            Assert.Equal("SET it1 end \"loop\"", VerbText.SetEnum("it1", "end", EndAction.Loop));
            Assert.Equal("{\"x\":0.01,\"y\":-0.02}", VerbText.Json(new Vec2d(0.01, -0.02)));
            Assert.Equal("\"two words\"", VerbText.Word("two words"));
            Assert.Equal("1:02:03", VerbText.Clock(3_723_000));
            Assert.Equal("0:03", VerbText.Clock(2_001, countdown: true));
            Assert.Equal(62_500_000_000, VerbText.ParseSeconds("1:02.5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task ADragsEditsCoalesceToTheLatestLinePerKey()
    {
        var sent = new List<string>();
        using var throttle = new ThrottledSender((line, _) =>
        {
            sent.Add(line);
            return Task.CompletedTask;
        }, TimeSpan.FromHours(1));
        throttle.Post("a", "SET op_1 plan 1");
        throttle.Post("b", "SET op_2 plan 1");
        throttle.Post("a", "SET op_1 plan 2");
        throttle.Post("a", "SET op_1 plan 3");
        Assert.Equal(2, throttle.Pending);
        await throttle.CommitAsync(Ct);
        Assert.Equal(["SET op_1 plan 3", "SET op_2 plan 1"], sent);
        Assert.Equal(0, throttle.Pending);
    }

    [Fact]
    public async Task ACommitDropsTheLineTheIntervalSentUnderItsKeyAndARefusedOneGoesAgain()
    {
        var time = new PlayMap.App.Tests.Quad.ManualTime();
        var sent = new List<string>();
        var refuse = false;
        using var throttle = new ThrottledSender((line, _) =>
        {
            sent.Add(line);
            return Task.FromResult(!refuse);
        }, TimeSpan.FromMilliseconds(16), time);

        // The interval sends A; the release posts A again: the node has it, so the commit sends nothing.
        throttle.Post("corner", "SURFACE CORNER sf TR TO 10 10 RASTER");
        time.Advance(TimeSpan.FromMilliseconds(16));
        await throttle.Pump;
        throttle.Post("corner", "SURFACE CORNER sf TR TO 10 10 RASTER");
        await throttle.CommitAsync(Ct);
        Assert.Equal(["SURFACE CORNER sf TR TO 10 10 RASTER"], sent);

        // The commit forgot what the interval sent: the next gesture's same line goes.
        throttle.Post("corner", "SURFACE CORNER sf TR TO 10 10 RASTER");
        await throttle.CommitAsync(Ct);
        Assert.Equal(2, sent.Count);

        // A line the node refused is not the node's: the commit sends it again.
        refuse = true;
        throttle.Post("corner", "SURFACE CORNER sf TR TO 20 20 RASTER");
        time.Advance(TimeSpan.FromMilliseconds(16));
        await throttle.Pump;
        refuse = false;
        throttle.Post("corner", "SURFACE CORNER sf TR TO 20 20 RASTER");
        await throttle.CommitAsync(Ct);
        Assert.Equal(["SURFACE CORNER sf TR TO 20 20 RASTER", "SURFACE CORNER sf TR TO 20 20 RASTER"], sent[2..]);

        // A different line under the key, and the same line under another key, both go.
        throttle.Post("corner", "SURFACE CORNER sf TR TO 30 30 RASTER");
        time.Advance(TimeSpan.FromMilliseconds(16));
        await throttle.Pump;
        throttle.Post("corner", "SURFACE CORNER sf TR TO 31 30 RASTER");
        throttle.Post("other", "SURFACE CORNER sf TR TO 30 30 RASTER");
        await throttle.CommitAsync(Ct);
        Assert.Equal(["SURFACE CORNER sf TR TO 30 30 RASTER", "SURFACE CORNER sf TR TO 31 30 RASTER", "SURFACE CORNER sf TR TO 30 30 RASTER"], sent[4..]);
    }

    [Fact]
    public void TheRestartBackoffDoublesToItsCeilingAndAHealthyRunStartsItAgain()
    {
        var b = new RestartBackoff();
        Assert.Equal(TimeSpan.FromMilliseconds(500), b.Next(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(1), b.Next(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(2), b.Next(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(4), b.Next(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(8), b.Next(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(10), b.Next(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(10), b.Next(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMilliseconds(500), b.Next(TimeSpan.FromMinutes(5)));
    }

    private sealed class FakeProcess(int id, int exitCode) : IEngineProcess
    {
        public int Id { get; } = id;

        public bool Disposed { get; private set; }

        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(exitCode);

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task TheSupervisorRestartsACrashedEngineAndLeavesOneThatWasAskedToStop()
    {
        var exits = new Queue<int>([3, 139, 0]);
        var made = new List<FakeProcess>();
        var notes = new List<string>();
        await using var supervisor = new EngineSupervisor(() =>
        {
            var p = new FakeProcess(100 + made.Count, exits.Dequeue());
            made.Add(p);
            return p;
        }, backoff: new RestartBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4)));
        supervisor.Note += notes.Add;
        Assert.True(supervisor.Start());
        await supervisor.Watching.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(3, supervisor.Starts);
        Assert.All(made, p => Assert.True(p.Disposed));
        Assert.Contains(notes, n => n.Contains("code 3", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("code 139", StringComparison.Ordinal));
        Assert.Contains("asked to", notes[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndpointReadsHostAndPortAndRefusesWhatIsNotOne()
    {
        Assert.True(EngineEndpoint.TryParse("10.0.0.7", "K7QM-3XWD-P9RA", out var a));
        Assert.Equal(9941, a.Port);
        Assert.Equal("K7QM-3XWD-P9RA", a.Token);
        Assert.False(a.Local);
        Assert.True(EngineEndpoint.TryParse("localhost:9000", null, out var b));
        Assert.True(b.Local);
        Assert.Equal("localhost:9000", b.ToString());
        Assert.False(EngineEndpoint.TryParse("host:99999", null, out _));
        Assert.False(EngineEndpoint.TryParse("two words", null, out _));
        Assert.False(EngineEndpoint.TryParse("", null, out _));
    }

    [Fact]
    public void ThePenToolsGesturesMakeCornersSmoothNodesAndAClosedPath()
    {
        var pen = new PenTool();
        BezierPath? closed = null;
        pen.Closed += p => closed = p;
        pen.Press(new Vec2d(0.1, 0.1), alt: false, hit: 0.02);
        pen.Release();
        pen.Press(new Vec2d(0.5, 0.1), alt: false, hit: 0.02);
        pen.Drag(new Vec2d(0.6, 0.15), 0.02);
        pen.Release();
        pen.Press(new Vec2d(0.5, 0.5), alt: false, hit: 0.02);
        pen.Release();
        Assert.Equal([false, true, false], pen.Nodes.Select(n => n.Smooth));
        Assert.Equal(0.1, pen.Nodes[1].Out.X, 9);
        Assert.Equal(0.05, pen.Nodes[1].Out.Y, 9);
        Assert.Equal(-pen.Nodes[1].Out, pen.Nodes[1].In);
        pen.Press(new Vec2d(0.5, 0.5), alt: true, hit: 0.02);
        Assert.True(pen.Nodes[2].Smooth);
        pen.Press(new Vec2d(0.5, 0.5), alt: true, hit: 0.02);
        Assert.False(pen.Nodes[2].Smooth);
        Assert.Equal(Vec2d.Zero, pen.Nodes[2].Out);
        pen.Press(new Vec2d(0.105, 0.1), alt: false, hit: 0.02);
        Assert.NotNull(closed);
        Assert.True(closed.Closed);
        Assert.Equal(3, closed.Nodes.Length);
        var json = pen.MaskJson("op_1", "Mask 1");
        var p = ProjectEdits.Add(DemoProject.Create(), "mask", json, out var id);
        var mask = p.FindOutput("op_1")!.Masks.Find(id)!;
        Assert.Equal("Mask 1", mask.Name);
        Assert.True(mask.Path.Closed);
        Assert.Equal(1 + 3 * 16, PenTool.Flatten(mask.Path).Length);
    }

    [Fact]
    public void TheGenericEditsRefuseWhatTheyCannotDoAndSayWhy()
    {
        var p = DemoProject.Create();
        var noId = Assert.Throws<EditRefusedException>(() => ProjectEdits.SetField(p, "sf_nope", "name", "\"x\""));
        Assert.Contains("sf_nope", noId.Message, StringComparison.Ordinal);
        Assert.Throws<EditRefusedException>(() => ProjectEdits.SetField(p, "sf_wall", "id", "\"other\""));
        var inUse = Assert.Throws<EditRefusedException>(() => ProjectEdits.Remove(p, "ms_wall"));
        Assert.Contains("surfaces", inUse.Message, StringComparison.Ordinal);
        Assert.Throws<EditRefusedException>(() => ProjectEdits.Add(p, "wizard", "{}", out _));
        Assert.Throws<EditRefusedException>(() => ProjectEdits.Add(p, "pin", "{\"world\":{\"x\":0,\"y\":0,\"z\":0},\"pixel\":{\"x\":1,\"y\":1},\"enabled\":true}", out _));
        var moved = ProjectEdits.Move(p, "it_outro", 0);
        Assert.Equal("it_outro", moved.FindPlaylist("pl_show")!.Items[0].Id);
        var pinned = ProjectEdits.SetField(p, "pn_3", "enabled", "false");
        Assert.False(pinned.FindProjector("pj_left")!.Pins.Find("pn_3")!.Enabled);
        var masked = ProjectEdits.SetField(p, "mk_door", "feather", "0.05");
        Assert.Equal(0.05, masked.FindSurface("sf_wall")!.Masks[0].Feather);
    }

    [Fact]
    public void CollectionSyncKeepsViewModelsByIdInTheModelsOrder()
    {
        var list = new System.Collections.ObjectModel.ObservableCollection<(string Id, string Name)>();
        var made = 0;
        void Sync(params (string Id, string Name)[] model) =>
            list.SyncById(model, m => m.Id, v => v.Id, m =>
            {
                made++;
                return m;
            }, (_, _) => { });
        Sync(("a", "A"), ("b", "B"), ("c", "C"));
        Sync(("c", "C"), ("a", "A"), ("d", "D"));
        Assert.Equal(["c", "a", "d"], list.Select(x => x.Id));
        Assert.Equal(4, made);
    }

    [Fact]
    public void TheOrbitCameraLooksAtItsTargetAndSendsItsOpticsAsProjectJson()
    {
        var cam = new OrbitCamera { Target = new Vec3d(0, 1, 0), Yaw = 0, Pitch = 0, Distance = 5 };
        Assert.Equal(new Vec3d(0, 1, 5), cam.Eye);
        var px = PlayMap.Core.Geometry.CameraModel.Project(cam.Target, cam.IntrinsicsFor(1280, 720), LensDistortion.None, cam.Pose)!.Value;
        Assert.Equal(640, px.X, 6);
        Assert.Equal(360, px.Y, 6);
        cam.Zoom(2);
        Assert.True(cam.Distance < 5);
        var json = System.Text.Json.Nodes.JsonNode.Parse(cam.ToJson(1280, 720))!;
        Assert.Equal(1280, json["intrinsics"]!["width"]!.GetValue<int>());
        Assert.NotNull(json["pose"]!["position"]);
    }

    [Fact]
    public void TheShortcutTableLeavesAFieldItsKeysButTheFewMarkedWhileTyping()
    {
        var ran = new List<string>();
        var q = new RelayCommand(() => ran.Add("q"));
        var next = new RelayCommand(() => ran.Add("next"));
        var help = new RelayCommand(() => ran.Add("help"));
        var entries = new List<Shortcut>
        {
            new("Q", "", "MAP", Avalonia.Input.Key.Q, Avalonia.Input.KeyModifiers.None, q),
            new("Ctrl+Right", "", "Transport", Avalonia.Input.Key.Right, Avalonia.Input.KeyModifiers.Control, next),
            new("F1", "", "Desk", Avalonia.Input.Key.F1, Avalonia.Input.KeyModifiers.None, help) { WhileTyping = true },
        };
        var table = ShortcutMap.For(entries);
        Assert.False(table.Handle(Avalonia.Input.Key.Q, Avalonia.Input.KeyModifiers.None, typing: true));
        Assert.False(table.Handle(Avalonia.Input.Key.Right, Avalonia.Input.KeyModifiers.Control, typing: true));
        Assert.True(table.Handle(Avalonia.Input.Key.F1, Avalonia.Input.KeyModifiers.None, typing: true));
        Assert.True(table.Handle(Avalonia.Input.Key.Q, Avalonia.Input.KeyModifiers.None, typing: false));
        Assert.True(table.Handle(Avalonia.Input.Key.Right, Avalonia.Input.KeyModifiers.Control, typing: false));
        Assert.Equal(["help", "q", "next"], ran);
    }

    [Fact]
    public async Task ARefusalBecomesAToastInPlainWordsWithTheVerbThatCausedIt()
    {
        await using var link = new FakeEngineLink(clock: new PlayMap.Core.Time.ManualClock());
        await using var session = new DeskSession(a => a());
        session.Attach(link, "Demo");
        var r = await session.SendAsync("PLAYLIST GO pl_nope", Ct);
        Assert.False(r.Ok);
        Assert.Equal(ToastKind.Refused, session.LastToast!.Kind);
        Assert.StartsWith("No playlist 'pl_nope'", session.LastToast.Text, StringComparison.Ordinal);
        Assert.Equal("PLAYLIST GO pl_nope", session.LastToast.Verb);
        Assert.Equal("Already playing", DeskSession.Plain("ERR already playing"));
        // A drag's refusal repeated folds into the toast already up.
        await session.SendAsync("PLAYLIST GO pl_nope", Ct);
        Assert.Single(session.Toasts);
        link.SetStatus(PlayMap.Core.Wire.LinkStatus.Down);
        var down = await session.SendAsync("BLACKOUT ON", Ct);
        Assert.False(down.Ok);
        Assert.Contains("nothing was sent", down.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BLACKOUT ON", link.SentLines);
    }
}
