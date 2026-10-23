using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Net.Media;
using PlayMap.Tests;

namespace PlayMap.Engine.Tests.Host;

/// <summary>Two real nodes in one process finding each other on the hello: one leads a sync group and the other takes
/// its clock; one holds a file and the other fetches it by its hash. A fetch from a peer that falls silent says so and ends
/// with its words; one from a peer that lists the file twice still lands it; one from a peer whose list fails ends each file
/// with the list's words; one that moves on to another peer keeps the chunks it has and says them at once; and a fetch of many
/// files from a peer that refuses every chunk or whose list fails journals each pull's first failed try and its end up to the
/// fetch's bound and one row that counts the rest, so the rows before it stay in the context's recent rows however many files it
/// asks. And a node
/// following a Patterns desk: its blackout reaches the air through the engine's own fact path, the follow switch decides, and the
/// link is kept in the node's settings with its token — which never reaches STATE or the journal.</summary>
public sealed class HostNetworkTests
{
    /// <summary>The follower's leader time agrees with the leader's own within this (loopback: a fraction of it).</summary>
    private const double ToleranceMs = 5;

    [Fact]
    public async Task AFollowerTakesItsLeadersClockWithinFiveMilliseconds()
    {
        // The leader's monotonic clock is five seconds ahead of the follower's: another machine's.
        const long ahead = 5_000_000_000;
        await using var leader = await HostRig.StartAsync("Stage left", network: true, mono: new OffsetClock(MonoClock.Instance, ahead));
        await using var follower = await HostRig.StartAsync("Stage right", network: true);
        HostRig.Introduce(leader, follower);
        Assert.True(await Eventually.TrueAsync(() => follower.Network.Hello!.Peers.Find(leader.Id) is not null));

        await leader.OkAsync("SYNC LEAD show");
        Assert.True(await Eventually.TrueAsync(() => leader.State().GetProperty("sync").GetProperty("role").GetString() == "leader"));
        // The leader's hello names its sync port once it answers on it.
        Assert.True(await Eventually.TrueAsync(() => follower.Network.Hello!.Peers.Find(leader.Id)?.Hello.Port("sync") == leader.Network.SyncPort));

        await follower.OkAsync("SYNC FOLLOW show " + leader.Id);
        Assert.True(await Eventually.TrueAsync(() => follower.State().GetProperty("sync").GetProperty("role").GetString() == "follower"));
        Assert.Equal(leader.Id, follower.State().GetProperty("sync").GetProperty("leader").GetString());

        // Bracketed by two reads of the leader's clock, so a pause between the reads widens the window, never the error.
        double error = double.NaN;
        Assert.True(await Eventually.TrueAsync(() =>
        {
            var before = leader.Engine.LeaderNowNs;
            var read = follower.Engine.LeaderNowNs;
            var after = leader.Engine.LeaderNowNs;
            error = read < before ? (before - read) / 1e6 : read > after ? (read - after) / 1e6 : 0;
            return error <= ToleranceMs;
        }), $"the follower's leader time is {error:0.###} ms outside the leader's");
        // STATE's offset is the reading's uncertainty, half the best round trip — not the clocks' five-second gap.
        var sync = follower.State().GetProperty("sync");
        Assert.Equal(sync.GetProperty("rttMs").GetDouble() / 2, sync.GetProperty("offsetMs").GetDouble(), 0.01);
        Assert.InRange(sync.GetProperty("offsetMs").GetDouble(), 0, 1000);
        Assert.True(await Eventually.TrueAsync(() => follower.Journal().Contains("following Stage left", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FetchPullsAFileByItsHashFromAPeerAndVerifiesIt()
    {
        // The holder has the file in its media store before it starts: the index finds it by its bytes.
        var holderDir = Directory.CreateTempSubdirectory("playmap-host-");
        var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Directory.CreateDirectory(Path.Combine(holderDir.FullName, "media"));
        await File.WriteAllBytesAsync(Path.Combine(holderDir.FullName, "media", "clip.bin"), bytes);
        await using var holder = await HostRig.StartAsync("Stage left", network: true, dir: holderDir);
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true);
        Assert.True(await Eventually.TrueAsync(() => holder.Host.Media.Lookup(hex) is not null));

        var id = await fetcher.OkAsync("ADD media {\"name\":\"clip\",\"kind\":\"video\",\"location\":\"/elsewhere/clip.bin\",\"contentHash\":\"sha256:" + hex + "\",\"sizeBytes\":" + bytes.Length + "}");
        Assert.Equal("no", await fetcher.OkAsync("MEDIA HAS sha256:" + hex));
        Assert.True(await Eventually.TrueAsync(() => fetcher.State().GetProperty("media").GetProperty("missing").GetInt32() == 1));

        HostRig.Introduce(holder, fetcher);
        Assert.True(await Eventually.TrueAsync(() => fetcher.Network.Hello!.Peers.Find(holder.Id) is not null));
        var asked = Stopwatch.StartNew();
        var reply = await fetcher.RunAsync($"MEDIA FETCH {id} FROM {holder.Id}");
        Assert.True(reply.Ok, reply.Payload);
        Assert.False(reply.Applied);   // accepted: the pull runs beside the engine

        // A file that does not land says where its pull stood: its row (state, stage and since, bytes, the peer and its address, the
        // last try's words), how long after the fetch, the thread pool, and both nodes' journals and the tails of their logs (their
        // folders go with them).
        string Saw() => string.Create(CultureInfo.InvariantCulture,
            $"{asked.ElapsedMilliseconds} ms after MEDIA FETCH; transfers {Transfers(fetcher)}; pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} items queued\n"
            + $"fetcher's journal:\n{fetcher.Journal()}\nfetcher's log:\n{fetcher.LogTail()}\nholder's journal:\n{holder.Journal()}\nholder's log:\n{holder.LogTail(10)}");
        Assert.True(await Eventually.TrueAsync(() => fetcher.Host.Media.PathForHash("sha256:" + hex) is not null, 20000), Saw());
        var landed = fetcher.Host.Media.PathForHash("sha256:" + hex)!;
        Assert.StartsWith(fetcher.Home.MediaStore, landed, StringComparison.Ordinal);
        Assert.Equal(hex, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(landed))));
        Assert.Equal("yes", await fetcher.OkAsync("MEDIA HAS sha256:" + hex));
        Assert.True(await Eventually.TrueAsync(() => fetcher.State().GetProperty("media").GetProperty("missing").GetInt32() == 0));
        // The file is a fact first; the journal's row and the transfer's leaving follow it.
        Assert.True(await Eventually.TrueAsync(() => fetcher.Journal().Contains("pulled from Stage left and verified", StringComparison.Ordinal)));
        Assert.True(await Eventually.TrueAsync(() => fetcher.State().GetProperty("media").GetProperty("transfers").GetArrayLength() == 0));
    }

    [Fact]
    public async Task AFetchFromAPeerThatFallsSilentSaysSoAndEndsWithItsWords()
    {
        var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
        // The peer answers the list and each try's headers, sends 64 KiB, and then nothing while it holds the connection open. The
        // node's clock is the test's: a try's silence is counted only as the test moves it.
        await using var peer = new FakeMediaPeer(hex, bytes, stallAfter: 64 * 1024);
        var clock = new ManualClock(Base);
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true, mono: clock, pulls: new MediaTransferOptions { Quiet = Quiet });
        var id = await fetcher.OkAsync("ADD media {\"name\":\"clip\",\"kind\":\"video\",\"location\":\"/elsewhere/clip.bin\",\"contentHash\":\"sha256:" + hex + "\",\"sizeBytes\":" + bytes.Length + "}");
        Hear(fetcher, peer, "5113e7a0", "Silent peer");
        Assert.True((await fetcher.RunAsync($"MEDIA FETCH {id} FROM 5113e7a0")).Ok);
        var at = "127.0.0.1:" + peer.Port.ToString(CultureInfo.InvariantCulture);

        // The first try that met the silence is said while the pull tries again, in the journal and on the transfer's row; the clock
        // then stands still, so the next try waits on the silent peer and the row stays on its way.
        Assert.True(await DriveAsync(clock, () => fetcher.Journal().Contains("trying again (2 of 3)", StringComparison.Ordinal)), fetcher.Journal());
        var row = Transfer(fetcher);
        Assert.Equal(("pulling", "receiving", "Silent peer", at), (Str(row, "state"), Str(row, "stage"), Str(row, "from"), Str(row, "at")));
        Assert.StartsWith($"chunk 1 of 1 from {at}: sent nothing for 1 s, ", Str(row, "note"), StringComparison.Ordinal);
        Assert.Contains($"media: clip from Silent peer: chunk 1 of 1 from {at}: sent nothing for 1 s, ", fetcher.Journal(), StringComparison.Ordinal);

        // The last try's words end the pull: a refused row and a failed transfer, the tries between said on the row and in the log
        // only, and the file can be asked for again.
        Assert.True(await DriveAsync(clock, () => fetcher.Journal().Contains("clip could not be fetched", StringComparison.Ordinal)), fetcher.Journal());
        Assert.Single(Rows(fetcher, "trying again"));
        Assert.Contains($"refused: clip could not be fetched: Silent peer: chunk 1 of 1 from {at} could not be had: sent nothing for 1 s, ", fetcher.Journal(),
            StringComparison.Ordinal);
        Assert.Equal(3, peer.FileAsks);
        Assert.StartsWith("failed: Silent peer: chunk 1 of 1 from ", Str(Transfer(fetcher), "state"), StringComparison.Ordinal);
        Hear(fetcher, peer, "5113e7a0", "Silent peer");
        var again = await fetcher.RunAsync($"MEDIA FETCH {id} FROM 5113e7a0");
        Assert.True(again.Ok, again.Payload);
    }

    [Fact]
    public async Task AFetchOfSeveralFilesFromAPeerWhoseListFailsEndsEachWithTheListsWords()
    {
        var files = Enumerable.Range(0, 2).Select(_ => RandomNumberGenerator.GetBytes(100_000)).Select(b => (Hash: Convert.ToHexStringLower(SHA256.HashData(b)), File: b)).ToList();
        await using var peer = new FakeMediaPeer(files, listStatus: 503);
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true);
        for (var k = 0; k < files.Count; k++)
            await AddAsync(fetcher, string.Create(CultureInfo.InvariantCulture, $"clip{k}"), files[k].Hash, files[k].File.Length);
        Hear(fetcher, peer, "11571e55", "Dead list");
        Assert.True((await fetcher.RunAsync("MEDIA FETCH ALL FROM 11571e55")).Ok);

        Assert.True(await Eventually.TrueAsync(() => Rows(fetcher, "could not be fetched").Count == 2, 20000), fetcher.Journal());
        const string Words = "Dead list's media list could not be read: Response status code does not indicate success: 503 (Refused).";
        Assert.Equal(["refused: clip0 could not be fetched: " + Words, "refused: clip1 could not be fetched: " + Words], Rows(fetcher, "could not be fetched"));
        Assert.All(fetcher.State().GetProperty("media").GetProperty("transfers").EnumerateArray(), t => Assert.Equal("failed: " + Words, Str(t, "state")));
        Assert.Equal(1, peer.ListAsks);
    }

    [Fact]
    public async Task AFetchOfManyFilesFromAPeerThatRefusesEveryChunkJournalsEachPullsFirstFailedTryAndItsEndUpToItsBound()
    {
        // Each file is four chunks, three in flight, two tries each, and every try is refused. A pull's failed tries are its row's to
        // carry and the log's, and the journal takes its first and its end; the files are pulled one after another, so the fetch's rows
        // are the first files' two each, and one more row counts the rest, which went to the node's log only.
        const int Files = 16;
        var files = Enumerable.Range(0, Files).Select(_ => RandomNumberGenerator.GetBytes(4 * 64 * 1024)).Select(b => (Hash: Convert.ToHexStringLower(SHA256.HashData(b)), File: b))
            .ToList();
        await using var peer = new FakeMediaPeer(files, fileStatus: 503);
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true, pulls: new MediaTransferOptions { ChunkBytes = 64 * 1024, ChunkAttempts = 2 });
        for (var k = 0; k < Files; k++)
            await AddAsync(fetcher, Clip(k), files[k].Hash, files[k].File.Length);
        Hear(fetcher, peer, "dead0001", "Refuser");
        Assert.True((await fetcher.RunAsync("MEDIA FETCH ALL FROM dead0001")).Ok);

        var ended = string.Create(CultureInfo.InvariantCulture,
            $"refused: the fetch of 16 files from Refuser ended: 0 pulled and verified, 16 could not be fetched; {2 * Files - FetchJournal.Most} of its rows are in the node's log only");
        Assert.True(await Eventually.TrueAsync(() => Rows(fetcher, "the fetch of").Count == 1, 60000), fetcher.Journal());
        // Whichever of the three chunks in flight is refused first is the one said.
        var at = "127.0.0.1:" + peer.Port.ToString(CultureInfo.InvariantCulture);
        var tried = Rows(fetcher, " from Refuser: chunk ");
        var refused = Rows(fetcher, " could not be fetched: ");
        Assert.Equal((FetchJournal.Most / 2, FetchJournal.Most / 2), (tried.Count, refused.Count));
        for (var k = 0; k < FetchJournal.Most / 2; k++)
        {
            Assert.Matches("^" + Regex.Escape($"media: {Clip(k)} from Refuser: chunk ") + "[123]" + Regex.Escape($" of 4 from {at}: answered 503 — trying again (2 of 2)") + "$", tried[k]);
            Assert.StartsWith($"refused: {Clip(k)} could not be fetched: Refuser: chunk ", refused[k], StringComparison.Ordinal);
        }
        Assert.Equal([ended], Rows(fetcher, "the fetch of"));
        // Every file's row says how it ended, and the log holds the rows the journal does not: the node's log writes on a thread of its
        // own, so the last file's line is waited for there.
        Assert.Equal(Files, fetcher.State().GetProperty("media").GetProperty("transfers").EnumerateArray()
            .Count(t => Str(t, "state").StartsWith("failed: Refuser: chunk ", StringComparison.Ordinal)));
        Assert.True(await Eventually.TrueAsync(() => fetcher.LogTail(200).Contains($"{Clip(Files - 1)} could not be fetched: Refuser: chunk ", StringComparison.Ordinal)),
            fetcher.LogTail());
        Assert.Single(Rows(fetcher, "Stage right is up"));
    }

    [Fact]
    public async Task AFetchOfMoreFilesThanTheJournalHoldsFromAPeerWhoseListFailsLeavesTheNodesOwnRows()
    {
        // Two hundred and fifty files, one verb's row to open them, and a peer whose list fails: one refused row a file would push
        // every other row out of the journal's recent ones. The fetch journals its first few and one that counts the rest.
        const int Files = 250;
        var hashes = Enumerable.Range(0, Files).Select(k => Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(k)))).ToList();
        await using var peer = new FakeMediaPeer([(hashes[0], new byte[16])], listStatus: 503);
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true);
        var show = SyncRig.Show() with
        {
            Media = [.. Enumerable.Range(0, Files).Select(k => new MediaItem(string.Create(CultureInfo.InvariantCulture, $"md{k:000}"), Clip(k), MediaKind.Video,
                $"/elsewhere/{Clip(k)}.bin", "sha256:" + hashes[k], 1000, MediaInfo.Unknown, []))],
        };
        await SyncRig.OpenShowAsync(fetcher, show);
        Hear(fetcher, peer, "11571e55", "Dead list");
        Assert.True((await fetcher.RunAsync("MEDIA FETCH ALL FROM 11571e55")).Ok);

        Assert.True(await Eventually.TrueAsync(() => Rows(fetcher, "the fetch of").Count == 1, 60000), fetcher.Journal());
        const string Words = "Dead list's media list could not be read: Response status code does not indicate success: 503 (Refused).";
        Assert.Equal([.. Enumerable.Range(0, FetchJournal.Most).Select(k => $"refused: {Clip(k)} could not be fetched: " + Words)], Rows(fetcher, " could not be fetched: "));
        var ended = string.Create(CultureInfo.InvariantCulture,
            $"refused: the fetch of 250 files from Dead list ended: 0 pulled and verified, 250 could not be fetched; {Files - FetchJournal.Most} of its rows are in the node's log only");
        Assert.Equal([ended], Rows(fetcher, "the fetch of"));
        Assert.Single(Rows(fetcher, "Stage right is up"));
        Assert.Single(Rows(fetcher, "verb: opened Sync test"));
        // The context's recent rows, the window of the journal that MCP's get_context, Patterns and the assistant read, still hold the
        // verb that opened the show: the fetch's rows there are its verb's own, its pulls' first few and the one that counts the rest.
        var context = ContextRows(fetcher);
        Assert.Contains(context, r => r.StartsWith("opened Sync test", StringComparison.Ordinal));
        Assert.Equal(FetchJournal.Most, context.Count(r => r.Contains(" could not be fetched: ", StringComparison.Ordinal)));
        Assert.Equal(ended, context[^1]);
        Assert.Equal(Files, fetcher.State().GetProperty("media").GetProperty("transfers").EnumerateArray().Count(t => Str(t, "state") == "failed: " + Words));
        Assert.Equal(1, peer.ListAsks);
    }

    [Fact]
    public async Task AFetchsRowsFollowTheVerbAndWhatWasQueuedWhenItsPeerEndsEveryPullInsideTheFetch()
    {
        // A peer whose hello names no address it can be asked at: every pull ends at it without a request, so the whole fetch (each
        // file refused, then the row that counts the rest) ends inside the node's own call that queued it. The verb's row comes first,
        // then what was queued, then every pull's row: none of the pulls begins before the rows that began it.
        const int Files = 6;
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true);
        for (var k = 0; k < Files; k++)
            await AddAsync(fetcher, Clip(k), Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(k))), 1000);
        fetcher.Network.Hello!.Peers.Hear(new ShowBusHello(ShowBusConstants.KindPlayMap, 1, 1, "a0d0e55e", "Nowhere", "elsewhere",
            new Dictionary<string, int> { ["http"] = 8080 }, ShowBusHello.PlayMapCaps, 0, null, null), "no address");
        var before = fetcher.Engine.Journal.Recent(200).Count;

        var reply = await fetcher.RunAsync("MEDIA FETCH ALL FROM a0d0e55e");

        Assert.True(reply.Ok, reply.Payload);
        var ended = string.Create(CultureInfo.InvariantCulture,
            $"the fetch of {Files} files from Nowhere ended: 0 pulled and verified, {Files} could not be fetched; {Files - FetchJournal.Most} of its rows are in the node's log only");
        List<(string Kind, string Text)> expected =
        [
            ("verb", $"fetching {Files} files"),
            ("media", $"fetching {Files} files from Nowhere"),
            .. Enumerable.Range(0, FetchJournal.Most).Select(k => ("refused", $"{Clip(k)} could not be fetched: no heard node holds it")),
            ("refused", ended),
        ];
        Assert.True(await Eventually.TrueAsync(() => Rows(fetcher, "the fetch of").Count == 1), fetcher.Journal());
        bool OfTheFetch(string text) => text.StartsWith("fetching ", StringComparison.Ordinal) || text.Contains(" could not be fetched: ", StringComparison.Ordinal)
            || text.StartsWith("the fetch of ", StringComparison.Ordinal);
        var rows = fetcher.Engine.Journal.Recent(200).Skip(before).Where(r => OfTheFetch(r.Text)).ToList();
        Assert.Equal(expected, rows.Select(r => (r.Kind, r.Text)));
        Assert.Equal("MEDIA FETCH ALL FROM a0d0e55e", rows[0].Line);
        // The context's recent rows say them in the same order.
        Assert.Equal([.. expected.Select(e => e.Kind == "refused" ? e.Kind + ": " + e.Text : e.Text)],
            ContextRows(fetcher).Where(t => OfTheFetch(t.StartsWith("refused: ", StringComparison.Ordinal) ? t["refused: ".Length..] : t)));
        Assert.Equal(Files, fetcher.State().GetProperty("media").GetProperty("transfers").EnumerateArray()
            .Count(t => Str(t, "state") == "failed: no heard node holds it"));
    }

    [Fact]
    public async Task AFetchThatMovesOnToAnotherPeerKeepsTheChunksItHasAndSaysThemAtOnce()
    {
        // Three chunks of 64 KiB, one at a time, one try each. Keeper gives the first chunk and then falls silent; the next peer, Silent,
        // falls silent at once. The node's clock is the test's, moved only while Keeper is asked and once its chunk is in, so the pull at
        // Silent waits for good: its row says the chunk the part file keeps, not 0.
        const int Chunk = 64 * 1024;
        var bytes = RandomNumberGenerator.GetBytes(3 * Chunk);
        var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var keeper = new FakeMediaPeer(hex, bytes, stallFrom: Chunk);
        await using var silent = new FakeMediaPeer(hex, bytes, stallAfter: 0);
        var clock = new ManualClock(Base);
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true, mono: clock,
            pulls: new MediaTransferOptions { Quiet = Quiet, ChunkBytes = Chunk, Parallelism = 1, ChunkAttempts = 1 });
        var id = await AddAsync(fetcher, "clip", hex, bytes.Length);
        Hear(fetcher, keeper, "0ee9e700", "Keeper");
        Hear(fetcher, silent, "5113e7a0", "Silent");
        Assert.True((await fetcher.RunAsync("MEDIA FETCH " + id)).Ok);

        var sw = Stopwatch.StartNew();
        while (silent.FileAsks == 0 && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (Transfers(fetcher) is var seen && seen.Contains("\"from\":\"Keeper\"", StringComparison.Ordinal) && seen.Contains("\"bytesDone\":65536", StringComparison.Ordinal))
                clock.Advance(Quiet.Ticks * TimeSpan.NanosecondsPerTick + 1_000_000);
            await Task.Delay(50);
        }

        Assert.True(silent.FileAsks > 0, $"transfers {Transfers(fetcher)}\n{fetcher.Journal()}");
        var row = Transfer(fetcher);
        Assert.Equal(("pulling", "receiving", "Silent", (long)Chunk), (Str(row, "state"), Str(row, "stage"), Str(row, "from"), row.GetProperty("bytesDone").GetInt64()));
        Assert.Equal((1, 2, 1, 1), (keeper.ListAsks, keeper.FileAsks, silent.ListAsks, silent.FileAsks));
    }

    /// <summary>The quiet the fault tests give a try.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    /// <summary>Where a hand-moved clock starts.</summary>
    private const long Base = 1_000_000_000_000;

    private static string Clip(int k) => string.Create(CultureInfo.InvariantCulture, $"clip{k:00}");

    private static Task<string> AddAsync(HostRig rig, string name, string hex, int size) =>
        rig.OkAsync("ADD media {\"name\":\"" + name + "\",\"kind\":\"video\",\"location\":\"/elsewhere/" + name + ".bin\",\"contentHash\":\"sha256:" + hex + "\",\"sizeBytes\":"
            + size.ToString(CultureInfo.InvariantCulture) + "}");

    /// <summary>The journal's rows that hold <paramref name="text"/>, oldest first.</summary>
    private static List<string> Rows(HostRig rig, string text) =>
        [.. rig.Journal().Split('\n').Where(r => r.Contains(text, StringComparison.Ordinal))];

    /// <summary>The context's recent rows as it says them, oldest first: the journal's window that MCP's get_context, Patterns and the
    /// assistant read.</summary>
    private static List<string> ContextRows(HostRig rig) => [.. ContextBuilder.Build(rig.Engine).Recent.Select(r => r.Text)];

    /// <summary>Moves the node's clock a quiet and a millisecond at a time, and looks for 200 ms after each step, until
    /// <paramref name="done"/> holds: each step ends the try then waiting on a silent peer, and a step taken while none waits counts
    /// nothing.</summary>
    private static async Task<bool> DriveAsync(ManualClock clock, Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (done())
                return true;
            clock.Advance(Quiet.Ticks * TimeSpan.NanosecondsPerTick + 1_000_000);
            if (await Eventually.TrueAsync(done, 200))
                return true;
        }
        return done();
    }

    [Fact]
    public async Task AFetchFromAPeerThatListsTheFileTwiceStillLandsIt()
    {
        var bytes = RandomNumberGenerator.GetBytes(200_000);
        var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var peer = new FakeMediaPeer(hex, bytes, listed: 2);
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true);
        var id = await fetcher.OkAsync("ADD media {\"name\":\"clip\",\"kind\":\"video\",\"location\":\"/elsewhere/clip.bin\",\"contentHash\":\"sha256:" + hex + "\",\"sizeBytes\":" + bytes.Length + "}");
        Hear(fetcher, peer, "d0b1e7a0", "Twice");
        Assert.True((await fetcher.RunAsync($"MEDIA FETCH {id} FROM d0b1e7a0")).Ok);

        Assert.True(await Eventually.TrueAsync(() => fetcher.Host.Media.PathForHash("sha256:" + hex) is not null, 20000),
            $"transfers {Transfers(fetcher)}\n{fetcher.Journal()}");
        Assert.True(await Eventually.TrueAsync(() => fetcher.Journal().Contains("clip pulled from Twice and verified", StringComparison.Ordinal)));
    }

    /// <summary>A fake PlayMap peer heard on the fetcher's hello at loopback, its web port the fake's.</summary>
    private static void Hear(HostRig rig, FakeMediaPeer peer, string id, string name) =>
        rig.Network.Hello!.Peers.Hear(new ShowBusHello(ShowBusConstants.KindPlayMap, 1, 1, id, name, "elsewhere", new Dictionary<string, int> { ["http"] = peer.Port },
            ShowBusHello.PlayMapCaps, 0, null, null), "127.0.0.1");

    private static string Transfers(HostRig rig) => rig.State().GetProperty("media").GetProperty("transfers").ToString();

    private static JsonElement Transfer(HostRig rig) => Assert.Single(rig.State().GetProperty("media").GetProperty("transfers").EnumerateArray());

    private static string Str(JsonElement e, string key) => e.GetProperty(key).GetString() ?? "";

    /// <summary>The node's clock is the test's: the desk sends its STATE once, at connect, and a running clip with no fresh STATE is
    /// trusted for 3 s of that clock, so the clip the link counts on stands while the test reads it, however late it reads.</summary>
    [Fact]
    public async Task ThePatternsLinkDrivesTheBlackoutAndKeepsItsTokenOffEveryLine()
    {
        const string deskToken = "AAAA-BBBB-CCCC";
        await using var desk = new FakePatternsDesk(deskToken);
        await using var rig = await HostRig.StartAsync(mono: new ManualClock(Base));
        await using var c = await rig.WireAsync();
        var link = await c.AskAsync($"@l LINK PATTERNS 127.0.0.1:{desk.Port} {deskToken}");
        Assert.StartsWith("OK @l {\"stage\":\"accepted\"", link, StringComparison.Ordinal);

        JsonElement Patterns() => rig.State().GetProperty("links").GetProperty("patterns");
        JsonElement Air() => rig.State().GetProperty("air");
        Assert.True(await Eventually.TrueAsync(() => Patterns().GetProperty("state").GetString() == "live" && Patterns().GetProperty("paired").GetBoolean()));
        Assert.True(await Eventually.TrueAsync(() => Patterns().GetProperty("clip").GetString()!.StartsWith("VT ", StringComparison.Ordinal)));
        Assert.Contains(desk.Heard, l => l.Contains(" AUTH-PROOF ", StringComparison.Ordinal));
        Assert.False(Air().GetProperty("blackout").GetBoolean());

        await desk.PushAsync(FakePatternsDesk.StateWith(blackout: true));
        Assert.True(await Eventually.TrueAsync(() => Air().GetProperty("blackout").GetBoolean()));
        Assert.True(Air().GetProperty("patternsBlackout").GetBoolean());

        // The follow switch decides whether the desk's blackout is this node's.
        Assert.StartsWith("OK @f", await c.AskAsync("@f LINK PATTERNS FOLLOW-BLACKOUT OFF"), StringComparison.Ordinal);
        Assert.False(Air().GetProperty("blackout").GetBoolean());
        Assert.False(Air().GetProperty("followPatternsBlackout").GetBoolean());
        Assert.StartsWith("ERR @g already ignoring", await c.AskAsync("@g LINK PATTERNS FOLLOW-BLACKOUT OFF"), StringComparison.Ordinal);
        Assert.StartsWith("OK @h", await c.AskAsync("@h LINK PATTERNS FOLLOW-BLACKOUT ON"), StringComparison.Ordinal);
        Assert.True(Air().GetProperty("blackout").GetBoolean());

        await desk.PushAsync(FakePatternsDesk.StateWith(blackout: false));
        Assert.True(await Eventually.TrueAsync(() => !Air().GetProperty("blackout").GetBoolean()));

        // Kept for the next start, token and all — in the node's settings, and nowhere a peer reads. Every write of the file landed
        // (one a reader could refuse would leave a settings fault in STATE, a fault row in the journal and the link off the disk).
        Assert.DoesNotContain(rig.State().GetProperty("faults").EnumerateArray(), f => f.GetProperty("part").GetString() == "settings");
        Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Kind == "fault");
        var kept = rig.Home.LoadSettings("unused");
        Assert.Equal($"127.0.0.1:{desk.Port}", kept.PatternsHost);
        Assert.Equal(deskToken, kept.PatternsToken);
        Assert.DoesNotContain(deskToken, rig.Engine.State().Json, StringComparison.Ordinal);
        Assert.DoesNotContain(deskToken, rig.Engine.Context(), StringComparison.Ordinal);
        Assert.DoesNotContain(deskToken, rig.Journal(), StringComparison.Ordinal);
        Assert.True(await Eventually.TrueAsync(() => rig.Journal().Contains("following Patterns at 127.0.0.1", StringComparison.Ordinal)));

        Assert.StartsWith("OK @o", await c.AskAsync("@o LINK PATTERNS OFF"), StringComparison.Ordinal);
        Assert.True(await Eventually.TrueAsync(() => Patterns().GetProperty("state").GetString() == "off"));
        Assert.True(await Eventually.TrueAsync(() => rig.Home.LoadSettings("unused") is { PatternsHost: "", PatternsToken: "" }));
    }

    /// <summary>The Patterns link counts the desk's silence on the node's clock. A desk silent for longer than the stale mark of real time
    /// while that clock stands still leaves the link live on its first connection; past the stale mark on the clock it is stale on the
    /// same connection, and past the drop mark it drops and dials the desk again.</summary>
    [Fact]
    public async Task ThePatternsLinkCountsTheDesksSilenceOnTheNodesClock()
    {
        const string deskToken = "AAAA-BBBB-CCCC";
        await using var desk = new FakePatternsDesk(deskToken);
        var clock = new ManualClock(Base);
        await using var rig = await HostRig.StartAsync(mono: clock);
        await using var c = await rig.WireAsync();
        Assert.StartsWith("OK @l {\"stage\":\"accepted\"", await c.AskAsync($"@l LINK PATTERNS 127.0.0.1:{desk.Port} {deskToken}"), StringComparison.Ordinal);
        JsonElement Patterns() => rig.State().GetProperty("links").GetProperty("patterns");
        string Word() => Patterns().GetProperty("state").GetString() ?? "";
        Assert.True(await Eventually.TrueAsync(() => Word() == "live" && Patterns().GetProperty("paired").GetBoolean()), Word());

        desk.Silent = true;
        await Task.Delay(TimeSpan.FromMilliseconds(ShowBusConstants.StaleMs + 500));
        Assert.Equal(("live", 1), (Word(), desk.Accepted));

        clock.AdvanceMs(ShowBusConstants.StaleMs + 500);
        Assert.True(await Eventually.TrueAsync(() => Word() == "stale"), Word());
        Assert.Equal(1, desk.Accepted);

        clock.AdvanceMs(ShowBusConstants.GoneMs - ShowBusConstants.StaleMs);
        Assert.True(await Eventually.TrueAsync(() => desk.Accepted == 2, 30000), $"{Word()} after {desk.Accepted} connections");
    }
}
