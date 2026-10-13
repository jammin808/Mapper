using System.Diagnostics;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Engine.Rendering;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The render host on a hidden OpenGL context (Mesa is enough; DISPLAY=:99 under Xvfb): it draws
/// the default project, shows a playlist's clip at the frame its leader instant names, blacks out and puts a
/// pattern on the one output named, lays a scan frame raw on one output and says when it was presented, moves
/// a warp node without uploading a mesh again, allocates nothing on a warm static frame, and says why when
/// there is no display at all.</summary>
[Collection("render host")]
public sealed class RenderHostTests(ITestOutputHelper log)
{
    [Fact]
    public async Task ARenderHostWithAHiddenContextRendersTheDefaultProject()
    {
        await using var rig = HostRig.Start(Project.CreateDefault("Default"));
        var px = await rig.Read("op_1");

        // The test card's left edge is a white border (as Render's own test of the default project reads it).
        Assert.Equal((255, 255, 255), HostRig.At(px, 1920, 0, 540));
        Assert.StartsWith("OpenGL ", rig.Host.RendererName, StringComparison.Ordinal);
        Assert.NotEmpty(rig.Host.Displays());
        await rig.Host.FramesAsync(30).WaitAsync(HostRig.Patience);
        var health = Assert.Single(rig.Host.Health());
        Assert.Equal("op_1", health.OutputId);
        Assert.False(health.Open, "a headless output has no window");
        Assert.True(health.Fps > 1, $"the loop runs at {health.Fps:0.0} fps");
        var stats = rig.Host.Stats;
        log.WriteLine($"{rig.Host.RendererName}: default project (1920×1080 test card canvas → output), frame path {stats.FrameMs:0.00} ms mean, {stats.FrameMsP95:0.00} ms p95, loop {stats.Fps:0.0} fps");
        Assert.Contains("\"open\":false", rig.Engine.State().Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlaylistItemShowsTheFrameItsLeaderInstantNames()
    {
        var clip = Clips.Counted(3);
        var media = new MediaItem("md_clip", "Counted", MediaKind.Video, clip, "", new FileInfo(clip).Length,
            MediaInfo.Unknown with { DurationNs = 3_000_000_000, Width = 64, Height = 48, FrameRate = "25" }, []);
        var item = PlaylistItem.For("it_clip", SourceRef.Media("md_clip"), "Counted") with { End = EndAction.Hold };
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Playlist("pl_show"), 64, 48)) with
        {
            Media = [media],
            Playlists = [new Playlist("pl_show", "Show", [item], false, null)],
        };
        var files = new MediaFiles();
        await using var rig = HostRig.Start(project, files);
        await rig.Ok("PLAYLIST GO pl_show");

        // The play started at the leader's now; frame n covers [n, n + 1) × 40 ms of it.
        foreach (var frame in new[] { 25, 62, 13 })
        {
            rig.Clock.NowNs = 5_000_000_000 + (long)((frame + 0.5) * 40_000_000);
            var px = await rig.ReadUntil("op_1", p => Clips.IndexIn(p, 64) == frame);
            Assert.Equal(frame, Clips.IndexIn(px, 64));
        }
        Assert.Equal(1, files.Opened);
    }

    [Fact]
    public async Task APlaylistSwitchesToItsNextItemOnTheFirstFrameAfterTheSwitch()
    {
        // The same one-second clip twice: the first item ends into the second, which is prepared ahead, so the
        // first frame after the switch is the second item's frame 0, not the first item's last frame held.
        var clip = Clips.Counted(1);
        var media = new MediaItem("md_clip", "Counted", MediaKind.Video, clip, "", new FileInfo(clip).Length,
            MediaInfo.Unknown with { DurationNs = 1_000_000_000, Width = 64, Height = 48, FrameRate = "25" }, []);
        var first = PlaylistItem.For("it_a", SourceRef.Media("md_clip"), "A");
        var second = PlaylistItem.For("it_b", SourceRef.Media("md_clip"), "B") with { End = EndAction.Hold };
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Playlist("pl_show"), 64, 48)) with
        {
            Media = [media],
            Playlists = [new Playlist("pl_show", "Show", [first, second], false, null)],
        };
        var files = new MediaFiles();
        await using var rig = HostRig.Start(project, files);
        await rig.Ok("PLAYLIST GO pl_show");
        rig.Clock.NowNs = 5_000_000_000 + 950_000_000;
        await rig.ReadUntil("op_1", p => Clips.IndexIn(p, 64) == 23);
        Assert.Equal(2, files.Opened);
        await rig.Host.FramesAsync(20).WaitAsync(HostRig.Patience);

        rig.Clock.NowNs = 5_000_000_000 + 1_020_000_000;
        Assert.Equal(0, Clips.IndexIn(await rig.Read("op_1"), 64));
        rig.Clock.NowNs = 5_000_000_000 + 1_500_000_000;
        await rig.ReadUntil("op_1", p => Clips.IndexIn(p, 64) == 12);
    }

    [Fact]
    public async Task BlackoutWinsAndAPatternCoversOnlyTheOutputItNames()
    {
        var project = HostRig.WithOutputs(HostRig.Screen("op_a", SourceRef.Solid("#0000FF"), 64, 36), HostRig.Screen("op_b", SourceRef.Solid("#00FF00"), 64, 36));
        await using var rig = HostRig.Start(project);
        Assert.Equal((0, 0, 255), HostRig.At(await rig.Read("op_a"), 64, 10, 10));

        await rig.Ok("BLACKOUT ON");
        await rig.ReadUntil("op_a", p => HostRig.At(p, 64, 10, 10) == (0, 0, 0));
        Assert.Equal((0, 0, 0), HostRig.At(await rig.Read("op_b"), 64, 10, 10));
        await rig.Ok("BLACKOUT OFF");

        await rig.Ok("PATTERN flat ON op_b");
        await rig.ReadUntil("op_b", p => HostRig.At(p, 64, 32, 18) == (255, 255, 255));
        Assert.Equal((0, 0, 255), HostRig.At(await rig.Read("op_a"), 64, 32, 18));
        await rig.Ok("PATTERN OFF");
        await rig.ReadUntil("op_b", p => HostRig.At(p, 64, 32, 18) == (0, 255, 0));
    }

    [Fact]
    public async Task AScanFrameReachesOneOutputRawAndSaysWhenItWasPresented()
    {
        const int w = 64, h = 36;
        var project = HostRig.WithOutputs(
            HostRig.Screen("op_a", SourceRef.Solid("#0000FF"), w, h) with { Color = ColorTrim.Neutral with { Gamma = 1.8 }, Rotation = 180 },
            HostRig.Screen("op_b", SourceRef.Solid("#00FF00"), w, h));
        await using var rig = HostRig.Start(project);
        await rig.Read("op_a");
        var gray = new byte[w * h];
        for (var i = 0; i < gray.Length; i++)
            gray[i] = (byte)(i * 13 % 256);

        var before = MonoClock.Instance.NowNs;
        var presented = await rig.Host.ShowAsync("op_a", gray, w, h, CancellationToken.None).WaitAsync(HostRig.Patience);
        var after = MonoClock.Instance.NowNs;
        Assert.InRange(presented, before, after);

        // Code for code at the same pixel: no trim, no rotation, no dither — and the other output keeps the show.
        var px = await rig.Read("op_a");
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Assert.Equal((gray[y * w + x], gray[y * w + x], gray[y * w + x]), HostRig.At(px, w, x, y));
        Assert.Equal((0, 255, 0), HostRig.At(await rig.Read("op_b"), w, 5, 5));

        // A frame of another raster, or one for an output that is not there, is refused with the reason.
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Host.ShowAsync("op_a", gray, 32, 18, CancellationToken.None));
        Assert.Contains("raster is 64×36", wrong.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Host.ShowAsync("op_x", gray, w, h, CancellationToken.None));

        rig.Host.Release();
        await rig.ReadUntil("op_a", p => HostRig.At(p, w, 5, 5).B > 200 && HostRig.At(p, w, 5, 5).G == 0);
    }

    [Fact]
    public async Task AScanFrameOnAWindowedOutputIsTimedByItsSwap()
    {
        const int w = 64, h = 36;
        var probe = RenderHost.TryCreate(RenderHostOptions.Default, out var why) ?? throw new InvalidOperationException(why);
        var display = probe.Displays()[0].Key;
        await probe.DisposeAsync();
        var project = HostRig.WithOutputs(HostRig.Screen("op_w", SourceRef.Solid("#FF0000"), w, h, OutputKind.Window, display));
        await using var rig = HostRig.Start(project, options: RenderHostOptions.Default);
        var waited = Stopwatch.StartNew();
        while (rig.Host.Health() is not [{ Open: true }])
        {
            Assert.True(waited.Elapsed < HostRig.Patience, "the window never opened");
            await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        }
        Assert.Equal(display, rig.Host.Health()[0].Display);
        Assert.Equal(1, rig.Host.Stats.Windows);

        var gray = new byte[w * h];
        Array.Fill(gray, (byte)77);
        var before = MonoClock.Instance.NowNs;
        var presented = await rig.Host.ShowAsync("op_w", gray, w, h, CancellationToken.None).WaitAsync(HostRig.Patience);
        Assert.InRange(presented, before, MonoClock.Instance.NowNs);
        Assert.Equal((77, 77, 77), HostRig.At(await rig.Read("op_w"), w, 20, 20));

        // Blackout wins over a scan frame, and the frame says so rather than claiming a present.
        await rig.Ok("BLACKOUT ON");
        await rig.ReadUntil("op_w", p => HostRig.At(p, w, 20, 20) == (0, 0, 0));
        var dark = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Host.ShowAsync("op_w", gray, w, h, CancellationToken.None).WaitAsync(HostRig.Patience));
        Assert.Contains("blacked out", dark.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovingOneWarpNodeRebuildsTheWarpWithoutUploadingAMeshAgain()
    {
        var project = BlendSolveTests.TwoProjectorsOnAWall();
        project = project with
        {
            Outputs = [.. project.Outputs.Select(o => o with
            {
                Width = 320,
                Height = 180,
                Warp = new OutputWarp(true, CornerPin.None, WarpLattice.AtRest(3, 3)) with { Pin = CornerPin.None with { TopLeft = new Vec2d(0.02, 0) } },
            })],
        };
        await using var rig = HostRig.Start(project);
        await rig.Host.Stage!.WhenSettledAsync(rig.Engine.Store.Current.Revision).WaitAsync(HostRig.Patience);
        await rig.Read("op_left");
        var meshes = await rig.Host.InvokeAsync(r => r.MeshUploads);
        var warps = await rig.Host.InvokeAsync(r => r.WarpUploads);
        var builds = rig.Host.Stage.WarpBuilds;
        Assert.True(meshes >= 1, "the wall was drawn");
        Assert.Equal(2, warps);

        await rig.Ok("WARP NODE op_left 1 1 0.03 -0.02");
        await rig.Host.Stage.WhenSettledAsync(rig.Engine.Store.Current.Revision).WaitAsync(HostRig.Patience);
        await rig.Read("op_left");
        await rig.Read("op_left");

        Assert.Equal(builds + 1, rig.Host.Stage.WarpBuilds);
        Assert.Equal(warps + 1, await rig.Host.InvokeAsync(r => r.WarpUploads));
        Assert.Equal(meshes, await rig.Host.InvokeAsync(r => r.MeshUploads));
    }

    [Fact]
    public async Task AWarmStaticFrameAllocatesNothing()
    {
        await using var rig = HostRig.Start(Project.CreateDefault("Static"));
        await rig.Host.FramesAsync(40).WaitAsync(HostRig.Patience);
        var before = rig.Host.Stats;
        await rig.Host.FramesAsync(90).WaitAsync(HostRig.Patience);
        var after = rig.Host.Stats;
        log.WriteLine($"{after.Frames - before.Frames} warm frames: {after.FramePathAllocatedBytes - before.FramePathAllocatedBytes} bytes on the frame path; {after.FrameMs:0.00} ms mean, {after.FrameMsP95:0.00} ms p95");
        Assert.True(after.Frames - before.Frames >= 90);
        Assert.Equal(before.FramePathAllocatedBytes, after.FramePathAllocatedBytes);
    }

    [Fact]
    public void WithNoDisplayTryCreateAnswersNullAndSaysWhy()
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", null);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            var host = RenderHost.TryCreate(RenderHostOptions.Default, out var why);
            Assert.Null(host);
            Assert.Contains("no display", why, StringComparison.Ordinal);
            Assert.Contains("DISPLAY", why, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", display);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", wayland);
        }
    }
}
