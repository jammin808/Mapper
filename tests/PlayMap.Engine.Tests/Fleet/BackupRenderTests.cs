using System.Diagnostics;
using System.Text.Json;
using PlayMap.Core.Led;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using Xunit.Abstractions;
using FfmpegFactAttribute = PlayMap.Engine.Tests.Host.FfmpegFactAttribute;
using Pictures = PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>The hot backup's picture, two real nodes each with a render host on llvmpipe: the primary plays a clip whose optimised
/// HAP Q version it holds; the backup pulls the show, the clip and the version by hash, plays the same version (STATE.decode's codec),
/// and its output read back is byte-equal to the primary's at the same leader instants — playing, under a blackout, and through a
/// panic fade. The show has an LED strip sampling the output and a send of it: the primary samples and packs every frame (both read
/// the output's final pixels, dither and all), the silent backup neither, and the pictures stay byte-equal.</summary>
[Collection("render host")]
public sealed class BackupRenderTests(ITestOutputHelper log)
{
    private const long Second = SyncRig.Second;

    private static string Encode(string folder, string name, params string[] codec)
    {
        var path = Path.Combine(folder, name);
        var painter = "geq=lum='if(lt(Y,24),if(mod(floor(N/pow(2,floor(X/8))),2),235,16),128)':cb=128:cr=128";
        var psi = new ProcessStartInfo(Pictures.Clips.Ffmpeg()) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x48:r=25:d=3", "-vf", painter }.Concat(codec).Append(path))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && File.Exists(path), "ffmpeg: " + err);
        return path;
    }

    private static async Task<string> ImportAsync(HostRig rig, string path, string name)
    {
        var import = await rig.RunAsync("MEDIA IMPORT " + WireGrammar.Quote(path) + " " + name);
        Assert.True(import.Ok, import.Payload);
        string id;
        using (var accepted = JsonDocument.Parse(import.Payload!))
            id = accepted.RootElement.GetProperty("id").GetString()!;
        Assert.True(await Eventually.TrueAsync(() => rig.Engine.Store.Project.FindMedia(id)?.ContentHash.Length > 0, 30_000), rig.Journal());
        return id;
    }

    private static async Task<byte[]> ReadAsync(HostRig rig)
    {
        var render = Assert.IsType<RenderHost>(rig.Host.Render);
        await render.FramesAsync(2).WaitAsync(Pictures.HostRig.Patience);
        return await render.ReadOutputAsync("op_1").WaitAsync(Pictures.HostRig.Patience) ?? throw new InvalidOperationException("op_1 drew nothing");
    }

    /// <summary>Both outputs at one leader instant, once each has drawn what that instant shows (a decoder opening may take frames).</summary>
    private static async Task<(byte[] A, byte[] B)> BothAt(ManualClock clock, long localNs, HostRig a, HostRig b, Func<byte[], bool> good)
    {
        clock.NowNs = localNs;
        // The hand-moved clock jumps under the clock exchange; the backup reads the primary's instant again once its estimate settles.
        Assert.True(await Eventually.TrueAsync(() => b.Engine.LeaderNowNs == a.Engine.LeaderNowNs, 20_000), $"the backup reads {b.Engine.LeaderNowNs}, the primary {a.Engine.LeaderNowNs}");
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var pa = await ReadAsync(a);
            var pb = await ReadAsync(b);
            if ((good(pa) && good(pb) && pa.AsSpan().SequenceEqual(pb)) || waited.Elapsed > Pictures.HostRig.Patience)
                return (pa, pb);
        }
    }

    /// <summary>One 16-pixel strip across the output's middle row, patched to a DDP bar on this machine.</summary>
    private static LedRig Strip(string output)
    {
        const string Node = "ln00000001";
        return new LedRig
        {
            Nodes = [new LedNode(Node, "Bar", LedProtocol.Ddp, "127.0.0.1", 0, true, 0, 0, LedSync.Latch, 510, "#FFFFFF")],
            Fixtures =
            [
                new LedFixture("lf00000001", "Strip", "builtin:rgb", LedSpace.Output, output, Transform3.Identity with { Position = new Vec3d(0, 24, 0) },
                    new LedLayout(LedShape.Line, 16, 1, new Vec3d(63, 0, 0), LedStart.TopLeft, false, false, []), new LedPatch(Node, 0, 1),
                    LedSampling.Point, 0, LedColor.Neutral, true, false, false, "#FF8800", []),
            ],
        };
    }

    private static string? Codec(HostRig rig, string media) =>
        rig.State().GetProperty("decode").GetProperty("sources").EnumerateArray()
            .Where(s => s.GetProperty("media").GetString() == media).Select(s => s.GetProperty("codec").GetString()).FirstOrDefault();

    [FfmpegFact]
    public async Task TheBackupPlaysTheSameVersionAndItsPictureIsTheSameBytesAtTheSameInstants()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-backup-render-");
        try
        {
            var source = Encode(dir.FullName, "counted.mp4", "-c:v", "mpeg4", "-q:v", "1", "-g", "25", "-pix_fmt", "yuv420p");
            var hap = Encode(dir.FullName, "counted-hapq.mov", "-c:v", "hap", "-format", "hap_q");
            var clock = new ManualClock(1_000 * Second);
            var time = new ManualTime();
            var parts = BackupRig.Parts(time) with { RenderHost = RenderHost.ForNode };
            var ledA = new RecordingLed();
            var ledB = new RecordingLed();
            using var ndiA = new VideoIo.FakeVideoProvider(LiveProtocol.Ndi, clock);
            using var ndiB = new VideoIo.FakeVideoProvider(LiveProtocol.Ndi, clock);
            await using var primary = await HostRig.StartAsync("FOH-A", network: true, mono: new OffsetClock(clock, 2 * Second), parts: NetworkEdges.With(parts, ledA, ndiA),
                settings: s => s with { NodeId = BackupRig.PrimaryId, TrustLoopback = false, Led = s.Led with { Output = true } });
            await using var backup = await HostRig.StartAsync("FOH-B", network: true, mono: clock, parts: NetworkEdges.With(parts, ledB, ndiB),
                settings: s => s with { NodeId = BackupRig.BackupId, TrustLoopback = false, Led = s.Led with { Output = true } });
            var show = Pictures.HostRig.WithOutputs(Pictures.HostRig.Screen("op_1", SourceRef.Playlist("pl_show"), 64, 48)) with
            {
                Playlists = [new Playlist("pl_show", "Show", [], false, "show")],
                Leds = Strip("op_1"),
            };
            await SyncRig.OpenShowAsync(primary, show);

            // The primary holds the clip and its HAP Q version (as optimise records a version: keyed by the source's hash and recipe).
            var clip = await ImportAsync(primary, source, "Counted");
            var made = await ImportAsync(primary, hap, "Made");
            primary.Engine.Store.Apply("the clip's HAP Q version", p =>
            {
                var item = p.FindMedia(clip)!;
                var file = p.FindMedia(made)!;
                var recipe = Recipe.For("hap-q", EncodeQuality.Show);
                var version = new MediaVersion("mv_1", "hap-q", recipe.Canonical(), recipe.Key(item.ContentHash), file.ContentHash, file.SizeBytes,
                    file.Info, new VersionQuality(44, 43, 3, 0), "hapenc/1", BackupRig.PrimaryId, 1);
                return p with
                {
                    Media = p.Media.Update(clip, m => m with { Versions = [version] }),
                    Playlists = [p.Playlists[0] with { Items = [PlaylistItem.For("it_a", SourceRef.Media(clip), "Counted") with { DurationNs = 3 * Second }] }],
                };
            });
            await primary.OkAsync("SEND ADD NDI Main output:op_1 ALWAYS");
            await primary.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(primary, backup);
            await BackupRig.BackUpAsync(primary, backup);
            Assert.NotNull(backup.Engine.Services.Media!.PathForHash(primary.Engine.Store.Project.FindMedia(clip)!.Versions[0].ContentHash));

            await primary.OkAsync("PLAYLIST GO pl_show");
            Assert.True(await Eventually.TrueAsync(() => backup.Engine.AnchorJson("pl_show") == primary.Engine.AnchorJson("pl_show")), backup.Journal());
            var start = clock.NowNs;
            foreach (var frame in new[] { 12, 37, 61 })
            {
                var (a, b) = await BothAt(clock, start + (long)((frame + 0.5) * 40_000_000), primary, backup, px => Pictures.Clips.IndexIn(px, 64) == frame);
                log.WriteLine($"frame {frame}: primary {Pictures.Clips.IndexIn(a, 64)}, backup {Pictures.Clips.IndexIn(b, 64)}");
                Assert.Equal(frame, Pictures.Clips.IndexIn(a, 64));
                Assert.True(a.AsSpan().SequenceEqual(b), $"frame {frame}: the two outputs differ");
            }
            // The same optimised version plays on both.
            Assert.True(await Eventually.TrueAsync(() => Codec(primary, clip) is { } c && c == Codec(backup, clip)), primary.State().GetProperty("decode").ToString());
            Assert.Equal("hap_q", Codec(backup, clip));

            // A blackout: black on both at the same instants.
            await primary.OkAsync("BLACKOUT ON");
            var dark = clock.NowNs + Second;   // the clocks only move forward: a show clock never runs backwards
            var (ba, bb) = await BothAt(clock, dark, primary, backup, px => Pictures.HostRig.At(px, 64, 32, 30) == (0, 0, 0));
            Assert.Equal((0, 0, 0), Pictures.HostRig.At(ba, 64, 32, 30));
            Assert.True(ba.AsSpan().SequenceEqual(bb));
            await primary.OkAsync("BLACKOUT OFF");

            // A panic fade, read at the same instants across it on both.
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            primary.Engine.EventRaised += e => events.Enqueue("A " + e.Kind + " " + e.AtLeaderNs + " " + e.FieldsJson);
            backup.Engine.EventRaised += e => events.Enqueue("B " + e.Kind + " " + e.AtLeaderNs + " " + e.FieldsJson);
            await primary.OkAsync("PANIC FADE 1000");
            var panicked = clock.NowNs;
            Assert.True(await Eventually.TrueAsync(() => events.Count(e => e.Contains(" panic ", StringComparison.Ordinal)) == 2), string.Join("\n", events));
            Assert.Single(events.Select(e => e.Split(' ')[2]).Distinct());   // the panic took the same leader instant on both
            foreach (var ms in new[] { 150, 450, 750, 1200 })
            {
                var (pa, pb) = await BothAt(clock, panicked + ms * 1_000_000L, primary, backup, _ => true);
                var diff = Enumerable.Range(0, pa.Length).Where(i => pa[i] != pb[i]).ToList();
                log.WriteLine($"+{ms} ms: centre {Pictures.HostRig.At(pa, 64, 32, 30)} / {Pictures.HostRig.At(pb, 64, 32, 30)}; {diff.Count} bytes differ, at most {(diff.Count == 0 ? 0 : diff.Max(i => Math.Abs(pa[i] - pb[i])))}, first at pixel {(diff.Count == 0 ? -1 : diff[0] / 4)}");
                Assert.True(pa.AsSpan().SequenceEqual(pb), $"+{ms} ms into the panic fade the two outputs differ");
            }
            Assert.Equal((0, 0, 0), Pictures.HostRig.At((await BothAt(clock, panicked + 1_200_000_000, primary, backup, _ => true)).A, 64, 32, 30));

            // All the while the primary sampled its LED strip and packed its send from the same final pixels; the backup did neither.
            Assert.True(primary.Engine.Led.Pass?.Frames > 0, "the primary's LED pass sampled nothing");
            Assert.True(Assert.Single(ndiA.Sinks).Frames.Count > 0, "the primary's send carried no frame");
            Assert.Equal(0, backup.Engine.Led.Pass?.Frames ?? 0);
            Assert.Empty(ndiB.Sinks);
            Assert.False(ledB.Master);
        }
        finally
        {
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException)
            {
                log.WriteLine("the clips' folder stays in the private temp folder");
            }
        }
    }
}
