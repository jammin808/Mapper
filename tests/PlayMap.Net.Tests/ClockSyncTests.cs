using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Time;
using PlayMap.Net.Sync;

namespace PlayMap.Net.Tests;

/// <summary>PlayMap's fine clock sync on loopback: two clocks that agree converge to an offset of about zero with a
/// small round trip, a leader five seconds ahead is found within a couple of milliseconds, and anything that is not
/// a well-formed packet answering one of ours is refused.</summary>
public class ClockSyncTests
{
    /// <summary>A monotonic clock moved by a fixed amount (another machine's clock).</summary>
    private sealed class OffsetClock(IMonoClock inner, long offsetNs) : IMonoClock
    {
        public long NowNs => inner.NowNs + offsetNs;
    }

    private static async Task<(ClockSyncResponder Leader, ClockSyncClient Follower)> PairAsync(long leaderOffsetNs)
    {
        var leader = new ClockSyncResponder(TestSettings.Make(), new NetworkClock(new OffsetClock(MonoClock.Instance, leaderOffsetNs)), port: 0);
        leader.Start();
        var follower = new ClockSyncClient(MonoClock.Instance, TimeSpan.FromMilliseconds(20));
        await follower.FollowAsync(new IPEndPoint(IPAddress.Loopback, leader.Port));
        return (leader, follower);
    }

    /// <summary>Converges, then quiets the leader and answers the follower's estimate once it holds still. The estimate
    /// moves with every exchange (the window's best sample changes), so a bound read at one moment and a clock read at
    /// another can belong to two different estimates; with no exchange in flight both are the same one.</summary>
    private static async Task<SyncSnapshot> ConvergeThenQuietAsync(ClockSyncResponder leader, ClockSyncClient follower)
    {
        try
        {
            Assert.True(await Eventually.TrueAsync(() => follower.Snapshot.Samples >= 8));
            // The responder counts an answer once its send has returned, which can be after the follower has taken that answer in.
            Assert.True(await Eventually.TrueAsync(() => leader.Answered >= 8), $"answered {leader.Answered}");
        }
        finally
        {
            await leader.DisposeAsync();
        }
        var before = follower.Snapshot;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            var now = follower.Snapshot;
            if (now.OffsetNs == before.OffsetNs && now.RoundTripNs == before.RoundTripNs)
                return now;
            before = now;
        }
        throw new TimeoutException("the follower's estimate kept moving after its leader went quiet");
    }

    [Fact]
    public async Task TwoClocksThatAgreeConvergeToNoOffsetAndASmallRoundTrip()
    {
        var (leader, follower) = await PairAsync(0);
        await using (follower)
        {
            var s = await ConvergeThenQuietAsync(leader, follower);
            var bound = 1 + s.RoundTripMs / 2;   // an exchange's offset is exact to within half its round trip
            Assert.InRange(s.OffsetMs, -bound, bound);
            Assert.InRange(s.RoundTripMs, 0, 20);
            Assert.NotEqual(SyncQuality.None, s.Quality);
            Assert.NotEqual(SyncQuality.Acquiring, s.Quality);
            Assert.True(follower.Clock.IsFollowing);
            // One local instant read through the follower's clock: no time passes between the two sides of the comparison.
            var local = MonoClock.Instance.NowNs;
            Assert.InRange(Math.Abs(follower.Clock.ToLeader(local) - local) / 1e6, 0, 3 + s.RoundTripMs);
        }
    }

    [Fact]
    public async Task ALeaderFiveSecondsAheadIsFoundToWithinMilliseconds()
    {
        const long ahead = 5_000_000_000;
        var (leader, follower) = await PairAsync(ahead);
        await using (follower)
        {
            var s = await ConvergeThenQuietAsync(leader, follower);
            var bound = 1e6 + s.RoundTripNs / 2.0;
            Assert.InRange(s.OffsetNs, ahead - bound, ahead + bound);
            var local = MonoClock.Instance.NowNs;
            Assert.InRange((follower.Clock.ToLeader(local) - (local + ahead)) / 1e6, -3 - s.RoundTripMs, 3 + s.RoundTripMs);
            await follower.StopAsync();
            Assert.False(follower.Clock.IsFollowing);
            Assert.Equal(SyncQuality.None, follower.Snapshot.Quality);
        }
    }

    [Fact]
    public async Task AMalformedPacketIsRefusedAndAStrayReplyIsDropped()
    {
        await using var leader = new ClockSyncResponder(TestSettings.Make(), new NetworkClock(MonoClock.Instance), port: 0);
        leader.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var to = new IPEndPoint(IPAddress.Loopback, leader.Port);
        await udp.SendAsync(new byte[] { 1, 2, 3 }, to);
        var reply = new SyncPacket(SyncPacket.Reply, 1, 10, 20, 30).ToBytes();
        await udp.SendAsync(reply, to);
        var badMagic = new SyncPacket(SyncPacket.Request, 2, 10, 0, 0).ToBytes();
        badMagic[0] ^= 0xFF;
        await udp.SendAsync(badMagic, to);
        var request = new SyncPacket(SyncPacket.Request, 3, 10, 0, 0).ToBytes();
        await udp.SendAsync(request, to);
        var answer = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SyncPacket.TryRead(answer.Buffer, out var pong));
        Assert.Equal((SyncPacket.Reply, 3u, 10L), (pong.Kind, pong.Seq, pong.T1));
        Assert.True(pong.T3 >= pong.T2);
        Assert.True(await Eventually.TrueAsync(() => leader.Refused == 3));
        Assert.False(SyncPacket.TryRead(new SyncPacket(SyncPacket.Reply, 1, 1, 9, 2).ToBytes(), out _));

        // A reply nobody asked for, sent to a follower, is dropped and moves nothing.
        await using var follower = new ClockSyncClient(MonoClock.Instance, TimeSpan.FromHours(1));
        await follower.FollowAsync(new IPEndPoint(IPAddress.Loopback, udp.Client.LocalEndPoint is IPEndPoint ep ? ep.Port : 0));
        var ask = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SyncPacket.TryRead(ask.Buffer, out var asked));
        await udp.SendAsync(new SyncPacket(SyncPacket.Reply, asked.Seq + 7, asked.T1, 5, 6).ToBytes(), ask.RemoteEndPoint);
        await udp.SendAsync(new SyncPacket(SyncPacket.Reply, asked.Seq, asked.T1 + 1, 5, 6).ToBytes(), ask.RemoteEndPoint);
        Assert.True(await Eventually.TrueAsync(() => follower.Strays == 2));
        Assert.False(follower.Clock.IsFollowing);
    }
}
