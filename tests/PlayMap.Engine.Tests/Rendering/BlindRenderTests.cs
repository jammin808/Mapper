using System.Diagnostics;
using System.Text.Json;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Rendering;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>Blind edit on the render host (Mesa is enough): the editor's draft view shows the draft's colour while the air's editor
/// view and the output, drawn at the same leader instant, show the live colour; a commit at the playlist's next switch reads the
/// old colour on the output one frame before the instant and the new colour at it.</summary>
[Collection("render host")]
public sealed class BlindRenderTests(ITestOutputHelper log)
{
    private const long Frame = 16_666_667;

    /// <summary>A wall facing the editor's eye showing canvas cv_main (green, no layers), an output showing the same canvas, and a
    /// playlist of two ten-second items to commit at.</summary>
    private static Project Scene()
    {
        var p = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Canvas("cv_main"), 64, 48));
        var wall = new MeshAsset("ms_wall", "Wall", MeshOrigin.Primitive, "", "", "plane",
            ParamSet.Empty.With("width", 6.0).With("height", 4.0).With("segments", 2), 0, 0);
        var surface = new Surface("sf_wall", "Wall", "ms_wall", Transform3.Identity, [],
            SurfaceContent.Empty with { Source = SourceRef.Canvas("cv_main") }, 1, [], true, false, "#ffffff", []);
        PlaylistItem Item(string id) => PlaylistItem.For(id, SourceRef.Generator(p.Generators[0].Id), id) with { DurationNs = 10_000_000_000 };
        return p with
        {
            Canvases = p.Canvases.Update("cv_main", c => c with { Background = "#00FF00", Layers = [] }),
            Stage = Stage.Empty with { Surfaces = [surface], Meshes = [wall] },
            Playlists = [new Playlist("pl_show", "Show", [Item("it_a"), Item("it_b")], false, null)],
        };
    }

    private static string Eye() => JsonSerializer.Serialize(new
    {
        intrinsics = JsonDocument.Parse(ProjectJson.SerializeEntity(Intrinsics.FromFieldOfView(160, 90, 40))).RootElement,
        pose = JsonDocument.Parse(ProjectJson.SerializeEntity(Pose.LookAt(new Vec3d(0, 0, 6), Vec3d.Zero, Vec3d.UnitY))).RootElement,
    });

    /// <summary>The centre of a view's newest picture, waiting until it satisfies the condition.</summary>
    private static async Task<(int R, int G, int B)> ViewCentre(HostRig rig, string view, Func<(int R, int G, int B), bool> good)
    {
        var path = rig.Engine.Previews.RingPath(view);
        var waited = Stopwatch.StartNew();
        var seen = (0, 0, 0);
        while (waited.Elapsed < HostRig.Patience)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var reader = new PreviewRingReader(path);
                    var buffer = new byte[reader.MaxWidth * reader.MaxHeight * 4];
                    if (reader.TryRead(buffer, -1, out var info))
                    {
                        seen = HostRig.At(buffer, info.Width, info.Width / 2, info.Height / 2);
                        if (good(seen))
                            return seen;
                    }
                }
                catch (InvalidDataException)
                {
                    // Being made: the next look finds it.
                }
            }
            await rig.Host.FramesAsync(1).WaitAsync(HostRig.Patience);
        }
        Assert.Fail($"{view} never showed what was expected (last {seen})");
        return seen;
    }

    private static bool Red((int R, int G, int B) p) => p.R > 150 && p.G < 60;

    /// <summary>The newest picture's sequence in a view's ring (−1 before the first).</summary>
    private static long Sequence(HostRig rig, string view)
    {
        using var reader = new PreviewRingReader(rig.Engine.Previews.RingPath(view));
        var buffer = new byte[reader.MaxWidth * reader.MaxHeight * 4];
        return reader.TryRead(buffer, -1, out var info) ? info.Sequence : -1;
    }

    private static bool Green((int R, int G, int B) p) => p.G > 150 && p.R < 60;

    [Fact]
    public async Task TheDraftViewShowsTheDraftWhileTheAirShowsTheLiveProject()
    {
        await using var rig = HostRig.Start(Scene());
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        await rig.Ok("VIEW draft CAMERA " + Eye());
        await rig.Ok("VIEW draft SHOW editor DRAFT");
        await rig.Ok("VIEW live CAMERA " + Eye());
        await rig.Ok("VIEW live SHOW editor 160x90");

        var draft = await ViewCentre(rig, "draft", Red);
        var live = await ViewCentre(rig, "live", Green);
        var air = HostRig.At(await rig.Read("op_1"), 64, 32, 24);
        log.WriteLine($"draft view {draft}, editor view {live}, output {air}");
        Assert.Equal((0, 255, 0), air);
        Assert.True(Red(draft) && Green(live));

        // A second draft view; hiding one while the other draws never faults the other (its ring outlives the reads in flight).
        await rig.Ok("VIEW draft2 CAMERA " + Eye());
        await rig.Ok("VIEW draft2 SHOW editor DRAFT");
        await ViewCentre(rig, "draft2", Red);
        Assert.True((await rig.Run("VIEW draft HIDE")).Ok);
        var before = Sequence(rig, "draft2");
        await rig.Host.FramesAsync(40).WaitAsync(HostRig.Patience);
        Assert.True(Sequence(rig, "draft2") > before, "the remaining draft view stopped drawing");
        Assert.DoesNotContain(rig.Engine.Journal.Recent(50), r => r.Kind == "fault");

        // A view id that has drawn the air never becomes a draft view (its ring is still mapped); another id does.
        Assert.Equal("live has drawn the air — a draft view takes a view id of its own (VIEW live-draft SHOW editor DRAFT)",
            (await rig.Run("VIEW live SHOW editor DRAFT")).Payload);

        // With the draft discarded a draft view shows the air.
        Assert.True((await rig.Run("VIEW draft2 HIDE")).Ok);
        await rig.Ok("BLIND DISCARD");
        await rig.Ok("VIEW again CAMERA " + Eye());
        await rig.Ok("VIEW again SHOW editor DRAFT");
        Assert.True(Green(await ViewCentre(rig, "again", Green)));
    }

    /// <summary>A draft view hidden and shown again at once: the draft renderer's ring for the old view still retires while the new
    /// view's is made in a file of its own (one writer per ring), and the view's own ring is a new file too; the new view shows the
    /// draft.</summary>
    [Fact]
    public async Task ADraftViewShownAgainAtOnceWritesNewRingsWhileTheOldOneRetires()
    {
        await using var rig = HostRig.Start(Scene());
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        await rig.Ok("VIEW draft CAMERA " + Eye());
        await rig.Ok("VIEW draft SHOW editor DRAFT");
        await ViewCentre(rig, "draft", Red);
        var first = rig.Engine.Previews.RingPath("draft");
        var privateRings = Path.Combine(PreviewRing.DefaultFolder(), $"playmap-{rig.Engine.Services.Settings.NodeId}-draft~draft~*.preview");
        string[] Private() => Directory.GetFiles(Path.GetDirectoryName(privateRings)!, Path.GetFileName(privateRings));
        var firstPrivate = Assert.Single(Private());

        // The render thread is held, so the old view's ring cannot retire before the new one is made: hidden and shown within a frame.
        using var hold = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = rig.Host.InvokeAsync(_ =>
        {
            entered.SetResult();
            return hold.Wait(HostRig.Patience);
        });
        await entered.Task;
        try
        {
            await rig.Ok("VIEW draft HIDE");
            await rig.Ok("VIEW draft SHOW editor DRAFT");
            var second = rig.Engine.Previews.RingPath("draft");
            Assert.NotEqual(first, second);
            // The new view's own ring is a file of its own, made while the old one still retires (the draft renderer may yet
            // deliver a read into it): never the same file. (The old file's name may be gone already — unlinked under its maps on
            // Linux — or kept while mapped on Windows.)
            var rings = Private();
            Assert.Contains(rings, r => r != firstPrivate);
        }
        finally
        {
            hold.Set();
        }
        Assert.True(await held);
        await ViewCentre(rig, "draft", Red);
    }

    [Fact]
    public async Task ACommitAtTheNextSwitchChangesTheOutputOnTheSwitchFrame()
    {
        // The frame's instant runs a frame ahead of the lane's (a display latency): the frame that reaches the boundary draws the
        // commit while the lane, a frame behind, has not made it the document — only the per-frame choice can turn it red.
        await using var rig = HostRig.Start(Scene(), options: RenderHostOptions.Default with { Windows = false, DisplayLatencyNs = Frame });
        var t = rig.Engine.LeaderNowNs;
        await rig.Ok("PLAYLIST GO pl_show");
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        await rig.Ok("BLIND COMMIT NEXT pl_show");
        var at = rig.Engine.Snapshot.Pending!.EffectiveLeaderNs;
        Assert.Equal(t + 10_000_000_000, at);

        // The frame one frame before the boundary keeps the air; the frame at it draws the draft.
        rig.Clock.NowNs = at - Frame - Frame / 2;
        Assert.Equal((0, 255, 0), HostRig.At(await rig.Read("op_1"), 64, 32, 24));
        rig.Clock.NowNs = at - Frame / 2;
        Assert.Equal((255, 0, 0), HostRig.At(await rig.Read("op_1"), 64, 32, 24));
        Assert.Equal("#00FF00", rig.Engine.Store.Project.Canvases[0].Background);
        rig.Clock.NowNs = at;
        Assert.True(await Show.ShowRig.Eventually(() => rig.Engine.Store.Project.Canvases[0].Background == "#FF0000"));
    }
}
