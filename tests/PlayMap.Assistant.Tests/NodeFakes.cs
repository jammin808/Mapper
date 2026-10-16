using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant.Tests;

/// <summary>The node as a test sees it: every line handed to it is recorded with who sent it, and answered
/// by <see cref="Reply"/> (OK by default, with an id for the verbs that make something).</summary>
internal sealed class FakeEndpoint : IWireEndpoint
{
    private readonly Project _project;

    public FakeEndpoint(Project? project = null) => _project = project ?? SampleShow.Project();

    public List<(string Line, SessionInfo Session)> Handled { get; } = [];

    public Func<WireLine, CommandReply> Reply { get; set; } = line => line.Verb switch
    {
        "GENERATOR" when line.WordUpper(1) == "NEW" => CommandReply.Success("gn7e7e7e7e", "made a generator"),
        "PLAYLIST" when line.WordUpper(1) == "NEW" => CommandReply.Success("pl7f7f7f7f", "made a playlist"),
        _ => CommandReply.Success(),
    };

    /// <summary>Runs before the reply: a test cancels here to cancel "while the node runs the line".</summary>
    public Action<WireLine>? OnHandle { get; set; }

    public string StateJson { get; set; } = SampleShow.StateJson;

    public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct)
    {
        Handled.Add((line.Raw, session));
        OnHandle?.Invoke(line);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Reply(line));
    }

    public (string Json, long Revision) State() => (StateJson, 412);

    public string Context() => SampleShow.ContextJson;

    public (string Json, long Revision) ProjectJson() => (PlayMap.Core.Model.ProjectJson.Serialize(_project), 118);

    public event Action<long>? StateChanged;

    public event Action<long>? ProjectChanged;

    public void RaiseChanged(long revision)
    {
        StateChanged?.Invoke(revision);
        ProjectChanged?.Invoke(revision);
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly TimeZoneInfo _zone;

    public ManualTime(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        Now = now;
        _zone = zone ?? TimeZoneInfo.Utc;
    }

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => _zone;
}

/// <summary>An approval desk on the bus that takes (or declines) what it is offered.</summary>
internal sealed class FakeSink : IProposalSink
{
    public string Name => "Stagewatch FOH";

    public string? Answer { get; set; } = "p17";

    public List<Proposal> Offered { get; } = [];

    public Task<string?> OfferAsync(Proposal proposal, CancellationToken ct)
    {
        Offered.Add(proposal);
        return Task.FromResult(Answer);
    }
}

/// <summary>A small show: a building with two windows and a door, three projectors and a confidence screen,
/// a Snow particle generator, and the Show playlist. Ids have the generated shape so the gate checks them.</summary>
internal static class SampleShow
{
    public const string WindowLeft = "sf00000001";
    public const string WindowRight = "sf00000002";
    public const string Door = "sf00000003";
    public const string Snow = "gn5a5a5a5a";
    public const string ShowPlaylist = "pl00000001";

    public static Project Project()
    {
        var mesh = new MeshAsset("ms00000001", "Facade", MeshOrigin.Scanned, "scans/facade.ply", "sha256:" + new string('a', 64), "", ParamSet.Empty, 1200, 2000);
        Surface Surface(string id, string name, bool visible, params string[] tags) =>
            new(id, name, mesh.Id, Transform3.Identity, [], SurfaceContent.Empty, 1, [], visible, false, "#FF8800", [.. tags]);
        Projector Projector(string id, string name) =>
            new(id, name, Intrinsics.FromThrowRatio(1920, 1080, 1.5), LensDistortion.None, Pose.Identity, 8000, "#00FF00", false, [], SolveLocks.Shift, 0.6);
        Output Out(string id, string name, OutputMode mode, string? projector, bool auto3D) =>
            new(id, name, OutputKind.Display, "", 1920, 1080, 60, mode, projector, SourceRef.None, new RectD(0, 0, 1920, 1080),
                OutputWarp.None, BlendSettings.Default with { Auto3D = auto3D }, ColorTrim.Neutral, [], new RectD(0, 0, 1920, 1080), 0, true);
        var stage = Stage.Empty with
        {
            Meshes = [mesh],
            Surfaces = [Surface(WindowLeft, "Window L", true, "window", "glass"), Surface(WindowRight, "Window R", false, "Window"), Surface(Door, "Door", true, "door")],
            Projectors = [Projector("pj00000001", "Projector 1"), Projector("pj00000002", "Projector 2"), Projector("pj00000003", "Projector 3")],
        };
        var playlist = new Playlist(ShowPlaylist, "Show", [PlaylistItem.For("it00000001", SourceRef.Generator(Snow), "Snow")], false, null);
        return PlayMap.Core.Model.Project.CreateDefault("Show") with
        {
            Generators = [new Generator("gn_testcard", "Test card", "pattern.testcard", ParamSet.Empty), new Generator(Snow, "Snow", "particles.field", ParamSet.Empty)],
            Stage = stage,
            Playlists = [playlist],
            Outputs =
            [
                Out("op00000001", "Projector 1", OutputMode.Projector, "pj00000001", true),
                Out("op00000002", "Projector 2", OutputMode.Projector, "pj00000002", false),
                Out("op00000003", "Projector 3", OutputMode.Projector, "pj00000003", true),
                Out("op00000004", "Confidence", OutputMode.Canvas, null, false),
            ],
        };
    }

    /// <summary>The STATE example from docs/WIRE.md.</summary>
    public const string StateJson =
        """
        {"v":1,"seq":412,"id":"a1b2c3d4","t":"playmap","name":"Stage left","version":"0.2.0","at":1760101234567,
         "light":"green","headline":"Show · Intro 0:42 left · 3 outputs green · synced to FOH (0.2 ms)",
         "air":{"blackout":false,"frozen":false,"pattern":null,"identify":false},
         "video":{"file":"intro.mp4","role":"playlist","tag":"Show","position":62,"length":210,"remaining":148,
           "positionText":"1:02","lengthText":"3:30","remainingText":"2:28","text":"Show intro.mp4 · 1:02 / 3:30 · 2:28 left",
           "chip":"Show 2:28","playing":true,"ended":false,"loops":false,"out":false,"call":"",
           "key":"md1a2b3c4d","positionMs":62000,"lengthMs":210000,"remainingMs":148000,"paused":false,"rate":1,
           "seekable":true,"seq":7},
         "playlists":[{"id":"pl…","name":"Show","focused":true,"state":"playing","index":0,"item":{"id":"it…","label":"Intro",
           "source":"media:md…"},"positionMs":62000,"lengthMs":210000,"remainingMs":148000,"rate":1,"loop":false,
           "syncGroup":null,"leader":null,"seq":7,"next":{"id":"it…","label":"Main"}}],
         "outputs":[{"id":"op_1","name":"Projector 1","mode":"projector","display":"DISPLAY2","width":1920,"height":1080,
           "refreshHz":60,"open":true,"fps":60.0,"dropped":0,"frameMsP95":4.1,"pattern":null,"light":"green"}],
         "media":{"items":12,"missing":0,"ready":true,"transfers":[]},
         "stage":{"surfaces":6,"meshes":2,"projectors":[{"id":"pj…","name":"Left","solved":true,"residualPx":0.6}],"scans":1},
         "scan":null,
         "sync":{"role":"follower","leader":"9f8e7d6c","offsetMs":0.21,"rttMs":0.35,"driftPpm":3.2,"groups":["show"]},
         "links":{"patterns":{"state":"live","host":"10.0.0.5:9697","paired":true,"clip":"VT 2:28"},
           "stagewatch":{"state":"live","host":"10.0.0.6:9924","timer":{"state":"running","text":"12:30","seconds":750,
           "phase":"warning","color":"#F59E0B","title":"Keynote","message":""}}},
         "peers":[{"t":"patterns","id":"…","name":"FOH","light":"green","live":true}],
         "machine":{"renderer":"OpenGL 4.6 NVIDIA RTX A4000","cpuPct":12,"memMb":840,"gcPauseMsMax":0.4},
         "project":{"id":"pr…","name":"Show","revision":118,"path":"D:/Shows/show.playmap","dirty":false}}
        """;

    public const string ContextJson =
        """{"v":1,"node":{"t":"playmap","id":"a1b2c3d4","name":"Stage left"},"at":1760101234567,"light":"green","headline":"Show · Intro 0:42 left","items":[],"links":[],"recent":[],"commands":[],"peers":[]}""";
}
