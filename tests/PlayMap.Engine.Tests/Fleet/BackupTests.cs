using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Host;
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

    /// <summary>A file the node is still writing, as far as it is written ("" when there is none yet).</summary>
    private static async Task<string> ReadShared(string path)
    {
        if (!File.Exists(path))
            return "";
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return await reader.ReadToEndAsync();
    }

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

        // The air: a blackout (the group's feed), a freeze (the mirror's own air), a panic fade at the primary's instant. The group's
        // feed and the mirror are two links, each in order but with no order between them: the backup's air is read once both
        // lines have landed, each a fact of its own.
        await primary.OkAsync("BLACKOUT ON");
        await primary.OkAsync("FREEZE ON");
        Assert.True(await Eventually.TrueAsync(() => Air(backup).GetProperty("blackout").GetBoolean() && Air(backup).GetProperty("frozen").GetBoolean()), Air(backup).ToString());
        await primary.OkAsync("FREEZE OFF");
        await primary.OkAsync("PANIC FADE 1000");
        Assert.True(await Eventually.TrueAsync(() => Air(backup).GetProperty("panic").ValueKind == JsonValueKind.Object && !Air(backup).GetProperty("frozen").GetBoolean()),
            Air(backup).ToString());
        // The fade is a pure function of the leader instant, the same on both at every instant of it. The render thread's levels are
        // built on the backup's lane after its air took the panic, so they are read as a fact of their own too.
        var now = primary.Engine.LeaderNowNs;
        long[] instants = [now, now + Second / 5, now + Second / 2, now + 4 * Second / 5, now + 2 * Second];
        Assert.True(await Eventually.TrueAsync(() => instants.All(t => primary.Engine.AirLevelsNow!.Gain("op_1", t) == backup.Engine.AirLevelsNow!.Gain("op_1", t))),
            string.Join(", ", instants.Select(t => $"{primary.Engine.AirLevelsNow!.Gain("op_1", t)} / {backup.Engine.AirLevelsNow!.Gain("op_1", t)}")));
        Assert.Equal(0f, backup.Engine.AirLevelsNow!.Gain("op_1", now + 3 * Second));

        // A client's edit on the backup is refused with the way out; its queries and panic stay.
        var local = await backup.RunAsync("SET op_1 name Elsewhere");
        Assert.False(local.Ok);
        Assert.Contains("FOH-B mirrors FOH-A — send it to FOH-A", local.Payload, StringComparison.Ordinal);
        Assert.Contains("or FAILOVER", local.Payload, StringComparison.Ordinal);
        Assert.True((await backup.RunAsync("STATE")).Ok);

        // FAILOVER: the backup leads on the same time line under a higher epoch; the old primary hears it and fences itself.
        // Both nodes' clocks are the test's manual one, standing still: the show time read across the takeover is a fact, not a time
        // measured, and continuing the old leader's line it moves not at all.
        var before = backup.Engine.LeaderNowNs;
        var takeover = await backup.RunAsync("FAILOVER");
        Assert.True(takeover.Ok, takeover.Payload);
        Assert.Equal(before, backup.Engine.LeaderNowNs);
        Assert.Contains("show clock continuous (0 ms read across the change)", takeover.Effect, StringComparison.Ordinal);
        Assert.Equal(("primary", 1L), (BackupRig.Role(backup), BackupRig.Fleet(backup).GetProperty("epoch").GetInt64()));
        Assert.Equal("leader", backup.State().GetProperty("sync").GetProperty("role").GetString());
        Assert.Equal(1, backup.State().GetProperty("sync").GetProperty("epoch").GetInt64());
        Assert.Equal("takeover", BackupRig.Fleet(backup).GetProperty("last").GetProperty("kind").GetString());
        Assert.Contains("epoch 1 committed", BackupRig.Fleet(backup).GetProperty("last").GetProperty("words").GetString(), StringComparison.Ordinal);
        // The backup was in the primary's panic black: it stays black on the air it now serves.
        Assert.Equal(JsonValueKind.Object, Air(backup).GetProperty("panic").ValueKind);
        Assert.Equal(0f, backup.Engine.AirLevelsNow!.Gain("op_1", backup.Engine.LeaderNowNs + 3 * Second));
        Assert.True((await backup.RunAsync("SET op_1 name \"Taken\"")).Ok);   // editing goes on on the new primary
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(primary) == "fenced"), BackupRig.Fleet(primary) + "\n" + primary.Journal());
        Assert.Equal(1, BackupRig.Fleet(primary).GetProperty("epoch").GetInt64());
        Assert.Equal(0f, primary.Engine.Services.OutputGains!.GainOf("op_1", primary.Engine.LeaderNowNs));
        Assert.Equal(0f, primary.Engine.AirLevelsNow!.Gain("op_1", primary.Engine.LeaderNowNs));
        var fencedEdit = await primary.RunAsync("SET op_1 name Again");
        Assert.False(fencedEdit.Ok);
        Assert.Contains("fenced: FOH-B has the show (epoch 1)", fencedEdit.Payload, StringComparison.Ordinal);
        // The role and the epoch are kept for the next start. The fenced node's STATE names the role before its settings file holds
        // it; the fence's journal row, which says whether it was kept, follows the file.
        Assert.True(await Eventually.TrueAsync(() => primary.Journal().Contains("fleet: fenced: FOH-B has the show since", StringComparison.Ordinal)), primary.Journal());
        Assert.Equal(("fenced", 1L), (primary.Home.LoadSettings("x").Fleet.Role, primary.Home.LoadSettings("x").Fleet.Epoch));
        Assert.Equal(("primary", 1L), (backup.Home.LoadSettings("x").Fleet.Role, backup.Home.LoadSettings("x").Fleet.Epoch));

        // FAILBACK: the old primary mirrors the new one, then takes its show back; the two swap roles.
        await BackupRig.BackUpAsync(backup, primary);
        Assert.Equal(1f, primary.Engine.Services.OutputGains!.GainOf("op_1", primary.Engine.LeaderNowNs));
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(backup).GetProperty("backups").EnumerateArray()
            .Any(b => b.GetProperty("node").GetString() == primary.Id && b.GetProperty("state").GetString() == "identical")), BackupRig.Fleet(backup).ToString());
        var back = await backup.RunAsync("FAILBACK CONFIRM");
        Assert.True(back.Ok, back.Payload);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(primary) == "primary" && BackupRig.MirrorState(backup) == "identical", 15000),
            BackupRig.Fleet(primary) + "\n" + BackupRig.Fleet(backup) + "\n" + primary.State().GetProperty("sync") + "\n" + backup.State().GetProperty("sync")
            + $"\nA answers on {primary.Network.SyncPort}, B on {backup.Network.SyncPort}; B hears A's clock on {backup.Network.Hello!.Peers.Find(primary.Id)?.Hello.Port("sync")} from {backup.Network.Hello!.Peers.Find(primary.Id)?.Hello.Name} ({backup.Network.Hello!.Peers.Find(primary.Id)?.Liveness}); hello ports A {primary.Network.HelloPort} B {backup.Network.HelloPort}\n" + backup.Journal() + "\n---\n" + primary.Journal());
        Assert.Equal(2, BackupRig.Fleet(primary).GetProperty("epoch").GetInt64());
        Assert.Equal(("backup", primary.Id), (BackupRig.Role(backup), BackupRig.Fleet(backup).GetProperty("of").GetString()));
        // The hand-back's record is written by the node that handed the show back once its join returns, beside its mirror coming
        // up: a fact of its own.
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(backup).GetProperty("last").GetProperty("kind").GetString() == "failback"),
            BackupRig.Fleet(backup).ToString());
        Assert.Equal(BackupRig.ShowJson(primary), BackupRig.ShowJson(backup));

        // Each node was given the other's token (CREDENTIAL SET node.<id> <token>) and proved it for its mirror, its takeover, its fence
        // and its hand-back: the token is in no journal row and no log line of either.
        foreach (var rig in new[] { primary, backup })
        {
            Assert.All(rig.Engine.Journal.Recent(200), r => Assert.DoesNotContain(HostRig.Token, $"{r.Text}|{r.Line}|{r.Who}", StringComparison.Ordinal));
            var journal = await ReadShared(rig.Home.JournalPath);
            Assert.Contains("\"kind\":\"verb\"", journal, StringComparison.Ordinal);
            Assert.DoesNotContain(HostRig.Token, journal, StringComparison.Ordinal);
            Assert.DoesNotContain(HostRig.Token, await ReadShared(Path.Combine(rig.Home.Folder, NodeLogProvider.FileName)), StringComparison.Ordinal);
        }
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
        using var wrong = Signed(nonce, PullProof.Sign("AAAA-BBBB-CCCC", nonce, "GET", "http", MirrorWebModule.ProjectPath));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(wrong)).StatusCode);
        var fresh = PullProof.NewNonce(primary.Engine.Services.Wall.UnixMs);
        using var right = Signed(fresh, PullProof.Sign(HostRig.Token, fresh, "GET", "http", MirrorWebModule.ProjectPath));
        var ok = await http.SendAsync(right);
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        Assert.Equal(primary.Engine.Store.Current.Revision.ToString(CultureInfo.InvariantCulture), ok.Headers.GetValues("X-PlayMap-Rev").Single());
        using var replay = Signed(fresh, PullProof.Sign(HostRig.Token, fresh, "GET", "http", MirrorWebModule.ProjectPath));
        var replayed = await http.SendAsync(replay);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, replayed.StatusCode);
        Assert.Contains("used already", await replayed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // A proof made for HTTPS is refused over plain HTTP (a request signed for one connection cannot be relayed to the other).
        var other = PullProof.NewNonce(primary.Engine.Services.Wall.UnixMs);
        using var relayed = Signed(other, PullProof.Sign(HostRig.Token, other, "GET", "https", MirrorWebModule.ProjectPath));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(relayed)).StatusCode);
        // A nonce from another minute is refused.
        var old = PullProof.NewNonce(primary.Engine.Services.Wall.UnixMs - 5 * 60_000);
        using var stale = Signed(old, PullProof.Sign(HostRig.Token, old, "GET", "http", MirrorWebModule.ProjectPath));
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
        // And the clip's own file.
        var clipBytes = bytes[..1000].Reverse().ToArray();
        var clipFile = Path.Combine(a.Dir.FullName, "clip.mp4");
        await File.WriteAllBytesAsync(clipFile, clipBytes);
        var clipHash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(clipBytes));
        a.Host.Media.AddVerified(clipHash, clipFile, "clip.mp4");
        var item = new MediaItem("md_clip", "Clip", MediaKind.Video, "/nowhere/clip.mp4", clipHash, clipBytes.Length, MediaInfo.Unknown, []);
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
        // The file is a fact first; the journal's row follows it.
        Assert.True(await Eventually.TrueAsync(() => b.Journal().Contains("pulled from FOH-A with proof over HTTPS and verified", StringComparison.Ordinal)), b.Journal());
        Assert.True(Mirror(a).Proved > 0);
        Assert.Equal(Mirror(a).Proved, Mirror(a).OverHttps);
        Assert.Contains("is here already", (await b.RunAsync("MEDIA FETCH md_clip VERSION mv_1 FROM FOH-A")).Payload, StringComparison.Ordinal);
        // The clip's own file the same way (MEDIA FETCH with the fleet's part pulls with the proof, from the first node that holds it).
        Assert.True((await b.RunAsync("MEDIA FETCH md_clip")).Ok);
        Assert.True(await Eventually.TrueAsync(() => b.Engine.Services.Media!.PathForHash(clipHash) is not null), b.Journal());
        Assert.True(await Eventually.TrueAsync(() => b.Journal().Contains("clip.mp4 pulled from FOH-A with proof over HTTPS and verified", StringComparison.Ordinal)), b.Journal());
    }

    [Fact]
    public async Task AMirroredShowsFilePullsAreJournalledAfterTheRowOfTheMirrorLineThatBeganThem()
    {
        // The backup applies its primary's show and then pulls the clip it names that the backup does not hold: the pull's row follows
        // the row of the MIRROR PROJECT line that began it, however late that row is written.
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var a = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        await using var b = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        var bytes = new byte[20_000];
        var random = new PlayMap.Core.Numerics.Pcg32(11);
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)random.NextUInt();
        var file = Path.Combine(a.Dir.FullName, "clip.mp4");
        await File.WriteAllBytesAsync(file, bytes);
        var hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        a.Host.Media.AddVerified(hash, file, "clip.mp4");
        var item = new MediaItem("md_clip", "Clip", MediaKind.Video, "/nowhere/clip.mp4", hash, bytes.Length, MediaInfo.Unknown, []);
        await SyncRig.OpenShowAsync(a, SyncRig.Show() with { Media = [item] });
        await a.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(a, b);
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Engine.Journal.Written += r =>
        {
            if (r.Kind == "media" && r.Text.StartsWith("clip.mp4 pulled from FOH-A", StringComparison.Ordinal))
                pulled.TrySetResult();
        };
        b.Engine.RowHeldForTests = (line, held) => !held && line.Raw.StartsWith("MIRROR PROJECT", StringComparison.Ordinal)
            ? pulled.Task.WaitAsync(TimeSpan.FromSeconds(30))
            : Task.CompletedTask;

        await BackupRig.BackUpAsync(a, b);
        await pulled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(b.Engine.Services.Media!.PathForHash(hash)!));
        var rows = b.Engine.Journal.Recent(200)
            .Where(r => (r.Kind == "verb" && r.Line?.StartsWith("MIRROR PROJECT", StringComparison.Ordinal) == true) || r.Text.StartsWith("clip.mp4 ", StringComparison.Ordinal))
            .ToList();
        var mirrored = rows.FindIndex(r => r.Kind == "verb");
        var pull = rows.FindIndex(r => r.Kind == "media");
        Assert.True(mirrored >= 0 && pull > mirrored, string.Join(" | ", rows.Select(r => $"{r.Kind}: {r.Text}")));
        Assert.StartsWith("mirrored FOH-A rev ", rows[mirrored].Text, StringComparison.Ordinal);
        Assert.Equal("clip.mp4 pulled from FOH-A with proof over HTTPS and verified", rows[pull].Text);
    }

    [Fact]
    public async Task AFetchOfMoreFilesThanTheJournalHoldsWithTheFleetsPartLeavesTheNodesOwnRows()
    {
        // Two hundred and fifty files FOH-A does not hold, each asked for by its size first and refused, and one verb's row to open
        // them: one refused row a file would push every other row out of the journal's recent ones. The fetch journals its first few
        // and one that counts the rest.
        const int Files = 250;
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var a = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        await using var b = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        string Name(int k) => string.Create(CultureInfo.InvariantCulture, $"clip{k:000}");
        var show = SyncRig.Show() with
        {
            Media = [.. Enumerable.Range(0, Files).Select(k => new MediaItem("md" + Name(k), Name(k), MediaKind.Video, $"/nowhere/{Name(k)}.mp4",
                "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(k))), 0, MediaInfo.Unknown, []))],
        };
        await SyncRig.OpenShowAsync(b, show);
        await BackupRig.MeetAsync(a, b);
        var before = b.Engine.Journal.Recent(1).Single();

        Assert.True((await b.RunAsync("MEDIA FETCH ALL FROM FOH-A")).Ok);

        Assert.True(await Eventually.TrueAsync(() => Rows(b, "the fetch of").Count == 1, 120000), b.Journal());
        var refused = Rows(b, "could not be pulled from FOH-A");
        Assert.Equal(FetchJournal.Most, refused.Count);
        for (var k = 0; k < FetchJournal.Most; k++)
            Assert.StartsWith($"refused: {Name(k)}.mp4 could not be pulled from FOH-A: ", refused[k], StringComparison.Ordinal);
        var ended = string.Create(CultureInfo.InvariantCulture,
            $"refused: the fetch of 250 files from FOH-A ended: 0 pulled and verified, 250 could not be fetched; {Files - FetchJournal.Most} of its rows are in the node's log only");
        Assert.Equal([ended], Rows(b, "the fetch of"));
        Assert.Single(Rows(b, "FOH-B is up"));
        Assert.Single(Rows(b, "verb: opened Sync test"));
        // The context's recent rows, the window of the journal that MCP's get_context, Patterns and the assistant read, still hold the
        // row written last before the fetch: the fetch's rows there are its verb's own, its pulls' first few and the one that counts the
        // rest.
        var context = ContextBuilder.Build(b.Engine).Recent.Select(r => r.Text).ToList();
        Assert.Contains(before.Ok ? before.Text : before.Kind + ": " + before.Text, context);
        Assert.Equal(FetchJournal.Most, context.Count(r => r.Contains("could not be pulled from FOH-A", StringComparison.Ordinal)));
        Assert.Equal(ended, context[^1]);
    }

    [Fact]
    public async Task AFleetFetchsRowsFollowTheRowOfTheVerbThatBeganThem()
    {
        // Six files FOH-A does not hold, each refused at its first ask: every pull's row, and the row that counts the rest, follow the
        // MEDIA FETCH's own row, however late that row is written.
        const int Files = 6;
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var a = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        await using var b = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        string Name(int k) => string.Create(CultureInfo.InvariantCulture, $"clip{k}");
        var show = SyncRig.Show() with
        {
            Media = [.. Enumerable.Range(0, Files).Select(k => new MediaItem("md" + Name(k), Name(k), MediaKind.Video, $"/nowhere/{Name(k)}.mp4",
                "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(k))), 0, MediaInfo.Unknown, []))],
        };
        await SyncRig.OpenShowAsync(b, show);
        await BackupRig.MeetAsync(a, b);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Engine.Journal.Written += r =>
        {
            if (r.Text.StartsWith("the fetch of ", StringComparison.Ordinal))
                ended.TrySetResult();
        };
        b.Engine.RowHeldForTests = (line, held) => !held && line.Raw.StartsWith("MEDIA FETCH", StringComparison.Ordinal)
            ? ended.Task.WaitAsync(TimeSpan.FromSeconds(60))
            : Task.CompletedTask;

        var reply = await b.RunAsync("MEDIA FETCH ALL FROM FOH-A");
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(reply.Ok, reply.Payload);
        var rows = b.Engine.Journal.Recent(200)
            .Where(r => r.Line == "MEDIA FETCH ALL FROM FOH-A" || r.Text.Contains(" could not be pulled from FOH-A: ", StringComparison.Ordinal)
                || r.Text.StartsWith("the fetch of ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["verb", .. Enumerable.Repeat("refused", FetchJournal.Most + 1)], rows.Select(r => r.Kind));
        Assert.Equal($"fetching {Files} files from FOH-A by hash, with proof", rows[0].Text);
        for (var k = 0; k < FetchJournal.Most; k++)
            Assert.StartsWith($"{Name(k)}.mp4 could not be pulled from FOH-A: ", rows[k + 1].Text, StringComparison.Ordinal);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture,
            $"the fetch of {Files} files from FOH-A ended: 0 pulled and verified, {Files} could not be fetched; {Files - FetchJournal.Most} of its rows are in the node's log only"),
            rows[^1].Text);
    }

    [Fact]
    public async Task AHandBacksTakeoverIsJournalledAfterTheRowOfTheMirrorLineThatAskedForIt()
    {
        // An identical backup handed the show back takes it over beside the line: the takeover's own lines and its row follow the
        // MIRROR HANDBACK's row, however late that row is written.
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backup.Engine.Journal.Written += r =>
        {
            if (r.Kind == "fleet" && r.Text.StartsWith("takeover ", StringComparison.Ordinal))
                taken.TrySetResult();
        };
        backup.Engine.RowHeldForTests = (line, held) => !held && line.Raw.StartsWith("MIRROR HANDBACK", StringComparison.Ordinal)
            ? taken.Task.WaitAsync(TimeSpan.FromSeconds(60))
            : Task.CompletedTask;
        Assert.True(WireGrammar.TryParse($"MIRROR HANDBACK {{\"from\":\"{BackupRig.PrimaryId}\",\"epoch\":0}}", out var handback, out var error), error?.Reason);
        var mirror = new SessionInfo("fleet:" + BackupRig.PrimaryId, "fleet", "127.0.0.1", false, "FOH-A (mirror)", true);

        var reply = await backup.Engine.HandleAsync(handback, mirror, CancellationToken.None);
        await taken.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(reply.Ok, reply.Payload);
        Assert.Equal("primary", BackupRig.Role(backup));
        var rows = backup.Engine.Journal.Recent(200).ToList();
        var asked = rows.FindLastIndex(r => r.Kind == "verb" && r.Line?.StartsWith("MIRROR HANDBACK", StringComparison.Ordinal) == true);
        var lead = rows.FindLastIndex(r => r.Line == "SYNC LEAD show");
        var takeover = rows.FindLastIndex(r => r.Kind == "fleet" && r.Text.StartsWith("takeover ", StringComparison.Ordinal));
        var said = string.Join(" | ", rows.Select(r => $"{r.Kind}: {r.Text}"));
        Assert.True(asked >= 0 && lead > asked && takeover > lead, said);
        Assert.StartsWith("taking the show back from FOH-A", rows[asked].Text, StringComparison.Ordinal);
        Assert.Equal(EngineCore.SystemSession.ClientName, rows[lead].Who);
        Assert.Contains("(handed back by FOH-A", rows[takeover].Text, StringComparison.Ordinal);
    }

    /// <summary>A FAILOVER between two nodes whose settings files refuse every write moves both for this run and says so on each: the
    /// backup's fault row says its takeover's epoch is not kept, the old primary's fence row says its fence is not, and each node's STATE
    /// holds what its row reports, and has been pushed, by the time that row is written.</summary>
    [Fact]
    public async Task AFailoverWhoseSettingsCannotBeKeptOnEitherNodeSaysSoInEachRowOnceStateHoldsIt()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(primary).GetProperty("backups").EnumerateArray()
            .Any(b => b.GetProperty("node").GetString() == backup.Id && b.GetProperty("state").GetString() == "identical")), BackupRig.Fleet(primary).ToString());

        primary.Home.WriteFile = (_, _) => throw new IOException("disk full");
        backup.Home.WriteFile = (_, _) => throw new IOException("disk full");
        var taken = new RowWatch(backup, "fault", "the takeover's epoch 1", () => backup.Engine.Services.Backup!.Role == FleetRoles.Primary);
        var fenced = new RowWatch(primary, "fleet", "fenced: FOH-B has the show since", () => primary.Engine.Services.Backup!.Role == FleetRoles.Fenced);
        var takeover = await backup.RunAsync("FAILOVER");
        Assert.True(takeover.Ok, takeover.Payload);

        var (row, state, pushedBefore) = await taken.SeenAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("the takeover's epoch 1 — not kept for the next start (the node's settings could not be written: disk full): after a "
            + "restart this node would not know it took the show", row.Text);
        Assert.False(row.Ok);
        var fleet = state.GetProperty("fleet");
        Assert.Equal(("primary", primary.Id, 1L), (fleet.GetProperty("role").GetString(), fleet.GetProperty("of").GetString(), fleet.GetProperty("epoch").GetInt64()));
        Assert.True(pushedBefore, "no push of STATE was raised between the takeover's role and epoch and its fault row");
        Assert.Equal((FleetRoles.Backup, 0L), (backup.Home.LoadSettings("x").Fleet.Role, backup.Home.LoadSettings("x").Fleet.Epoch));

        var (fence, fenceState, fencePushedBefore) = await fenced.SeenAsync(TimeSpan.FromSeconds(15));
        Assert.StartsWith("fenced: FOH-B has the show since ", fence.Text, StringComparison.Ordinal);
        Assert.EndsWith("(epoch 1) — outputs held black; FLEET BACKUP FOH-B to mirror it — not kept for the next start (the node's settings "
            + "could not be written: disk full)", fence.Text, StringComparison.Ordinal);
        Assert.False(fence.Ok);
        var fenceFleet = fenceState.GetProperty("fleet");
        Assert.Equal(("fenced", backup.Id, 1L), (fenceFleet.GetProperty("role").GetString(), fenceFleet.GetProperty("of").GetString(),
            fenceFleet.GetProperty("epoch").GetInt64()));
        Assert.True(fencePushedBefore, "no push of STATE was raised between the fence's role and epoch and its row");
        Assert.Equal((FleetRoles.Primary, 0L), (primary.Home.LoadSettings("x").Fleet.Role, primary.Home.LoadSettings("x").Fleet.Epoch));
    }

    /// <summary>A node a backup joins is the show's primary, with that backup, for this run though its settings file refuses the write,
    /// and says so: STATE names it the primary, and has been pushed, by the time that row is written.</summary>
    [Fact]
    public async Task APrimaryWhoseSettingsCannotKeepItsRoleSaysSo()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        primary.Home.WriteFile = (_, _) => throw new IOException("disk full");
        var role = new RowWatch(primary, "fault", "FOH-A's role as the primary of FOH-B", () => primary.Engine.Services.Backup!.Role == FleetRoles.Primary);

        await BackupRig.BackUpAsync(primary, backup);
        var (row, state, pushedBefore) = await role.SeenAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("FOH-A's role as the primary of FOH-B — not kept for the next start (the node's settings could not be written: disk full): "
            + "after a restart it would not wait to hear them before it sends, and none that took its show could fence it", row.Text);
        Assert.False(row.Ok);
        Assert.Equal("primary", state.GetProperty("fleet").GetProperty("role").GetString());
        Assert.True(pushedBefore, "no push of STATE was raised between the role's move and the row");
        Assert.Equal(FleetRoles.Solo, primary.Home.LoadSettings("x").Fleet.Role);
    }

    /// <summary>A node that handed its show back mirrors the node that took it back, under the epoch it took it with, for this run though
    /// its settings file refuses the write: the row of the hand-back says the epoch is not kept for the next start, and STATE holds the
    /// hand-back's record and the epoch, and has been pushed, by the time that row is written.</summary>
    [Fact]
    public async Task ANodeWhoseSettingsCannotKeepTheEpochOfItsHandBackSaysSoInItsRow()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);
        var takeover = await backup.RunAsync("FAILOVER");
        Assert.True(takeover.Ok, takeover.Payload);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(primary) == "fenced"), BackupRig.Fleet(primary) + "\n" + primary.Journal());
        await BackupRig.BackUpAsync(backup, primary);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(backup).GetProperty("backups").EnumerateArray()
            .Any(b => b.GetProperty("node").GetString() == primary.Id && b.GetProperty("state").GetString() == "identical")), BackupRig.Fleet(backup).ToString());

        backup.Home.WriteFile = (_, _) => throw new IOException("disk full");
        var handedBack = new RowWatch(backup, "fleet", "took the show back (epoch 2)", () => backup.Engine.Services.Backup!.Last?.Kind == "failback");
        var back = await backup.RunAsync("FAILBACK CONFIRM");
        Assert.True(back.Ok, back.Payload);
        var (row, state, pushedBefore) = await handedBack.SeenAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("FOH-A took the show back (epoch 2): FOH-B is its hot backup again — not kept for the next start (the node's settings could "
            + "not be written: disk full)", row.Text);
        Assert.False(row.Ok);
        var fleet = state.GetProperty("fleet");
        Assert.Equal(("backup", primary.Id, 2L, "failback"), (fleet.GetProperty("role").GetString(), fleet.GetProperty("of").GetString(),
            fleet.GetProperty("epoch").GetInt64(), fleet.GetProperty("last").GetProperty("kind").GetString()));
        Assert.Equal(row.Text, fleet.GetProperty("last").GetProperty("words").GetString());
        Assert.True(pushedBefore, "no push of STATE was raised between the hand-back's record and its row");
        // The file holds what it held before the hand-back: the primary under the takeover's epoch.
        Assert.Equal((FleetRoles.Primary, 1L), (backup.Home.LoadSettings("x").Fleet.Role, backup.Home.LoadSettings("x").Fleet.Epoch));
    }

    /// <summary>The journal's recent rows that hold <paramref name="text"/>, oldest first.</summary>
    private static List<string> Rows(HostRig rig, string text) => [.. rig.Journal().Split('\n').Where(r => r.Contains(text, StringComparison.Ordinal))];

    private static JsonElement Air(HostRig rig) => rig.State().GetProperty("air");

    private static MirrorWebModule Mirror(HostRig rig) => rig.Engine.Services.Backup!.Web!;
}
