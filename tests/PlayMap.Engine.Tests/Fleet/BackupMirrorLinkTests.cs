using System.Collections.Concurrent;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Net.Fleet;

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
