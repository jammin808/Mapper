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

    private static async Task<PreflightRow> MirrorRowAsync(HostRig backup) =>
        (await BackupRig.PreflightAsync(backup)).Single(r => r.Id == "backup:mirror");

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
