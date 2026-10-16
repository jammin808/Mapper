using System.Security.Cryptography;
using PlayMap.Core.Align;
using PlayMap.Core.Fleet;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>What a hot backup pulls besides media, two real nodes on loopback: the files the show names beside its project folder — a
/// mesh read from a file, a measured warp map, a survey's thinned cloud — land in the backup's folder for them before the show that
/// names them is applied, verified by the hash the show names, so the backup's stage loads the primary's mesh; the primary serves only
/// what its show names, and only to a proof that is no web page's. A draft on the primary never reaches the backup; its landing does.
/// A live input is never fetched. A stopping node starts no pull.</summary>
public sealed class BackupShowFilesTests
{
    private const long Second = SyncRig.Second;

    [Fact]
    public async Task TheBackupPullsTheShowsFilesBesideItBeforeTheShowAndItsStageLoadsThem()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);

        // Beside the primary's show: a wall mesh, a measured warp and a survey's cloud, each written where the show names it.
        var folder = primary.Dir.FullName;
        var meshBytes = Write(folder, "meshes/ms00000001-wall.ply", s => MeshIO.WritePly(s, Primitives.Box(4, 3, 0.2)));
        var warpBytes = Pfm.Write(new FloatMap(3, 2, 3, [0, 0, 0, 0.5f, 0, 0, 1, 0, 0, 0, 1, 0, 0.5f, 1, 0, 1, 1, 0]));
        Write(folder, "align/run1/warp-op_1.pfm", s => s.Write(warpBytes));
        Write(folder, "clouds/sv00000001.ply", s => s.Write("ply\nformat ascii 1.0\nend_header\n"u8));
        var mesh = new MeshAsset("ms00000001", "Wall", MeshOrigin.Imported, "meshes/ms00000001-wall.ply",
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(meshBytes)), "", ParamSet.Empty, 8, 12);
        var survey = new SurveySource("sv00000001", "North front", SurveyFormat.Ply, "/survey/north.ply", "", "LOCAL", 8, 0, mesh.Id,
            "clouds/sv00000001.ply", 0.05, 0, SurveyState.Ready, 1_760_000_000_000, "");
        var p = SyncRig.Show();
        var show = p with
        {
            Stage = p.Stage with { Meshes = [mesh], Surveys = [survey] },
            Outputs = [p.Outputs[0] with
            {
                Measured = new MeasuredCorrection(new MeasuredWarp(3, 2, "align/run1/warp-op_1.pfm", Pfm.Hash(warpBytes), 0.4), null, null, null, 0.4, "", "", "run1", 1),
            }],
        };
        await SyncRig.OpenShowAsync(primary, show);
        Assert.Equal(3, ShowFiles.Of(primary.Engine.Store.Project).Count);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);

        // The backup has no project folder of its own: the files are in its folder for its primary's show, each as the show names it.
        var kept = backup.Engine.ProjectFolderForFiles;
        Assert.Equal(Path.Combine(backup.Dir.FullName, "mirror", primary.Id), kept);
        Assert.Equal(meshBytes, await File.ReadAllBytesAsync(Path.Combine(kept!, "meshes/ms00000001-wall.ply")));
        Assert.Equal(warpBytes, await File.ReadAllBytesAsync(Path.Combine(kept!, "align/run1/warp-op_1.pfm")));
        Assert.True(File.Exists(Path.Combine(kept!, "clouds/sv00000001.ply")));
        Assert.Contains("meshes/ms00000001-wall.ply pulled from FOH-A with proof over HTTPS and verified", backup.Journal(), StringComparison.Ordinal);
        // Its stage reads the primary's mesh.
        var loaded = backup.Engine.Meshes.Get(backup.Engine.Store.Project.Stage.Meshes.Find(mesh.Id)!);
        Assert.NotNull(loaded);
        Assert.Equal(12, loaded!.TriangleCount);

        // A file that changes on the primary is pulled again with the show's next revision (its hash moved).
        var wider = Write(folder, "meshes/ms00000001-wall-2.ply", s => MeshIO.WritePly(s, Primitives.Box(6, 3, 0.2)));
        primary.Engine.Store.Apply("a wider wall", x => x with
        {
            Stage = x.Stage with
            {
                Meshes = [x.Stage.Meshes[0] with { Path = "meshes/ms00000001-wall-2.ply", ContentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(wider)) }],
            },
        });
        Assert.True(await Eventually.TrueAsync(() => BackupRig.ShowJson(backup) == BackupRig.ShowJson(primary) && BackupRig.MirrorState(backup) == "identical"),
            BackupRig.Fleet(backup).ToString());
        Assert.Equal(wider, await File.ReadAllBytesAsync(Path.Combine(kept!, "meshes/ms00000001-wall-2.ply")));

        // The primary serves only what its show names, and only to a proof: never to a web page, whatever it carries.
        using var http = primary.Http();
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.GetAsync(new Uri(MirrorWebModule.FilePath + "mesh/" + mesh.Id, UriKind.Relative))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.SendAsync(Signed(primary, MirrorWebModule.FilePath + "mesh/ms0000000f"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.SendAsync(Signed(primary, MirrorWebModule.FilePath + "warp/op_1"))).StatusCode);
        using var page = Signed(primary, MirrorWebModule.FilePath + "warp/op_1");
        page.Headers.Add("Origin", "http://evil.example");
        var fromPage = await http.SendAsync(page);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, fromPage.StatusCode);
        Assert.Contains("never from a web page", await fromPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADraftNeverMirrorsItsLandingDoesAndALiveInputIsNeverFetched()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        var show = SyncRig.Show() with
        {
            Media = [new MediaItem("md_cam", "Cam 1", MediaKind.Stream, "ndi:STUDIO (Cam 1)", "", 0, MediaInfo.Unknown, ["live"])],
        };
        await SyncRig.OpenShowAsync(primary, show);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);
        var name = () => backup.Engine.Store.Project.FindOutput("op_1")!.Name;
        var before = name();

        // The primary's operator edits blind: the draft stays on the primary.
        await primary.OkAsync("BLIND ON");
        await primary.OkAsync("SET op_1 name Drafted");
        var revision = primary.Engine.Store.Current.Revision;
        Assert.Equal(before, primary.Engine.Store.Project.FindOutput("op_1")!.Name);
        clock.Advance(Second);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.MirrorState(backup) == "identical"));
        Assert.Equal(before, name());
        Assert.Equal(revision, BackupRig.Fleet(backup).GetProperty("mirror").GetProperty("rev").GetInt64());
        // Its landing is a revision of the show like any edit: the backup takes it.
        await primary.OkAsync("BLIND COMMIT NOW");
        clock.Advance(Second);   // past the commit's lead
        Assert.True(await Eventually.TrueAsync(() => primary.Engine.Store.Project.FindOutput("op_1")!.Name == "Drafted"));
        Assert.True(await Eventually.TrueAsync(() => name() == "Drafted"), BackupRig.Fleet(backup).ToString());
        // A backup neither begins nor lands a draft over its primary's show.
        Assert.Contains("FOH-B mirrors FOH-A", (await backup.RunAsync("BLIND ON")).Payload, StringComparison.Ordinal);
        Assert.Contains("FOH-B mirrors FOH-A", (await backup.RunAsync("BLIND COMMIT")).Payload, StringComparison.Ordinal);

        // A live input is received, never fetched; its address is not said (it may carry a password).
        var live = await backup.RunAsync("MEDIA FETCH md_cam");
        Assert.False(live.Ok);
        Assert.Equal("Cam 1 is a live input: it is received from the network, never fetched", live.Payload);
    }

    [Fact]
    public async Task AStoppingNodeStartsNoPull()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var node = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, new ManualTime());
        var clip = new MediaItem("md_clip", "Clip", MediaKind.Video, "/nowhere/clip.mp4", "sha256:" + new string('a', 64), 10, MediaInfo.Unknown, []);
        await SyncRig.OpenShowAsync(node, SyncRig.Show() with { Media = [clip] });
        // Even the node's own line: a pull would outlive the stop.
        Assert.Contains("no other PlayMap node is heard", (await Own(node, "MEDIA FETCH md_clip")).Payload, StringComparison.Ordinal);
        node.Engine.BeginStop();
        var stopping = await Own(node, "MEDIA FETCH md_clip");
        Assert.False(stopping.Ok);
        Assert.Equal(BackupNode.StoppingWords, stopping.Payload);
        // A client's line is refused at the door, as every line is once the node stops.
        Assert.Equal(EngineCore.StoppingWords, (await node.RunAsync("MEDIA FETCH md_clip")).Payload);
    }

    private static async Task<CommandReply> Own(HostRig rig, string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return await rig.Engine.HandleAsync(line, EngineCore.SystemSession, CancellationToken.None);
    }

    private static byte[] Write(string folder, string relative, Action<Stream> write)
    {
        var full = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var s = File.Create(full))
            write(s);
        return File.ReadAllBytes(full);
    }

    private static HttpRequestMessage Signed(HostRig rig, string path)
    {
        var nonce = PullProof.NewNonce(rig.Engine.Services.Wall.UnixMs);
        var r = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        r.Headers.Add(PullProof.NonceHeader, nonce);
        r.Headers.Add(PullProof.ProofHeader, PullProof.Sign(HostRig.Token, nonce, "GET", "http", path));
        return r;
    }
}
