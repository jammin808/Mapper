using System.Globalization;
using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Preflight's backup rows and INDEX's fleet section with faults injected: an optimised version no machine holds leaves the
/// backup's files amber with the line that fetches it, and a mesh the show names beside it that the primary does not hold is counted
/// and named there too; a route whose projector does not answer is red with FLEET ROUTE TEST; the primary lists its backup; the
/// backup's INDEX offers FAILOVER lit by its mirror, pushed to a deck that holds INDEX as it moves; a solo node says it has no hot
/// backup.</summary>
public sealed class BackupPreflightTests
{
    private const long Second = SyncRig.Second;

    /// <summary>What the backup saw, read as a read of its mirror fails (after that read, not at it): its mirror's state and words, the
    /// mirror link's status, connection and the primary's silence on it on the node's clock, and when the fleet monitor last heard the
    /// primary on the fleet's clock.</summary>
    private static string Saw(HostRig backup) =>
        "the backup's mirror, its link and the fleet monitor's record of the primary, read after that read as this fails: " + BackupRig.Saw(backup);

    private static int ClosedPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task TheBackupsFilesAndTheRoutesAreCheckedWithTheLineThatFixesThem()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        var solo = Assert.Single(await BackupRig.PreflightAsync(primary));
        Assert.Equal((Light.Grey, "none"), (solo.Light, solo.Value));

        // A version whose file no machine holds and a mesh file beside the show the primary does not hold (the faults), and a route
        // to a projector that does not answer (the other).
        var item = new MediaItem("md_clip", "Clip", MediaKind.Video, "/nowhere/clip.mp4", "", 10, MediaInfo.Unknown, []);
        var version = new MediaVersion("mv_1", "hap-q", "{}", "ok:" + new string('0', 32), "sha256:" + new string('3', 64), 100, MediaInfo.Unknown,
            VersionQuality.None, "hapenc/1", BackupRig.PrimaryId, 1);
        var gone = new MeshAsset("ms00000009", "Gone", MeshOrigin.Imported, "meshes/ms00000009-gone.ply", "sha256:" + new string('9', 64), "", ParamSet.Empty, 8, 12);
        var show = SyncRig.Show();
        await SyncRig.OpenShowAsync(primary, show with { Media = [item with { Versions = [version] }], Stage = show.Stage with { Meshes = [gone] } });
        var dv = await primary.OkAsync($"DEVICE ADD P1 pjlink 127.0.0.1:{ClosedPort().ToString(CultureInfo.InvariantCulture)}");
        await primary.OkAsync($"FLEET ROUTE op_1 DEVICE {dv} 31 32");
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        // A deck on the backup holds INDEX: it is pushed again when the fleet section's FAILOVER key comes and its light moves.
        var deck = new Companion.RecordingSink();
        Assert.True(((IPushTopics)backup.Engine).TryOpen("INDEX", [], Companion.CompanionRig.Paired, deck, out var watching, out var why), why);
        using var subscription = watching;
        await backup.OkAsync($"CREDENTIAL SET node.{primary.Id} {HostRig.Token}");
        await backup.OkAsync("FLEET BACKUP " + primary.Id);
        Assert.True(await Eventually.TrueAsync(() => backup.Journal().Contains("mv_1.hap-q could not be pulled from FOH-A", StringComparison.Ordinal)), backup.Journal());

        var rows = await BackupRig.PreflightAsync(backup);
        var files = rows.Single(r => r.Id == "backup:files");
        Assert.Equal((Light.Amber, "2 missing"), (files.Light, files.Value));
        Assert.Equal("MEDIA FETCH md_clip VERSION mv_1 FROM 0badc0de", files.Fix);
        Assert.Contains("meshes/ms00000009-gone.ply", files.Note, StringComparison.Ordinal);
        var mirror = rows.Single(r => r.Id == "backup:mirror");
        // What the backup saw is read only when a read misses, so a passing run reads nothing more between the two reads under watch.
        if (("different", Light.Amber) != (mirror.Value, mirror.Light))
            Assert.Fail($"backup:mirror read {mirror.Value} {mirror.Light}, not different Amber; {Saw(backup)}");
        Assert.Contains("could not be pulled from FOH-A", mirror.Note, StringComparison.Ordinal);
        var route = rows.Single(r => r.Id == "route:op_1");
        Assert.Equal((Light.Red, "unreachable", "FLEET ROUTE TEST"), (route.Light, route.Value, route.Fix));
        Assert.StartsWith("P1 · unreachable", route.Note, StringComparison.Ordinal);

        // The primary lists its backup with the state it reports; its route is red too.
        var primaryRows = await BackupRig.PreflightAsync(primary);
        Assert.Equal(Light.Amber, primaryRows.Single(r => r.Id == "backup:" + backup.Id).Light);
        Assert.Equal(Light.Red, primaryRows.Single(r => r.Id == "route:op_1").Light);

        // INDEX: the backup's FAILOVER key, lit by its mirror; the primary's key per backup.
        var failover = (await BackupRig.IndexAsync(backup)).Single(r => r.GetProperty("id").GetString() == "FAILOVER");
        var lit = (failover.GetProperty("light").GetString(), failover.GetProperty("state").GetString());
        if (("amber", "different") != lit)
            Assert.Fail($"INDEX's FAILOVER read {lit}, not (amber, different); {Saw(backup)}");
        await deck.Heard("\"id\":\"FAILOVER\",\"kind\":\"takeover\",\"label\":\"FAILOVER\",\"parent\":\"fleet\",\"light\":\"amber\",\"state\":\"different\"");
        Assert.Contains(await BackupRig.IndexAsync(primary), r => r.GetProperty("label").GetString() == "FOH-B" && r.GetProperty("kind").GetString() == "backup");
    }
}
