using System.Collections.Concurrent;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>The backup's link to its primary, its silence counted on the backup's own clock, two real nodes on loopback: a mirror whose
/// primary is heard nothing from past the stale mark is lost while it is quiet and followed again with the next line on the same
/// connection (a subscription lives as long as its connection); a connection that ends takes its subscription, and the next one
/// subscribes anew.</summary>
public sealed class BackupMirrorLinkTests
{
    private const long Second = SyncRig.Second;

    private const string Quiet = "FOH-A's mirror has said nothing for over 6 s — the last show and air hold here";

    private const string Down = "FOH-A's mirror is down — the last show and air hold here";

    private const string NotSubscribed = "FOH-A's mirror is not subscribed — the last show and air hold here";

    [Fact]
    public async Task AMirrorQuietPastTheStaleMarkIsFollowedAgainWithItsNextLineAndADroppedOneSubscribesAnew()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
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
        // The backup's reader is held on the next MIRROR line it reads (after the node has taken it), so nothing more is heard from the
        // primary until the test lets it go.
        using var release = new ManualResetEventSlim();
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

        // An edit on the primary, its line read and the reader held there; the backup's clock moves past the stale mark: the mirror is
        // lost while it is quiet, on the same connection.
        await primary.OkAsync("SET op_1 name Quiet");
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(6 * Second + Second / 2);
        Assert.True(await Eventually.TrueAsync(() => !seen.IsEmpty), BackupRig.Saw(backup));
        Assert.Equal((LinkStatus.Stale, "lost", Quiet), seen.First());
        Assert.Equal(1L, link.Connections);
        // The reader goes on: the next line on the same connection finds the mirror followed again, and the edit mirrored.
        release.Set();
        Assert.True(await Eventually.TrueAsync(() => link.Status == LinkStatus.Live && BackupRig.MirrorState(backup) == "identical"
            && BackupRig.Fleet(backup).GetProperty("mirror").GetProperty("rev").GetInt64() == primary.Engine.Store.Current.Revision
            && BackupRig.ShowJson(backup) == BackupRig.ShowJson(primary)), BackupRig.Saw(backup));
        Assert.Equal(1L, link.Connections);

        // A connection that ends takes its subscription: lost while the link is down and dials again, lost on the new connection until
        // its subscription is taken, identical once it is.
        var before = seen.Count;
        link.Drop();
        Assert.True(await Eventually.TrueAsync(() => link.Connections == 2 && BackupRig.MirrorState(backup) == "identical"), BackupRig.Saw(backup));
        var redial = new (LinkStatus Status, string Mirror, string Words)[]
        {
            (LinkStatus.Down, "lost", Down), (LinkStatus.Connecting, "lost", Down), (LinkStatus.Live, "lost", NotSubscribed),
        };
        Assert.Equal(redial, seen.Skip(before).Take(3));
    }
}
