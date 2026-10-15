using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Engine.Playback;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.VideoIo;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>A live input's loss policy and its backup, on the fake provider and the rig's hand-moved clock: a feed is lost at LostMs
/// (or LostMs after it opened with no frame); hold keeps the last picture, black routes the renderer to black and a fallback to its
/// picture, in a playlist too; a backup takes over on loss, journalled once, and only a person's INPUT SWITCH goes back; a feed
/// whose loss showed black or a fallback is shown again only after RecoverMs of steady frames.</summary>
public sealed class VideoIoLossTests
{
    /// <summary>The engine's media backend as far as the renderer's live inputs go: each opens through the hub.</summary>
    private sealed class HubMedia(VideoIoHub hub) : IMediaBackend
    {
        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();

        public IVideoSource? Open(MediaItem item) => hub.Open(item);

        public MediaReadiness Readiness(MediaItem item) => new(true, true, true, true, "ready");

        public string? PathForHash(string contentHash) => null;
    }

    private static Project WithStandby() => VideoIoRig.Show() with
    {
        Media = [new MediaItem("md_standby", "Standby loop", MediaKind.Video, "/clips/standby.mp4", "", 0, MediaInfo.Unknown, [])],
    };

    private static EngineSnapshot Snap(VideoIoRig rig, PlaybackSnapshot? playback = null) =>
        new(new ProjectSnapshot(rig.Engine.Store.Project, 1, ""), playback ?? PlaybackSnapshot.Empty, AirState.Initial);

    /// <summary>Frames until the renderer's slot for a source answers (its open runs on a worker), as a renderer asks each frame.</summary>
    private static void UntilDrawn(EngineSources sources, EngineSnapshot snap, SourceRef source, ref long frame)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            sources.BeginFrame(snap, ++frame);
            using var lease = sources.Acquire(source, 0);
            if (lease is not null)
                return;
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the input never opened for the renderer");
            Thread.Yield();
        }
    }

    private static List<JournalRow> Said(VideoIoRig rig) => [.. rig.Engine.Journal.Recent(100).Where(j => j.Kind == "videoio")];

    /// <summary>Frames on a feed every 20 ms (50 fps) for a span, the hub ticking as it would.</summary>
    private static void Deliver(VideoIoRig rig, FakeLiveSource feed, int ms, byte luma = 128)
    {
        for (var t = 0; t < ms; t += 20)
        {
            feed.Push(luma: luma);
            rig.AdvanceMs(20);
            rig.Engine.VideoIo.Tick();
        }
    }

    [Fact]
    public async Task HoldKeepsTheFeedBlackAndAFallbackRouteTheRendererAndAPlaylistShowsTheSame()
    {
        await using var rig = new VideoIoRig(WithStandby());
        var events = new List<ShowEvent>();
        rig.Engine.EventRaised += e => events.Add(e);
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var feed = rig.Omt.Opened.Single();
        Deliver(rig, feed, 200);
        using var sources = new EngineSources(new HubMedia(rig.Engine.VideoIo));
        long frame = 0;
        var cam = SourceRef.Media(id);
        UntilDrawn(sources, Snap(rig), cam, ref frame);
        Assert.Null(sources.Route(cam, 0));
        Assert.Equal("", rig.Input(id).GetProperty("loss").GetString());

        // Hold (the default): lost at LostMs, said once, and the renderer keeps reading the feed (it holds the last picture).
        rig.AdvanceMs(1_100);
        rig.Engine.VideoIo.Tick();
        rig.Engine.VideoIo.Tick();
        sources.BeginFrame(Snap(rig), ++frame);
        Assert.Null(sources.Route(cam, 0));
        var row = rig.Input(id);
        Assert.Equal("lost", row.GetProperty("state").GetString());
        Assert.Equal("hold", row.GetProperty("loss").GetString());
        var lost = Assert.Single(Said(rig), j => j.Text.Contains("lost", StringComparison.Ordinal));
        Assert.Equal("Cam 1 lost (no frame for 1.1 s) — holding its last picture", lost.Text);
        Assert.False(lost.Ok);
        var raised = Assert.Single(events, e => e.Kind == "input.lost");
        var fields = JsonDocument.Parse(raised.FieldsJson).RootElement;
        Assert.Equal(id, fields.GetProperty("input").GetString());
        Assert.Equal("hold", fields.GetProperty("shows").GetString());

        // Black: the renderer is routed to black at the next tick.
        var black = await rig.Ok($"INPUT FALLBACK {id} BLACK");
        Assert.Equal("Cam 1 on loss: shows black", black.Effect);
        rig.Engine.VideoIo.Tick();
        sources.BeginFrame(Snap(rig), ++frame);
        Assert.Equal(SourceRef.None, sources.Route(cam, 0));
        Assert.Equal("black", rig.Input(id).GetProperty("loss").GetString());
        Assert.Equal("amber", ContextLight(rig));

        // A fallback: the renderer draws it (a library item through the item's own decoder, a colour by itself).
        Assert.Equal("Cam 1 on loss: shows Standby loop", (await rig.Ok($"INPUT FALLBACK {id} media:md_standby")).Effect);
        rig.Engine.VideoIo.Tick();
        sources.BeginFrame(Snap(rig), ++frame);
        Assert.Equal(SourceRef.Media("md_standby"), sources.Route(cam, 0));
        Assert.Equal("fallback", rig.Input(id).GetProperty("loss").GetString());
        Assert.Equal("media:md_standby", rig.Input(id).GetProperty("fallback").GetString());
        await rig.Ok($"INPUT FALLBACK {id} color:#203040");
        rig.Engine.VideoIo.Tick();
        sources.BeginFrame(Snap(rig), ++frame);
        Assert.Equal(SourceRef.Solid("#203040"), sources.Route(cam, 0));

        // A playlist playing the input shows what the input shows.
        var list = new Playlist("pl_1", "Cams", [PlaylistItem.For("it_1", cam, "Cam 1")], false, null);
        var lanes = new PlaybackLanes(_ => 0);
        var project = rig.Engine.Store.Project with { Playlists = [list] };
        lanes.Reconcile(project);
        lanes.Go(list, 0, 1_000);
        var playing = new EngineSnapshot(new ProjectSnapshot(project, 2, ""), lanes.Snapshot, AirState.Initial);
        sources.BeginFrame(playing, frame + 1);
        Assert.Equal(SourceRef.Solid("#203040"), sources.Route(SourceRef.Playlist("pl_1"), 2_000));

        // Only one loss was said, whatever the policy became while it lasted.
        Assert.Single(events, e => e.Kind == "input.lost");
    }

    [Fact]
    public async Task AFeedLeavesBlackOrItsFallbackOnlyAfterRecoverMsOfSteadyFramesAndAHeldFeedAtOnce()
    {
        await using var rig = new VideoIoRig(WithStandby());
        var events = new List<ShowEvent>();
        rig.Engine.EventRaised += e => events.Add(e);
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        await rig.Ok($"INPUT FALLBACK {id} media:md_standby");
        var input = rig.Engine.VideoIo.InputOf(id)!;
        var feed = rig.Omt.Opened.Single();
        Deliver(rig, feed, 100);
        rig.AdvanceMs(1_100);
        rig.Engine.VideoIo.Tick();
        Assert.Equal(SourceRef.Media("md_standby"), input.LossRoute);
        Assert.Contains(Said(rig), j => j.Text == "Cam 1 lost (no frame for 1.1 s) — showing its fallback Standby loop");

        // Frames again, but a gap (stale) inside the RecoverMs starts the count again.
        Deliver(rig, feed, 1_000);
        rig.AdvanceMs(100);
        rig.Engine.VideoIo.Tick();
        Assert.Equal("stale", rig.Input(id).GetProperty("state").GetString());
        Deliver(rig, feed, 1_900);
        Assert.Equal(SourceRef.Media("md_standby"), input.LossRoute);
        Assert.Equal("fallback", rig.Input(id).GetProperty("loss").GetString());
        Deliver(rig, feed, 200);
        Assert.Null(input.LossRoute);
        Assert.Equal("", rig.Input(id).GetProperty("loss").GetString());
        Assert.Contains(Said(rig), j => j.Ok && j.Text == "Cam 1 is back (steady for 2.0 s) — showing its primary");
        Assert.Single(events, e => e.Kind == "input.back");

        // Held: there is no picture to leave, so the feed is back at its first steady frame.
        await rig.Ok($"INPUT FALLBACK {id} HOLD");
        rig.AdvanceMs(1_100);
        rig.Engine.VideoIo.Tick();
        Assert.Equal("hold", rig.Input(id).GetProperty("loss").GetString());
        feed.Push();
        rig.Engine.VideoIo.Tick();
        Assert.Equal("", rig.Input(id).GetProperty("loss").GetString());
        Assert.Contains(Said(rig), j => j.Text == "Cam 1 is back (frames again) — showing its primary");
    }

    [Fact]
    public async Task AFeedThatNeverConnectsIsLostLostMsAfterItOpened()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 9)\""));
        await rig.Ok($"INPUT FALLBACK {id} BLACK");
        rig.AdvanceMs(900);
        rig.Engine.VideoIo.Tick();
        Assert.Equal("", rig.Input(id).GetProperty("loss").GetString());
        rig.AdvanceMs(200);
        rig.Engine.VideoIo.Tick();
        Assert.Equal("black", rig.Input(id).GetProperty("loss").GetString());
        Assert.Contains(Said(rig), j => j.Text == "Cam 9 lost (no frame in the 1.1 s since it opened) — showing black");
    }

    [Fact]
    public async Task TheBackupTakesOverOnLossJournalledOnceAndOnlyInputSwitchGoesBack()
    {
        await using var rig = new VideoIoRig();
        var events = new List<ShowEvent>();
        rig.Engine.EventRaised += e => events.Add(e);
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var other = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 2)\""));
        Assert.Equal($"Cam 2 has no backup set — INPUT BACKUP {other} <uri>", await rig.Refused($"INPUT SWITCH {other} BACKUP"));
        Assert.Equal("Cam 2 has no backup", await rig.Refused($"INPUT BACKUP {other} OFF"));
        Assert.Equal("omt:STUDIO (Cam 1) is Cam 1's primary — a backup is another feed", await rig.Refused($"INPUT BACKUP {id} \"omt:STUDIO (Cam 1)\""));
        Assert.StartsWith("a backup is a live input", await rig.Refused($"INPUT BACKUP {id} rtsp://10.0.0.9/cam"), StringComparison.Ordinal);

        var set = await rig.Ok($"INPUT BACKUP {id} \"omt:STUDIO2 (Cam 1)\"");
        Assert.Equal("Cam 1's backup is omt:STUDIO2 (Cam 1): it takes over when the primary is lost", set.Effect);
        Assert.Equal("omt:STUDIO2 (Cam 1)", rig.Engine.Store.Project.FindMedia(id)!.Live!.Backup);
        Assert.Equal("Cam 1's backup is already omt:STUDIO2 (Cam 1)", await rig.Refused($"INPUT BACKUP {id} \"omt:STUDIO2 (Cam 1)\""));
        // Warm beside the primary: its own receiver, keyed apart.
        var primary = rig.Omt.Opened.Single(s => s.Address == "STUDIO (Cam 1)");
        var backup = rig.Omt.Opened.Single(s => s.Address == "STUDIO2 (Cam 1)");
        Assert.Equal(id + "/backup", backup.Key);
        Assert.Equal("connecting", rig.Input(id).GetProperty("backupState").GetString());

        primary.Push(luma: 60);
        backup.Push(luma: 200);
        rig.Engine.VideoIo.Tick();
        using var handle = rig.Engine.VideoIo.Open(rig.Engine.Store.Project.FindMedia(id)!)!;
        Assert.Equal(60, Luma(handle));
        Assert.Equal("Cam 1 is already on its primary", await rig.Refused($"INPUT SWITCH {id} PRIMARY"));

        // The primary goes quiet while the backup delivers: the backup takes over, said once.
        Deliver(rig, backup, 1_200, luma: 200);
        Assert.Equal("backup", rig.Input(id).GetProperty("using").GetString());
        Assert.Equal("", rig.Input(id).GetProperty("loss").GetString());
        Assert.Equal(200, Luma(handle));
        Assert.Single(Said(rig), j => j.Text.StartsWith("Cam 1 lost (no frame for", StringComparison.Ordinal)
            && j.Text.EndsWith("— showing its backup omt:STUDIO2 (Cam 1)", StringComparison.Ordinal));
        Assert.Single(events, e => e.Kind == "input.switched");
        Assert.Equal("amber", ContextLight(rig));
        Assert.EndsWith("· on its backup", ContextState(rig, "input:" + id), StringComparison.Ordinal);

        // The primary comes back: the input stays on its backup (never flap on the air).
        for (var i = 0; i < 150; i++)
        {
            primary.Push(luma: 60);
            backup.Push(luma: 200);
            rig.AdvanceMs(20);
            rig.Engine.VideoIo.Tick();
        }
        Assert.Equal("backup", rig.Input(id).GetProperty("using").GetString());
        Assert.Single(events, e => e.Kind == "input.switched");

        // A person's word returns it, answered at the next frame from the primary.
        var switching = rig.Run($"INPUT SWITCH {id} PRIMARY");
        while (!switching.IsCompleted)
        {
            primary.Push(luma: 60);
            await Task.Yield();
        }
        var back = await switching;
        Assert.True(back.Ok, back.Payload);
        Assert.True(back.Applied);
        var observed = JsonDocument.Parse(back.Payload!).RootElement;
        Assert.Equal("primary", observed.GetProperty("using").GetString());
        Assert.Equal(0, observed.GetProperty("firstFrameMs").GetInt64());
        Assert.Equal(60, Luma(handle));
        Assert.Contains(Said(rig), j => j.Text == "PlayMapDesk switched Cam 1 to its primary (omt:STUDIO (Cam 1))");

        // Switching to a backup that went quiet is refused with its age; OFF while on the primary just lets it go.
        rig.AdvanceMs(1_500);
        primary.Push(luma: 60);
        rig.Engine.VideoIo.Tick();
        Assert.Equal("Cam 1's backup has given no frame for 1.5 s", await rig.Refused($"INPUT SWITCH {id} BACKUP"));
        Assert.Equal("Cam 1 has no backup now", (await rig.Ok($"INPUT BACKUP {id} OFF")).Effect);
        Assert.True(backup.Closed);
        Assert.False(primary.Closed);
        Assert.Equal("", rig.Input(id).GetProperty("backup").GetString());
    }

    [Fact]
    public async Task ABackupThatGoesWhileInUseReturnsTheInputToItsPrimary()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        await rig.Ok($"INPUT BACKUP {id} \"omt:STUDIO2 (Cam 1)\"");
        var primary = rig.Omt.Opened.Single(s => s.Address == "STUDIO (Cam 1)");
        var backup = rig.Omt.Opened.Single(s => s.Address == "STUDIO2 (Cam 1)");
        primary.Push();
        Deliver(rig, backup, 1_200);
        Assert.Equal("backup", rig.Input(id).GetProperty("using").GetString());
        var off = await rig.Ok($"INPUT BACKUP {id} OFF");
        Assert.Equal("Cam 1 has no backup now — it shows its primary again", off.Effect);
        Assert.Equal("primary", rig.Input(id).GetProperty("using").GetString());
        Assert.True(backup.Closed);
    }

    [Fact]
    public async Task EveryFallbackRefusalSaysWhy()
    {
        await using var rig = new VideoIoRig(WithStandby());
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var other = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 2)\""));
        Assert.Equal("Cam 1 already holds its last picture on loss", await rig.Refused($"INPUT FALLBACK {id} HOLD"));
        Assert.Equal($"Cam 1 cannot fall back to a live input (media:{other}): a fallback is a picture this node always has",
            await rig.Refused($"INPUT FALLBACK {id} media:{other}"));
        Assert.StartsWith("Cam 1 cannot fall back to a live input", await rig.Refused($"INPUT FALLBACK {id} live:{other}"), StringComparison.Ordinal);
        Assert.Equal("no generator gn_nothing in the project", await rig.Refused($"INPUT FALLBACK {id} generator:gn_nothing"));
        Assert.StartsWith("INPUT FALLBACK takes HOLD, BLACK or a source", await rig.Refused($"INPUT FALLBACK {id} SOMETIMES"), StringComparison.Ordinal);
        Assert.StartsWith("Standby loop is not a live input", await rig.Refused("INPUT FALLBACK md_standby BLACK"), StringComparison.Ordinal);
        Assert.Equal("no live input md_nothing", await rig.Refused("INPUT FALLBACK md_nothing BLACK"));
        // The generic edits hold the same line.
        Assert.Contains("never a live input", await rig.Refused($"SET {id} live {{\"onLoss\":\"fallback\",\"fallback\":\"media:{other}\"}}"), StringComparison.Ordinal);
        Assert.Contains("names its picture in live.fallback", await rig.Refused($"SET {id} live {{\"onLoss\":\"fallback\"}}"), StringComparison.Ordinal);
        Assert.Contains("its own location", await rig.Refused($"SET {id} live {{\"backup\":\"omt:STUDIO (Cam 1)\"}}"), StringComparison.Ordinal);
    }

    private static string ContextState(VideoIoRig rig, string item)
    {
        using var ctx = JsonDocument.Parse(rig.Engine.Context());
        return ctx.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == item).GetProperty("state").GetString()!;
    }

    private static string ContextLight(VideoIoRig rig)
    {
        using var ctx = JsonDocument.Parse(rig.Engine.Context());
        return ctx.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "videoio").GetProperty("light").GetString()!;
    }

    private static byte Luma(IVideoSource handle)
    {
        using var lease = handle.Acquire(0);
        Assert.NotNull(lease);
        return Marshal.ReadByte(lease!.Planes[0].Data);
    }
}
