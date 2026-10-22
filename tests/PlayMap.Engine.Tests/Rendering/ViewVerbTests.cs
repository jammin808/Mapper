using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>VIEW with no GPU: a render-backed view is refused with words, a bad view id, kind or size is
/// refused cheaply, a peer elsewhere that has not paired is refused, a camera view is fed by whatever the
/// scan side registered — into the ring the desk reads — and a view nobody renews is let go.</summary>
public sealed class ViewVerbTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("d1");

    private sealed class GreyCamera : IPreviewFeed
    {
        public ViewRequest? Started { get; private set; }
        public bool Stopped { get; private set; }

        public IDisposable? Start(ViewRequest request, PreviewSink sink, out string why)
        {
            why = "";
            if (request.TargetId != "cm_1")
            {
                why = $"no camera {request.TargetId}";
                return null;
            }
            Started = request;
            var frame = new byte[1280 * 720];
            Array.Fill(frame, (byte)90);
            sink.WriteGray(frame, 1280, 720, 1280, 1);
            return new Stop(this);
        }

        private sealed class Stop(GreyCamera feed) : IDisposable
        {
            public void Dispose() => feed.Stopped = true;
        }
    }

    private static EngineCore Engine(ManualClock? clock = null)
    {
        var services = EngineServices.ForTests(clock ?? new ManualClock(5_000_000_000));
        return new EngineCore(services with { Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8] } });
    }

    private static async Task<CommandReply> Run(EngineCore engine, string line, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, session ?? Desk, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoGpuARenderBackedViewIsRefusedWithWords()
    {
        await using var engine = Engine();
        var r = await Run(engine, "VIEW editor SHOW editor 1280x720");
        Assert.False(r.Ok);
        Assert.Contains("no OpenGL", r.Payload, StringComparison.Ordinal);
        Assert.False((await Run(engine, "VIEW op-op_1 SHOW output:op_1 480x270")).Ok);
        Assert.Contains("no output", (await Run(engine, "VIEW op-x SHOW output:op_nowhere")).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadWordsAreRefusedCheaply()
    {
        await using var engine = Engine();
        Assert.StartsWith("a view shows editor", (await Run(engine, "VIEW v1 SHOW banana")).Payload, StringComparison.Ordinal);
        Assert.StartsWith("a view id is", (await Run(engine, "VIEW ../etc SHOW editor")).Payload, StringComparison.Ordinal);
        Assert.StartsWith("a view's size", (await Run(engine, "VIEW v1 SHOW editor 12x9000")).Payload, StringComparison.Ordinal);
        Assert.StartsWith("VIEW <view> CAMERA", (await Run(engine, "VIEW v1 CAMERA {\"pose\":1}")).Payload, StringComparison.Ordinal);
        Assert.StartsWith("no view v1", (await Run(engine, "VIEW v1 HIDE")).Payload, StringComparison.Ordinal);
        Assert.False((await Run(engine, "VIEW v1 SPIN")).Ok);
        var remote = new SessionInfo("w1", "wire", "10.0.0.8", false, "Peer", false);
        Assert.StartsWith("not paired", (await Run(engine, "VIEW v1 SHOW editor", remote)).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACameraViewIsFedByTheRegisteredFeedIntoTheRingTheDeskReads()
    {
        await using var engine = Engine();
        Assert.Contains("no camera pictures", (await Run(engine, "VIEW scan-camera SHOW camera:cm_1 960x540")).Payload, StringComparison.Ordinal);
        var feed = new GreyCamera();
        engine.Previews.Register("camera", feed);
        Assert.Contains("no camera cm_9", (await Run(engine, "VIEW scan-camera SHOW camera:cm_9 960x540")).Payload, StringComparison.Ordinal);

        var shown = await Run(engine, "VIEW scan-camera SHOW camera:cm_1 960x540");
        Assert.True(shown.Ok, shown.Payload);
        Assert.Equal(ViewKind.Camera, feed.Started!.Kind);
        var path = engine.Previews.RingPath("scan-camera");
        using (var reader = new PreviewRingReader(path))
        {
            var buffer = new byte[reader.MaxWidth * reader.MaxHeight * 4];
            Assert.True(reader.TryRead(buffer, 0, out var info));
            // 1280 × 720 thinned by whole steps to fit 960 × 540: every second pixel.
            Assert.Equal((640, 360), (info.Width, info.Height));
            Assert.Equal((90, 90, 90), HostRig.At(buffer, info.Width, 100, 100));
        }
        Assert.True((await Run(engine, "VIEW scan-camera HIDE")).Ok);
        Assert.True(feed.Stopped);
        Assert.False(File.Exists(path));
    }

    /// <summary>A view shown again after its ring went writes a new ring in a file of its own: the SHOW reply and STATE's
    /// machine.views name it, and a reader still mapping the old ring keeps the picture it had — no writer opens its file again
    /// (on Windows a file a reader maps cannot be made again, and on Linux making it again would cut it short under the map).</summary>
    [Fact]
    public async Task AViewShownAgainWritesANewRingAndAReaderOfTheOldOneKeepsItsPicture()
    {
        await using var engine = Engine();
        engine.Previews.Register("camera", new GreyCamera());
        var first = await Run(engine, "VIEW scan-camera SHOW camera:cm_1 960x540");
        Assert.True(first.Ok, first.Payload);
        var ringA = Ring(first);
        // Generations count from the engine's start (its wall-clock milliseconds × 1000): a ring made after a restart never takes
        // the name of one an earlier run made.
        Assert.EndsWith("~" + (engine.Services.Wall.UnixMs * 1000).ToString(CultureInfo.InvariantCulture) + ".preview", ringA, StringComparison.Ordinal);
        Assert.Equal(ringA, engine.Previews.RingOf("scan-camera"));
        Assert.Equal([("scan-camera", "camera:cm_1", ringA)], StateViews(engine));
        using var old = new PreviewRingReader(ringA);
        var buffer = new byte[old.MaxWidth * old.MaxHeight * 4];
        Assert.True(old.TryRead(buffer, -1, out var had));

        Assert.True((await Run(engine, "VIEW scan-camera HIDE")).Ok);
        Assert.Empty(StateViews(engine));
        Assert.Null(engine.Previews.RingOf("scan-camera"));
        var again = await Run(engine, "VIEW scan-camera SHOW camera:cm_1 960x540");
        Assert.True(again.Ok, again.Payload);
        var ringB = Ring(again);
        Assert.NotEqual(ringA, ringB);
        Assert.Equal([("scan-camera", "camera:cm_1", ringB)], StateViews(engine));

        // The old reader's map is the old ring, whole: its last picture is still there.
        Assert.True(old.TryRead(buffer, -1, out var still));
        Assert.Equal((had.Width, had.Height, had.Sequence), (still.Width, still.Height, still.Sequence));
        using var reader = new PreviewRingReader(ringB);
        Assert.True(reader.TryRead(new byte[reader.MaxWidth * reader.MaxHeight * 4], -1, out var info));
        Assert.Equal((640, 360), (info.Width, info.Height));
        Assert.True((await Run(engine, "VIEW scan-camera HIDE")).Ok);
    }

    /// <summary>The SHOW reply carries the STATE seq from which STATE shows the view: a STATE read before the SHOW counts below it and
    /// does not list the view; STATE from then on counts at least as far and lists it with the reply's ring.</summary>
    [Fact]
    public async Task TheShowReplyCarriesTheStateSeqFromWhichStateShowsTheView()
    {
        await using var engine = Engine();
        engine.Previews.Register("camera", new GreyCamera());
        var before = StateSeq(engine);
        Assert.Empty(StateViews(engine));
        var shown = await Run(engine, "VIEW scan-camera SHOW camera:cm_1 960x540");
        Assert.True(shown.Ok, shown.Payload);
        using var doc = JsonDocument.Parse(shown.Payload!);
        var seq = doc.RootElement.GetProperty("seq").GetInt64();
        Assert.True(before < seq, $"{before} < {seq}");
        Assert.True(StateSeq(engine) >= seq);
        Assert.Equal([("scan-camera", "camera:cm_1", Ring(shown))], StateViews(engine));
        Assert.True((await Run(engine, "VIEW scan-camera HIDE")).Ok);
    }

    private static long StateSeq(EngineCore engine)
    {
        using var doc = JsonDocument.Parse(engine.State().Json);
        return doc.RootElement.GetProperty("seq").GetInt64();
    }

    /// <summary>The ring a VIEW SHOW reply names.</summary>
    private static string Ring(CommandReply shown)
    {
        using var doc = JsonDocument.Parse(shown.Payload!);
        return doc.RootElement.GetProperty("ring").GetString()!;
    }

    /// <summary>STATE machine.views: (id, what, ring) of every view shown.</summary>
    private static List<(string, string, string)> StateViews(EngineCore engine)
    {
        using var doc = JsonDocument.Parse(engine.State().Json);
        return [.. doc.RootElement.GetProperty("machine").GetProperty("views").EnumerateArray()
            .Select(v => (v.GetProperty("id").GetString()!, v.GetProperty("what").GetString()!, v.GetProperty("ring").GetString()!))];
    }

    [Fact]
    public async Task AViewNotRenewedForFifteenSecondsIsHiddenAndItsRingLetGo()
    {
        var clock = new ManualClock(5_000_000_000);
        await using var engine = Engine(clock);
        var feed = new GreyCamera();
        engine.Previews.Register("camera", feed);
        Assert.True((await Run(engine, "VIEW scan-camera SHOW camera:cm_1 960x540")).Ok);
        var path = engine.Previews.RingPath("scan-camera");
        Assert.Contains("no view nowhere is shown", (await Run(engine, "VIEW nowhere KEEP")).Payload, StringComparison.Ordinal);

        // Renewed within its lease (KEEP, or SHOW of the same thing), it lives on.
        clock.NowNs += 10_000_000_000;
        Assert.True((await Run(engine, "VIEW scan-camera KEEP")).Ok);
        clock.NowNs += 10_000_000_000;
        Assert.True((await Run(engine, "VIEW scan-camera SHOW camera:cm_1 960x540")).Ok);
        clock.NowNs += 10_000_000_000;
        Assert.Empty(engine.Previews.Expire());
        Assert.True(File.Exists(path));
        Assert.False(feed.Stopped);

        // A viewer that stopped renewing: the lane's tick hides the view and lets its ring go, and says so.
        clock.NowNs += 6_000_000_000;
        Assert.True(await OpsRig.Eventually(() => !File.Exists(path)));
        // The tick that let the ring go journals it once the views have said so: a lane turn after it sees the row.
        await engine.Lane.RunAsync(() => { });
        Assert.True(feed.Stopped);
        Assert.Empty(engine.Previews.Shown());
        Assert.Contains(engine.Journal.Recent(10), r => r.Text.StartsWith("scan-camera hidden: not renewed for 15 s", StringComparison.Ordinal));
        Assert.False((await Run(engine, "VIEW scan-camera KEEP")).Ok);
    }

    [Fact]
    public void AViewRequestParsesTheDesksWords()
    {
        var scan = ViewRequest.Parse("scan-sc1-smart", "scan:sc_1:smart", "1280x720", out _)!;
        Assert.Equal((ViewKind.Scan, "sc_1", "smart"), (scan.Kind, scan.TargetId, scan.Variant));
        Assert.Equal("scan:sc_1:smart", scan.What);
        var pj = ViewRequest.Parse("pj-1", "projector:pj_1", null, out _)!;
        Assert.Equal((1280, 720), (pj.Width, pj.Height));
        Assert.Null(ViewRequest.Parse("x", "scan:sc_1:depth", null, out var why));
        Assert.Contains("scan:<id>:smart|confidence", why, StringComparison.Ordinal);
        Assert.Equal((1920, 1080), ViewRequest.Fit(3840, 2160, 16 / 9.0));
        Assert.Equal((432, 270), ViewRequest.Fit(480, 270, 1.6));
    }
}

/// <summary>VIEW with the render host: an output view reaches its ring at the output's aspect and at most ten
/// times a second, the editor follows CAMERA, a projector view is fitted to its raster, HIDE lets go, and
/// DISPLAYS answers the shape the desk reads.</summary>
[Collection("render host")]
public sealed class RenderedViewTests
{
    private static async Task<(PreviewFrameInfo Info, byte[] Pixels)> Frame(string path, long after = 0)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var reader = new PreviewRingReader(path);
                    var buffer = new byte[reader.MaxWidth * reader.MaxHeight * 4];
                    if (reader.TryRead(buffer, after, out var info))
                        return (info, buffer);
                }
                catch (InvalidDataException)
                {
                    // Being made: the next look finds it.
                }
            }
            Assert.True(waited.Elapsed < HostRig.Patience, $"no picture in {path}");
            await Task.Delay(30);
        }
    }

    [Fact]
    public async Task AnOutputViewReachesItsRingAtTheOutputsAspectAtMostTenTimesASecond()
    {
        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_a", SourceRef.Solid("#0000FF"), 64, 40)));
        // The clock starts before any picture is asked for: a frame ends after it starts, and the view is shown after that
        // frame, so the first frame that asks for a picture began after the clock did.
        var clock = Stopwatch.StartNew();
        await rig.Host.FramesAsync(1).WaitAsync(HostRig.Patience);
        await rig.Ok("VIEW op-op_a SHOW output:op_a 480x270");
        var path = rig.Engine.Previews.RingPath("op-op_a");
        var (info, px) = await Frame(path);
        Assert.Equal((432, 270), (info.Width, info.Height));
        Assert.Equal((0, 0, 255), HostRig.At(px, info.Width, 200, 100));

        await rig.Host.FramesAsync(40).WaitAsync(HostRig.Patience);
        var (later, _) = await Frame(path, info.Sequence);
        var seconds = clock.Elapsed.TotalSeconds;
        // Picture n was asked for at least (n − 1) × 100 ms after the first, and landed before it was read.
        Assert.True(later.Sequence <= seconds * 10 + 1, $"{later.Sequence} pictures in {seconds:0.00} s");

        Assert.True((await rig.Run("VIEW op-op_a HIDE")).Ok);
        Assert.False((await rig.Run("VIEW op-op_a HIDE")).Ok);
        Assert.Equal(0, rig.Host.ViewsShown);
    }

    [Fact]
    public async Task TheEditorFollowsItsCameraAndAProjectorViewFitsItsRaster()
    {
        await using var rig = HostRig.Start(BlendSolveTests.TwoProjectorsOnAWall());
        var eye = new
        {
            intrinsics = JsonDocument.Parse(ProjectJson.SerializeEntity(Intrinsics.FromFieldOfView(320, 180, 60))).RootElement,
            pose = JsonDocument.Parse(ProjectJson.SerializeEntity(Pose.LookAt(new Vec3d(0, 1, 6), Vec3d.Zero, Vec3d.UnitY))).RootElement,
            near = 0.05,
            far = 500,
        };
        await rig.Ok("VIEW editor CAMERA " + JsonSerializer.Serialize(eye));
        await rig.Ok("VIEW editor SHOW editor 320x180");
        var (editor, _) = await Frame(rig.Engine.Previews.RingPath("editor"));
        Assert.Equal((320, 180), (editor.Width, editor.Height));

        await rig.Ok("VIEW pj-pj_left SHOW projector:pj_left 3840x2160");
        var (projector, _) = await Frame(rig.Engine.Previews.RingPath("pj-pj_left"));
        Assert.Equal((1920, 1080), (projector.Width, projector.Height));
        var notEditor = await rig.Run("VIEW pj-pj_left CAMERA " + JsonSerializer.Serialize(eye));
        Assert.Contains("only an editor view", notEditor.Payload, StringComparison.Ordinal);
        Assert.Equal(2, rig.Host.ViewsShown);
    }

    [Fact]
    public async Task DisplaysAnswersTheShapeTheDeskReads()
    {
        await using var rig = HostRig.Start(Project.CreateDefault("Displays"));
        var r = await rig.Run("DISPLAYS");
        Assert.True(r.Ok, r.Payload);
        var first = JsonDocument.Parse(r.Payload!).RootElement.EnumerateArray().First();
        foreach (var field in new[] { "key", "name", "width", "height", "refreshHz", "primary" })
            Assert.True(first.TryGetProperty(field, out _), field);
        Assert.Equal(rig.Host.Displays()[0].Key, first.GetProperty("key").GetString());
    }
}
