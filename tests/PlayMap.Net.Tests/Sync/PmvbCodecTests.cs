using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Numerics;
using PlayMap.Core.Sync;
using PlayMap.Core.Time;
using PlayMap.Net.Sync;

namespace PlayMap.Net.Tests.Sync;

/// <summary>The PMVB vblank report: a report reads back as itself; every field out of its range, any other length, version or magic
/// is refused (fail closed); 100 000 random datagrams from a seeded generator are all refused; and on the wire the clock-sync
/// responder hands a report to its clock's sink only from an address that asked it for the time.</summary>
public sealed class PmvbCodecTests
{
    private static readonly VblankReport Good = new(LockState.Framelocked, VblankReport.FlagSettled | VblankReport.FlagBoard | VblankReport.FlagBoardSynced,
        VblankReport.PathDxgiFlip, 0x9f8e7d6c, VblankReport.HashId("op_1"), 7, 0x123456, 16_666_712_000, 123_456_789_000_000, 9_876_543,
        2, 59_940, 33_000, 210);

    private static byte[] Bytes(in VblankReport r)
    {
        var b = new byte[VblankReport.Size];
        r.Write(b);
        return b;
    }

    [Fact]
    public void AReportReadsBackAsItself()
    {
        var b = Bytes(Good);
        Assert.Equal(64, b.Length);
        Assert.Equal((byte)'P', b[0]);
        Assert.Equal((byte)'M', b[1]);
        Assert.Equal((byte)'V', b[2]);
        Assert.Equal((byte)'B', b[3]);
        Assert.True(VblankReport.TryRead(b, out var back));
        Assert.Equal(Good, back);
        Assert.Equal("9f8e7d6c", back.NodeText);
        Assert.Equal(0x9f8e7d6cu, VblankReport.NodeNumber("9f8e7d6c"));
        Assert.Equal(VblankReport.HashId("node-x"), VblankReport.NodeNumber("node-x"));
        // FNV-1a 32 of "a" is a well-known value.
        Assert.Equal(0xe40c292cu, VblankReport.HashId("a"));
    }

    public static TheoryData<string, int, long> Malformed => new()
    {
        { "version 2", 4, 2 },
        { "version 0", 4, 0 },
        { "state 8 (beyond joining)", 5, 8 },
        { "state 255", 5, 255 },
        { "path 3", 7, 3 },
        { "board synced without a board", 6, VblankReport.FlagBoardSynced },
        { "a frame count over 24 bits", 20, 0x01000000 },
        { "a period under 1 ms (1001 Hz)", 24, VblankReport.MinPeriodPs - 1 },
        { "a period over 50 ms (19.99 Hz)", 24, VblankReport.MaxPeriodPs + 1 },
        { "a period of zero", 24, 0 },
        { "a negative vblank instant", 32, -1 },
        { "more missed vblanks than a minute has", 48, VblankReport.MaxMissed60s + 1 },
        { "house sync over 1000 Hz", 52, VblankReport.MaxHouseSyncMilliHz + 1 },
        { "a negative air latency", 56, -1 },
        { "an air latency over 500 ms", 56, VblankReport.MaxAirLatencyUs + 1 },
        { "a clock uncertainty over a second", 60, VblankReport.MaxClockUncertaintyUs + 1 },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void EveryFieldOutOfItsRangeIsRefused(string why, int offset, long value)
    {
        var b = Bytes(Good);
        switch (offset)
        {
            case 4 or 5 or 6 or 7:
                b[offset] = (byte)value;
                break;
            case 24 or 32:
                BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(offset), value);
                break;
            case 56:
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), (int)value);
                break;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), (uint)value);
                break;
        }
        Assert.False(VblankReport.TryRead(b, out _), why);
    }

    [Fact]
    public void AnyOtherLengthOrMagicIsRefused()
    {
        var b = Bytes(Good);
        for (var n = 0; n < 128; n++)
        {
            var other = new byte[n];
            b.AsSpan(0, Math.Min(n, 64)).CopyTo(other);
            Assert.Equal(n == 64, VblankReport.TryRead(other, out _));
        }
        for (var i = 0; i < 4; i++)
        {
            var c = (byte[])b.Clone();
            c[i] ^= 0x20;
            Assert.False(VblankReport.TryRead(c, out _), $"magic byte {i}");
            Assert.False(VblankReport.Claims(c));
        }
        // The clock exchange's own packet is not a report.
        Assert.False(VblankReport.Claims(new SyncPacket(SyncPacket.Request, 1, 2, 0, 0).ToBytes()));
    }

    [Fact]
    public void AHundredThousandRandomDatagramsAreAllRefusedAndEveryMutationReadsBackOnlyAsWhatItSays()
    {
        var rng = new Pcg32(20261015);
        var b = new byte[64];
        var accepted = 0;
        for (var i = 0; i < 100_000; i++)
        {
            for (var j = 0; j < 64; j += 4)
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(j), rng.NextUInt());
            if (VblankReport.TryRead(b, out _))
                accepted++;
        }
        Assert.Equal(0, accepted);

        // The same with the magic and the version right, so every datagram reaches the field checks: still none taken.
        for (var i = 0; i < 100_000; i++)
        {
            for (var j = 0; j < 64; j += 4)
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(j), rng.NextUInt());
            BinaryPrimitives.WriteUInt32LittleEndian(b, VblankReport.Magic);
            b[4] = VblankReport.Version;
            if (VblankReport.TryRead(b, out _))
                accepted++;
        }
        Assert.Equal(0, accepted);

        // A good report with one byte changed is either refused or reads back as exactly those bytes: never guessed at.
        var good = Bytes(Good);
        for (var i = 0; i < 100_000; i++)
        {
            var c = (byte[])good.Clone();
            c[(int)(rng.NextUInt() % 64)] = (byte)rng.NextUInt();
            if (VblankReport.TryRead(c, out var r))
                Assert.Equal(c, Bytes(r));
        }
    }

    private sealed class Sink : IVblankSink
    {
        public List<(VblankReport Report, IPAddress From)> Heard { get; } = [];

        public void Offer(in VblankReport report, IPAddress from)
        {
            lock (Heard)
                Heard.Add((report, from));
        }
    }

    [Fact]
    public async Task TheResponderHandsAReportToItsClocksSinkOnlyFromAnAddressThatAskedForTheTime()
    {
        var clock = new NetworkClock(MonoClock.Instance);
        var sink = new Sink();
        VblankSinks.Attach(clock, sink);
        await using var responder = new ClockSyncResponder(TestSettings.Make(), clock, port: 0);
        responder.Start();
        var leader = new IPEndPoint(IPAddress.Loopback, responder.Port);
        using var member = new VblankReporter(leader);

        // Never asked for the time: refused, and the responder still answers the clock exchange.
        Assert.True(member.Send(Good));
        Assert.True(await Eventually.TrueAsync(() => responder.Refused == 1));
        Assert.Empty(sink.Heard);

        // A member that follows the clock has asked: its reports reach the sink (from the reporter's own socket's address).
        await using var follower = new ClockSyncClient(MonoClock.Instance, TimeSpan.FromMilliseconds(20));
        await follower.FollowAsync(leader);
        Assert.True(await Eventually.TrueAsync(() => responder.Answered > 0));
        Assert.True(member.Send(Good with { Seq = 8 }));
        Assert.True(await Eventually.TrueAsync(() => responder.Reports == 1));
        Assert.Equal(8u, sink.Heard.Single().Report.Seq);
        Assert.Equal(IPAddress.Loopback, sink.Heard.Single().From);

        // A claimed report that does not read is refused and counted.
        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var bad = Bytes(Good);
        bad[4] = 9;
        await raw.SendToAsync(bad, SocketFlags.None, leader);
        Assert.True(await Eventually.TrueAsync(() => responder.Refused == 2));
        Assert.Single(sink.Heard);
        VblankSinks.Detach(clock, sink);
    }
}
