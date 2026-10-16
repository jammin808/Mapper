using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>A hot backup of a token-protected primary, two real nodes on loopback: the backup proves the primary's token (never sends
/// it), pulls the show over HTTPS pinned by the hello, follows the primary's group clock, and tracks every edit, transport and air
/// line at the primary's leader instants; local edits on the backup are refused with the way out; FAILOVER makes it the primary on
/// the same time line under a higher epoch, the old primary hears it and fences itself; FAILBACK hands the show back and the two swap.</summary>
public sealed class BackupTests
{
    private const long Second = SyncRig.Second;

    private static string? Anchor(HostRig rig, string playlist) => rig.Engine.AnchorJson(playlist);

    [Fact]
    public async Task ABackupMirrorsATokenProtectedPrimaryTakesOverAndHandsTheShowBack()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);

        // Without the primary's token the backup is refused, and told how to give it one.
        var refused = await backup.RunAsync("FLEET BACKUP " + primary.Id);
        Assert.False(refused.Ok);
        Assert.Contains($"CREDENTIAL SET node.{primary.Id}", refused.Payload, StringComparison.Ordinal);
        Assert.Equal("solo", BackupRig.Role(backup));

        await BackupRig.BackUpAsync(primary, backup);
        Assert.Equal("backup", BackupRig.Role(backup));
        Assert.Equal(primary.Id, BackupRig.Fleet(backup).GetProperty("of").GetString());
        Assert.Equal(BackupRig.ShowJson(primary), BackupRig.ShowJson(backup));
        Assert.Equal(primary.Engine.Store.Current.Revision, BackupRig.Fleet(backup).GetProperty("mirror").GetProperty("rev").GetInt64());
        // The primary lists its backup, and its pulls were proved and came over HTTPS.
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(primary).GetProperty("backups").EnumerateArray()
            .Any(b => b.GetProperty("node").GetString() == backup.Id && b.GetProperty("state").GetString() == "identical")), BackupRig.Fleet(primary).ToString());
        Assert.Equal("primary", BackupRig.Role(primary));
        var mirror = Mirror(primary);
        Assert.True(mirror.Proved > 0);
        Assert.Equal(mirror.Proved, mirror.OverHttps);
        // Both clocks read the same leader instant.
        Assert.True(await Eventually.TrueAsync(() => backup.Engine.LeaderNowNs == primary.Engine.LeaderNowNs));

        // An edit on the primary reaches the backup (event-driven), revision for revision.
        await primary.OkAsync("SET op_1 name \"Stage left\"");
        Assert.True(await Eventually.TrueAsync(() => BackupRig.ShowJson(backup) == BackupRig.ShowJson(primary)), BackupRig.Fleet(backup).ToString());
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(backup).GetProperty("mirror").GetProperty("rev").GetInt64() == primary.Engine.Store.Current.Revision));

        // Transports: a playlist in the group (the sync feed) and one in none (the mirror) land on the same anchors.
        await primary.OkAsync("PLAYLIST GO pl_show");
        await primary.OkAsync("PLAYLIST GO pl_local");
        Assert.True(await Eventually.TrueAsync(() => Anchor(backup, "pl_show") == Anchor(primary, "pl_show")), backup.Journal());
        Assert.True(await Eventually.TrueAsync(() => Anchor(backup, "pl_local") == Anchor(primary, "pl_local")), backup.Journal());

        // The air: a blackout (the group's feed), a freeze (the mirror's own air), a panic fade at the primary's instant.
        await primary.OkAsync("BLACKOUT ON");
        await primary.OkAsync("FREEZE ON");
        Assert.True(await Eventually.TrueAsync(() => Air(backup).GetProperty("blackout").GetBoolean() && Air(backup).GetProperty("frozen").GetBoolean()), Air(backup).ToString());
        await primary.OkAsync("FREEZE OFF");
        await primary.OkAsync("PANIC FADE 1000");
        Assert.True(await Eventually.TrueAsync(() => Air(backup).GetProperty("panic").ValueKind == JsonValueKind.Object), Air(backup).ToString());
        Assert.False(Air(backup).GetProperty("frozen").GetBoolean());
        // The fade is a pure function of the leader instant, the same on both at every instant of it.
        var now = primary.Engine.LeaderNowNs;
        foreach (var t in new[] { now, now + Second / 5, now + Second / 2, now + 4 * Second / 5, now + 2 * Second })
            Assert.Equal(primary.Engine.AirLevelsNow!.Gain("op_1", t), backup.Engine.AirLevelsNow!.Gain("op_1", t));
        Assert.Equal(0f, backup.Engine.AirLevelsNow!.Gain("op_1", now + 3 * Second));

        // A client's edit on the backup is refused with the way out; its queries and panic stay.
        var local = await backup.RunAsync("SET op_1 name Elsewhere");
        Assert.False(local.Ok);
        Assert.Contains("FOH-B mirrors FOH-A — send it to FOH-A", local.Payload, StringComparison.Ordinal);
        Assert.Contains("or FAILOVER", local.Payload, StringComparison.Ordinal);
        Assert.True((await backup.RunAsync("STATE")).Ok);

        // FAILOVER: the backup leads on the same time line under a higher epoch; the old primary hears it and fences itself.
        var before = backup.Engine.LeaderNowNs;
        var takeover = await backup.RunAsync("FAILOVER");
        Assert.True(takeover.Ok, takeover.Payload);
        Assert.InRange(backup.Engine.LeaderNowNs - before, 0, 16_666_667);
        Assert.Equal(("primary", 1L), (BackupRig.Role(backup), BackupRig.Fleet(backup).GetProperty("epoch").GetInt64()));
        Assert.Equal("leader", backup.State().GetProperty("sync").GetProperty("role").GetString());
        Assert.Equal(1, backup.State().GetProperty("sync").GetProperty("epoch").GetInt64());
        Assert.Equal("takeover", BackupRig.Fleet(backup).GetProperty("last").GetProperty("kind").GetString());
        Assert.Contains("epoch 1 committed", BackupRig.Fleet(backup).GetProperty("last").GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.True((await backup.RunAsync("SET op_1 name \"Taken\"")).Ok);   // editing goes on on the new primary
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(primary) == "fenced"), BackupRig.Fleet(primary) + "\n" + primary.Journal());
        Assert.Equal(1, BackupRig.Fleet(primary).GetProperty("epoch").GetInt64());
        Assert.Equal(0f, primary.Engine.Services.OutputGains!.GainOf("op_1"));
        Assert.Equal(0f, primary.Engine.AirLevelsNow!.Gain("op_1", primary.Engine.LeaderNowNs));
        var fencedEdit = await primary.RunAsync("SET op_1 name Again");
        Assert.False(fencedEdit.Ok);
        Assert.Contains("fenced: FOH-B has the show (epoch 1)", fencedEdit.Payload, StringComparison.Ordinal);
        // The role and the epoch are kept for the next start.
        Assert.Equal(("fenced", 1L), (primary.Home.LoadSettings("x").Fleet.Role, primary.Home.LoadSettings("x").Fleet.Epoch));
        Assert.Equal(("primary", 1L), (backup.Home.LoadSettings("x").Fleet.Role, backup.Home.LoadSettings("x").Fleet.Epoch));

        // FAILBACK: the old primary mirrors the new one, then takes its show back; the two swap roles.
        await BackupRig.BackUpAsync(backup, primary);
        Assert.Equal(1f, primary.Engine.Services.OutputGains!.GainOf("op_1"));
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(backup).GetProperty("backups").EnumerateArray()
            .Any(b => b.GetProperty("node").GetString() == primary.Id && b.GetProperty("state").GetString() == "identical")), BackupRig.Fleet(backup).ToString());
        var back = await backup.RunAsync("FAILBACK CONFIRM");
        Assert.True(back.Ok, back.Payload);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(primary) == "primary" && BackupRig.MirrorState(backup) == "identical", 15000),
            BackupRig.Fleet(primary) + "\n" + BackupRig.Fleet(backup) + "\n" + backup.Journal());
        Assert.Equal(2, BackupRig.Fleet(primary).GetProperty("epoch").GetInt64());
        Assert.Equal(("backup", primary.Id), (BackupRig.Role(backup), BackupRig.Fleet(backup).GetProperty("of").GetString()));
        Assert.Equal("failback", BackupRig.Fleet(backup).GetProperty("last").GetProperty("kind").GetString());
        Assert.Equal(BackupRig.ShowJson(primary), BackupRig.ShowJson(backup));
    }

    [Fact]
    public async Task APullWithoutTheProofIsRefusedAndTheTokenIsNeverTheProof()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        using var http = primary.Http();
        var none = await http.GetAsync(new Uri(MirrorWebModule.ProjectPath, UriKind.Relative));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, none.StatusCode);
        Assert.Contains("pairing proof", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // The token in a header is what the token gate takes; the mirror takes a proof only.
        using var withToken = new HttpRequestMessage(HttpMethod.Get, new Uri(MirrorWebModule.ProjectPath, UriKind.Relative));
        withToken.Headers.Add("X-PlayMap-Token", HostRig.Token);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(withToken)).StatusCode);
        // A proof of another token, and a proof replayed, are refused; a fresh proof of this node's token is let in.
        var nonce = PullProof.NewNonce(primary.Engine.Services.Wall.UnixMs);
        using var wrong = Signed(nonce, PullProof.Sign("AAAA-BBBB-CCCC", nonce, "GET", MirrorWebModule.ProjectPath));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(wrong)).StatusCode);
        var fresh = PullProof.NewNonce(primary.Engine.Services.Wall.UnixMs);
        using var right = Signed(fresh, PullProof.Sign(HostRig.Token, fresh, "GET", MirrorWebModule.ProjectPath));
        var ok = await http.SendAsync(right);
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        Assert.Equal(primary.Engine.Store.Current.Revision.ToString(CultureInfo.InvariantCulture), ok.Headers.GetValues("X-PlayMap-Rev").Single());
        using var replay = Signed(fresh, PullProof.Sign(HostRig.Token, fresh, "GET", MirrorWebModule.ProjectPath));
        var replayed = await http.SendAsync(replay);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, replayed.StatusCode);
        Assert.Contains("used already", await replayed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // A nonce from another minute is refused.
        var old = PullProof.NewNonce(primary.Engine.Services.Wall.UnixMs - 5 * 60_000);
        using var stale = Signed(old, PullProof.Sign(HostRig.Token, old, "GET", MirrorWebModule.ProjectPath));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(stale)).StatusCode);
        Assert.Equal(1, Mirror(primary).Proved);

        static HttpRequestMessage Signed(string nonce, string proof)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, new Uri(MirrorWebModule.ProjectPath, UriKind.Relative));
            r.Headers.Add(PullProof.NonceHeader, nonce);
            r.Headers.Add(PullProof.ProofHeader, proof);
            return r;
        }
    }

    [Fact]
    public async Task MediaFetchVersionPullsOneOptimisedVersionByHashWithTheProof()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var a = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        await using var b = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        // FOH-A holds a version's file (any bytes: the pull is by hash and verified by it).
        var bytes = new byte[300_000];
        var random = new PlayMap.Core.Numerics.Pcg32(7);
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)random.NextUInt();
        var file = Path.Combine(a.Dir.FullName, "version.mov");
        await File.WriteAllBytesAsync(file, bytes);
        var hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        a.Host.Media.AddVerified(hash, file, "version.mov");
        var item = new MediaItem("md_clip", "Clip", MediaKind.Video, "/nowhere/clip.mp4", "sha256:" + new string('1', 64), 10, MediaInfo.Unknown, []);
        var version = new MediaVersion("mv_1", "hap-q", "{}", "ok:" + new string('0', 32), hash, bytes.Length, MediaInfo.Unknown,
            VersionQuality.None, "hapenc/1", BackupRig.PrimaryId, 1);
        var show = SyncRig.Show() with { Media = [item with { Versions = [version] }] };
        await SyncRig.OpenShowAsync(a, show);
        await SyncRig.OpenShowAsync(b, show);
        await BackupRig.MeetAsync(a, b);

        Assert.Contains("has no version mv_9", (await b.RunAsync("MEDIA FETCH md_clip VERSION mv_9 FROM FOH-A")).Payload, StringComparison.Ordinal);
        // Without FOH-A's token the pull is refused at FOH-A's door; nothing lands.
        Assert.True((await b.RunAsync("MEDIA FETCH md_clip VERSION mv_1 FROM FOH-A")).Ok);
        Assert.True(await Eventually.TrueAsync(() => b.Journal().Contains("could not be pulled from FOH-A", StringComparison.Ordinal)), b.Journal());
        Assert.Null(b.Engine.Services.Media!.PathForHash(hash));
        Assert.Equal(0, Mirror(a).Proved);

        await b.OkAsync($"CREDENTIAL SET node.{a.Id} {HostRig.Token}");
        var fetch = await b.RunAsync("MEDIA FETCH md_clip VERSION mv_1 FROM FOH-A");
        Assert.True(fetch.Ok, fetch.Payload);
        Assert.False(fetch.Applied);   // accepted: the pull reports when it lands
        Assert.True(await Eventually.TrueAsync(() => b.Engine.Services.Media!.PathForHash(hash) is not null), b.Journal());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(b.Engine.Services.Media!.PathForHash(hash)!));
        Assert.Contains("pulled from FOH-A with proof over HTTPS and verified", b.Journal(), StringComparison.Ordinal);
        Assert.True(Mirror(a).Proved > 0);
        Assert.Equal(Mirror(a).Proved, Mirror(a).OverHttps);
        Assert.Contains("is here already", (await b.RunAsync("MEDIA FETCH md_clip VERSION mv_1 FROM FOH-A")).Payload, StringComparison.Ordinal);
    }

    private static JsonElement Air(HostRig rig) => rig.State().GetProperty("air");

    private static MirrorWebModule Mirror(HostRig rig) => rig.Engine.Services.Backup!.Web!;
}
