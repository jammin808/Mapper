using System.Collections.Concurrent;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Net.Fleet;

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

    [Fact]
    public async Task AMirrorQuietPastTheStaleMarkIsFollowedAgainWithItsNextLineAndADroppedOneSubscribesAnew()
    {
        // The gate the backup's reader is held on: declared before the nodes, so it outlives their close, and set on every way out
        // before they stop (the finally below), so a failing assertion reports instead of holding the backup's close on its reader.
        using var release = new ManualResetEventSlim();
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
            var holding = HoldNextLine(link, release);

            // An edit on the primary, its line read and the reader held there; the backup's clock moves past the stale mark: the
            // mirror is lost while it is quiet, on the same connection.
            await primary.OkAsync("SET op_1 name Quiet");
            await holding.WaitAsync(TimeSpan.FromSeconds(10));
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
        // The gate the backup's reader is held on, as above: it outlives the nodes and is set on every way out before they stop.
        using var release = new ManualResetEventSlim();
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
            var holding = HoldNextLine(link, release);

            // The primary's line read and the reader held there, the backup's clock past the stale mark: preflight reads the mirror lost
            // with the quiet words and offers no line, its subscription standing; FLEET BACKUP is refused then and changes nothing.
            await primary.OkAsync("SET op_1 name Quiet");
            await holding.WaitAsync(TimeSpan.FromSeconds(10));
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
        // The gate the backup's greeting is held on: it outlives the nodes and is set on every way out before they stop.
        using var release = new ManualResetEventSlim();
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
            var greeting = HoldNextSubscribe(link, release);

            // The connection ends; the next one is live with its greeting held before it subscribes, and the backup's clock moves past
            // the stale mark with no subscription standing: the mirror is lost and not subscribed, and offered the line that joins it
            // anew. An answer the primary gave before the clock moved (to the connection's first ping) that is read after it starts the
            // silence again, so the clock moves once more then.
            link.Drop();
            await greeting.WaitAsync(TimeSpan.FromSeconds(10));
            for (var moves = 0; moves < 2 && !seen.Any(e => e.Status == LinkStatus.Stale); moves++)
            {
                clock.Advance(6 * Second + Second / 2);
                Assert.True(await Eventually.TrueAsync(() => seen.Any(e => e.Status == LinkStatus.Stale) || link.Silence < TimeSpan.FromSeconds(6)),
                    BackupRig.Saw(backup));
            }
            var quiet = seen.FirstOrDefault(e => e.Status == LinkStatus.Stale);
            Assert.Equal((LinkStatus.Stale, "lost", NotSubscribed, "FLEET BACKUP " + BackupRig.PrimaryId), quiet);
            Assert.Equal(2L, link.Connections);
            // Preflight's row says the same while the greeting is held, the link quiet or live again with the next answer.
            var row = await MirrorRowAsync(backup);
            Assert.Equal((Light.Red, "lost", NotSubscribed, "FLEET BACKUP " + BackupRig.PrimaryId), (row.Light, row.Value, row.Note, row.Fix));

            // The greeting goes on: the primary takes the subscription on the same connection and the mirror is identical again.
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
        // The gate the backup's reader is held on: it outlives the nodes and is set on every way out before they stop.
        using var release = new ManualResetEventSlim();
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
            var holding = HoldNextLine(backup.Engine.Services.Backup!.Link!, release);

            // The backup's reader held on the primary's line and its clock past the stale mark: its mirror is lost, its health says so, and
            // the primary lights it red alike in STATE, on INDEX's key for it and in preflight's row for it.
            await primary.OkAsync("SET op_1 name Quiet");
            await holding.WaitAsync(TimeSpan.FromSeconds(10));
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

    /// <summary>Holds the backup's greeting on the link's next connection just before it subscribes, until <paramref name="release"/> is
    /// set, so no subscription stands on that connection meanwhile; the task ends once the greeting is held.</summary>
    private static Task HoldNextSubscribe(MirrorLink link, ManualResetEventSlim release)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 1;
        link.Subscribing += () =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                holding.TrySetResult();
                release.Wait();
            }
        };
        return holding.Task;
    }

    /// <summary>Holds the backup's reader on the next mirror line it reads (after the node has taken it) until <paramref name="release"/>
    /// is set, so nothing more is heard from the primary meanwhile; the task ends once the reader is held.</summary>
    private static Task HoldNextLine(MirrorLink link, ManualResetEventSlim release)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 1;
        link.Line += _ =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                holding.TrySetResult();
                release.Wait();
            }
        };
        return holding.Task;
    }
}
