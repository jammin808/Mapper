using System.Collections.Concurrent;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Net.Fleet;
using PlayMap.Net.Links;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>The backup's link to its primary, its silence counted on the backup's own clock, two real nodes on loopback: a mirror whose
/// primary is heard nothing from past the stale mark is lost while it is quiet and followed again with the next line on the same
/// connection (a subscription lives as long as its connection); a connection that ends takes its subscription, and the next one
/// subscribes anew. Preflight offers a quiet mirror no line (its subscription stands, which FLEET BACKUP refuses) and one whose
/// connection ended the line that joins it anew.</summary>
public sealed class BackupMirrorLinkTests
{
    private const long Second = SyncRig.Second;

    private const string Quiet = "FOH-A has said nothing on its mirror for over 6 s — the last show and air hold here";

    private const string Down = "FOH-A's mirror is down — the last show and air hold here";

    private const string NotSubscribed = "FOH-A's mirror is not subscribed — the last show and air hold here";

    /// <summary>How long a test waits to see the backup's reader or greeting held: the product's way to the hold, on a thread of the
    /// test's own (<see cref="OwnThread.WaitAsync"/>).</summary>
    private static readonly TimeSpan HoldPatience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AMirrorQuietPastTheStaleMarkIsFollowedAgainWithItsNextLineAndADroppedOneSubscribesAnew()
    {
        // The gate the backup's reader is held on: declared before the nodes, so it outlives their close, and set on every way out
        // before they stop (the finally below), so a failing assertion reports instead of holding the backup's close on its reader.
        // The reader sets held as it is held; declared here too, so no reader sets it once it is gone.
        using var release = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        try
        {
            await SyncRig.OpenShowAsync(primary);
            await primary.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(primary, backup);
            await BackupRig.BackUpAsync(primary, backup);
            var node = backup.Engine.Services.Backup!;
            var link = node.Link!;
            // What the mirror says at each of the link's moves, read as the link moves.
            var seen = new ConcurrentQueue<(LinkStatus Status, string Mirror, string Words)>();
            link.StatusChanged += s =>
            {
                var m = node.Mirror()!;
                seen.Enqueue((s, m.State, m.Words));
            };
            HoldNextLine(link, held, release);

            // An edit on the primary, its line read and the reader held there; the backup's clock moves past the stale mark: the
            // mirror is lost while it is quiet, on the same connection.
            await primary.OkAsync("SET op_1 name Quiet");
            await OwnThread.WaitAsync("the backup's reader held on the primary's line", held, HoldPatience);
            clock.Advance(6 * Second + Second / 2);
            Assert.True(await Eventually.TrueAsync(() => !seen.IsEmpty), BackupRig.Saw(backup));
            Assert.Equal((LinkStatus.Stale, "lost", Quiet), seen.First());
            Assert.Equal(1L, link.Connections);
            // The reader goes on: the next line on the same connection moves the link live again (that move recorded), the mirror is
            // followed again and the edit mirrored.
            release.Set();
            Assert.True(await Eventually.TrueAsync(() => seen.Count >= 2 && BackupRig.MirrorState(backup) == "identical"
                && BackupRig.Fleet(backup).GetProperty("mirror").GetProperty("rev").GetInt64() == primary.Engine.Store.Current.Revision
                && BackupRig.ShowJson(backup) == BackupRig.ShowJson(primary)), BackupRig.Saw(backup));
            Assert.Equal([LinkStatus.Stale, LinkStatus.Live], seen.Select(e => e.Status));
            Assert.Equal(1L, link.Connections);

            // A connection that ends takes its subscription: lost while the link is down and dials again, lost on the new connection
            // until its subscription is taken, identical once it is.
            link.Drop();
            Assert.True(await Eventually.TrueAsync(() => link.Connections == 2 && BackupRig.MirrorState(backup) == "identical"), BackupRig.Saw(backup));
            var redial = new (LinkStatus Status, string Mirror, string Words)[]
            {
                (LinkStatus.Down, "lost", Down), (LinkStatus.Connecting, "lost", Down), (LinkStatus.Live, "lost", NotSubscribed),
            };
            Assert.Equal(redial, seen.Skip(2).Take(3));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AQuietMirrorIsOfferedNoLineInPreflightAndADroppedOneTheLineThatJoinsItAnew()
    {
        // The gate the backup's reader is held on, as above: it outlives the nodes and is set on every way out before they stop; held
        // with it.
        using var release = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        try
        {
            await SyncRig.OpenShowAsync(primary);
            await primary.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(primary, backup);
            await BackupRig.BackUpAsync(primary, backup);
            var link = backup.Engine.Services.Backup!.Link!;
            HoldNextLine(link, held, release);

            // The primary's line read and the reader held there, the backup's clock past the stale mark: preflight reads the mirror lost
            // with the quiet words and offers no line, its subscription standing; FLEET BACKUP is refused then and changes nothing.
            await primary.OkAsync("SET op_1 name Quiet");
            await OwnThread.WaitAsync("the backup's reader held on the primary's line", held, HoldPatience);
            clock.Advance(6 * Second + Second / 2);
            Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Stale), BackupRig.Saw(backup));
            var quiet = await MirrorRowAsync(backup);
            Assert.Equal((Light.Red, "lost", Quiet, (string?)null), (quiet.Light, quiet.Value, quiet.Note, quiet.Fix));
            var refused = await backup.RunAsync("FLEET BACKUP " + primary.Id);
            Assert.False(refused.Ok, refused.Payload);
            Assert.StartsWith("already a hot backup of FOH-A", refused.Payload, StringComparison.Ordinal);
            Assert.Equal((LinkStatus.Stale, 1L, "lost"), (link.Status, link.Connections, BackupRig.MirrorState(backup)));
            release.Set();
            Assert.True(await Eventually.TrueAsync(() => BackupRig.MirrorState(backup) == "identical"), BackupRig.Saw(backup));

            // The primary stops: its connection ends and takes the subscription, and preflight offers the line that joins the mirror
            // anew.
            await primary.StopAsync();
            Assert.True(await Eventually.TrueAsync(() => link.Status is LinkStatus.Down or LinkStatus.Connecting), BackupRig.Saw(backup));
            var down = await MirrorRowAsync(backup);
            Assert.Equal((Light.Red, "lost", Down, "FLEET BACKUP " + BackupRig.PrimaryId), (down.Light, down.Value, down.Note, down.Fix));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AMirrorQuietBeforeItsNewConnectionIsSubscribedSaysItIsNotSubscribedAndIsOfferedTheLineThatJoinsItAnew()
    {
        // The gate the backup's reader and greeting are held on: it outlives the nodes and is set on every way out before they stop;
        // what each sets as it is held, with it.
        using var release = new ManualResetEventSlim();
        using var paired = new ManualResetEventSlim();
        using var greeting = new ManualResetEventSlim();
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        try
        {
            await SyncRig.OpenShowAsync(primary);
            await primary.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(primary, backup);
            await BackupRig.BackUpAsync(primary, backup);
            var node = backup.Engine.Services.Backup!;
            var link = node.Link!;
            // What the mirror says at each of the link's moves, and the line preflight's row offers with it, read as the link moves.
            var seen = new ConcurrentQueue<(LinkStatus Status, string Mirror, string Words, string? Line)>();
            link.StatusChanged += s =>
            {
                var m = node.Mirror()!;
                seen.Enqueue((s, m.State, m.Words, m.Rejoin));
            };
            HoldNextPairing(link, paired, release);
            HoldNextSubscribe(link, greeting, release);

            // The connection ends; the next one pairs, its reader held on the primary's answer to the proof and its greeting held before
            // it subscribes, so nothing more is heard on it (the primary's STATE pushes and answers wait unread) and no subscription
            // stands; the backup's clock moves past the stale mark: the mirror is lost and not subscribed, and offered the line that
            // joins it anew.
            link.Drop();
            await OwnThread.WaitAsync("the backup's reader held on the primary's answer that pairs it", paired, HoldPatience);
            await OwnThread.WaitAsync("the backup's greeting held before it subscribes", greeting, HoldPatience);
            clock.Advance(6 * Second + Second / 2);
            Assert.True(await Eventually.TrueAsync(() => seen.Any(e => e.Status == LinkStatus.Stale)), BackupRig.Saw(backup));
            var quiet = seen.FirstOrDefault(e => e.Status == LinkStatus.Stale);
            Assert.Equal((LinkStatus.Stale, "lost", NotSubscribed, "FLEET BACKUP " + BackupRig.PrimaryId), quiet);
            Assert.Equal(2L, link.Connections);
            // Preflight's row says the same while the reader and the greeting are held.
            var row = await MirrorRowAsync(backup);
            Assert.Equal((Light.Red, "lost", NotSubscribed, "FLEET BACKUP " + BackupRig.PrimaryId), (row.Light, row.Value, row.Note, row.Fix));

            // The reader and the greeting go on: the primary takes the subscription on the same connection and the mirror is identical
            // again.
            release.Set();
            Assert.True(await Eventually.TrueAsync(() => BackupRig.MirrorState(backup) == "identical"), BackupRig.Saw(backup));
            Assert.Equal(2L, link.Connections);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task APrimaryLightsABackupWhoseMirrorIsLostRedInStateOnItsKeyAndInPreflightAlike()
    {
        // The gate the backup's reader is held on: it outlives the nodes and is set on every way out before they stop; held with it.
        using var release = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        try
        {
            await SyncRig.OpenShowAsync(primary);
            await primary.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(primary, backup);
            await BackupRig.BackUpAsync(primary, backup);
            HoldNextLine(backup.Engine.Services.Backup!.Link!, held, release);

            // The backup's reader held on the primary's line and its clock past the stale mark: its mirror is lost, its health says so, and
            // the primary lights it red alike in STATE, on INDEX's key for it and in preflight's row for it.
            await primary.OkAsync("SET op_1 name Quiet");
            await OwnThread.WaitAsync("the backup's reader held on the primary's line", held, HoldPatience);
            clock.Advance(6 * Second + Second / 2);
            Assert.True(await Eventually.TrueAsync(() => BackupState(primary) == "lost"), BackupRig.Fleet(primary).ToString());
            Assert.Equal(("lost", "red", "lost", "red", "lost", Light.Red), await LightsAsync(primary, backup.Id));

            // The reader goes on: identical again, and green alike.
            release.Set();
            Assert.True(await Eventually.TrueAsync(() => BackupState(primary) == "identical"), BackupRig.Fleet(primary).ToString());
            Assert.Equal(("identical", "green", "identical", "green", "identical", Light.Green), await LightsAsync(primary, backup.Id));
        }
        finally
        {
            release.Set();
        }
    }

    private static string? BackupState(HostRig primary) =>
        BackupRig.Fleet(primary).GetProperty("backups").EnumerateArray().SingleOrDefault().GetProperty("state").GetString();

    /// <summary>How a primary shows its one backup: STATE.fleet.backups' state and light, INDEX's key for it, and preflight's row for it.</summary>
    private static async Task<(string?, string?, string?, string?, string, Light)> LightsAsync(HostRig primary, string backupId)
    {
        var listed = BackupRig.Fleet(primary).GetProperty("backups").EnumerateArray().Single();
        var key = (await BackupRig.IndexAsync(primary)).Single(r => r.GetProperty("kind").GetString() == "backup");
        var row = (await BackupRig.PreflightAsync(primary)).Single(r => r.Id == "backup:" + backupId);
        return (listed.GetProperty("state").GetString(), listed.GetProperty("light").GetString(), key.GetProperty("state").GetString(),
            key.GetProperty("light").GetString(), row.Value, row.Light);
    }

    private static async Task<PreflightRow> MirrorRowAsync(HostRig backup) =>
        (await BackupRig.PreflightAsync(backup)).Single(r => r.Id == "backup:mirror");

    // Each hold below sets its held event where the hold is entered, then holds the link's thread there (a pool worker: the reader or
    // the greeting) until release is set. An event set queues nothing, so the held worker's own queue keeps nothing the test needs;
    // a task source's continuation, queued there, would wait until another worker took it, which a pool that never goes idle does not.

    /// <summary>Holds the backup's reader on the link's next connection as it reads the primary's answer that pairs it, until
    /// <paramref name="release"/> is set, so nothing more is heard from the primary on that connection meanwhile; sets
    /// <paramref name="held"/> as the reader is held.</summary>
    private static void HoldNextPairing(MirrorLink link, ManualResetEventSlim held, ManualResetEventSlim release)
    {
        var hold = 1;
        link.PairingChanged += s =>
        {
            if (s == PairState.Paired && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait();
            }
        };
    }

    /// <summary>Holds the backup's greeting on the link's next connection just before it subscribes, until <paramref name="release"/> is
    /// set, so no subscription stands on that connection meanwhile; sets <paramref name="held"/> as the greeting is held.</summary>
    private static void HoldNextSubscribe(MirrorLink link, ManualResetEventSlim held, ManualResetEventSlim release)
    {
        var hold = 1;
        link.Subscribing += () =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait();
            }
        };
    }

    /// <summary>Holds the backup's reader on the next mirror line it reads (after the node has taken it) until <paramref name="release"/>
    /// is set, so nothing more is heard from the primary meanwhile; sets <paramref name="held"/> as the reader is held.</summary>
    private static void HoldNextLine(MirrorLink link, ManualResetEventSlim held, ManualResetEventSlim release)
    {
        var hold = 1;
        link.Line += _ =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait();
            }
        };
    }
}
