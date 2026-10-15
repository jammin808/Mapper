using System.Buffers.Binary;
using PlayMap.Core.Control;
using PlayMap.Core.Dmx;

namespace PlayMap.Core.Tests.Dmx;

/// <summary>sACN (ANSI E1.31-2018) read and written from the standard's tables, and the receiver's merge: the highest priority wins,
/// equal priorities merge highest-takes-precedence, a source that terminates or falls silent for 2.5 s gives way to those left, late
/// packets are dropped by the §6.7.2 rule, preview data is ignored, and synchronized data waits for its synchronization packet.</summary>
public sealed class SacnTests
{
    private static readonly Guid ConsoleA = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
    private static readonly Guid ConsoleB = Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8");
    private static readonly Guid ConsoleC = Guid.Parse("6ba7b812-9dad-11d1-80b4-00c04fd430c8");

    private const uint AddressA = 0x0A000005; // 10.0.0.5
    private const uint AddressB = 0x0A000006;

    /// <summary>A full-universe data packet laid out by hand from E1.31 Table 4-1 (no writer of ours involved).</summary>
    private static byte[] HandBuilt(byte[] slots)
    {
        var p = new byte[638];
        // Root layer: preamble size 0x0010, post-amble size 0, the ACN packet identifier, flags and length 0x726e, vector 4, CID.
        p[1] = 0x10;
        "ASC-E1.17\0\0\0"u8.CopyTo(p.AsSpan(4));
        p[16] = 0x72; p[17] = 0x6E;
        p[21] = 0x04;
        ConsoleA.TryWriteBytes(p.AsSpan(22, 16), bigEndian: true, out _);
        // Framing layer: flags and length 0x7258, vector 2, source name, priority 100, sync address 0, sequence 7, options 0, universe 1.
        p[38] = 0x72; p[39] = 0x58;
        p[43] = 0x02;
        "Lighting desk"u8.CopyTo(p.AsSpan(44));
        p[108] = 100;
        p[111] = 7;
        p[114] = 1;
        // DMP layer: flags and length 0x720b, vector 2, type 0xa1, first address 0, increment 1, count 0x0201, START code 0, slots.
        p[115] = 0x72; p[116] = 0x0B;
        p[117] = 0x02;
        p[118] = 0xA1;
        p[122] = 0x01;
        p[123] = 0x02; p[124] = 0x01;
        slots.CopyTo(p.AsSpan(126));
        return p;
    }

    private static byte[] Data(Guid cid, int universe, byte sequence, int priority, byte[] levels, int sync = 0, bool preview = false, bool terminated = false)
    {
        var buffer = new byte[638];
        var n = Sacn.WriteData(buffer, new SacnData(cid, cid == ConsoleA ? "A" : "B", priority, sync, sequence, preview, terminated, false, universe, 0, levels.Length), levels);
        return buffer[..n];
    }

    private static byte[] Levels(params (int Channel, byte Value)[] set)
    {
        var l = new byte[512];
        foreach (var (c, v) in set)
            l[c - 1] = v;
        return l;
    }

    [Fact]
    public void AHandBuiltPacketReadsAndOursIsByteForByteTheSame()
    {
        var slots = Levels((1, 255), (2, 128), (512, 7));
        var packet = HandBuilt(slots);
        Assert.Equal(SacnKind.Data, Sacn.KindOf(packet));
        Assert.True(Sacn.TryReadData(packet, out var h, out var read));
        Assert.Equal(new SacnData(ConsoleA, "Lighting desk", 100, 0, 7, false, false, false, 1, 0, 512), h);
        Assert.Equal(slots, read.ToArray());
        var ours = new byte[638];
        Assert.Equal(638, Sacn.WriteData(ours, h, slots));
        Assert.Equal(packet, ours);
    }

    [Fact]
    public void ASyncPacketIsFortyNineBytesAndReadsBack()
    {
        var buffer = new byte[64];
        Assert.Equal(49, Sacn.WriteSync(buffer, new SacnSync(ConsoleB, 9, 7)));
        var packet = buffer[..49];
        // Root flags and length 0x7021 with the extended vector 8; framing 0x700b with the synchronization vector 1.
        Assert.Equal((0x7021, 8u, 0x700B, 1u), (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(18)),
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(38)), BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(40))));
        Assert.Equal(SacnKind.Sync, Sacn.KindOf(packet));
        Assert.True(Sacn.TryReadSync(packet, out var sync));
        Assert.Equal(new SacnSync(ConsoleB, 9, 7), sync);
    }

    [Fact]
    public void WhatIsMalformedIsRefused()
    {
        var good = HandBuilt(new byte[512]);
        static byte[] With(byte[] p, int at, byte value)
        {
            var c = (byte[])p.Clone();
            c[at] = value;
            return c;
        }

        Assert.False(Sacn.TryReadData(With(good, 4, (byte)'X'), out _, out _));          // not "ASC-E1.17"
        Assert.False(Sacn.TryReadData(With(good, 108, 201), out _, out _));              // priority above 200
        Assert.False(Sacn.TryReadData(With(good, 114, 0), out _, out _));                // universe 0
        Assert.False(Sacn.TryReadData(With(good, 17, 0x6D), out _, out _));              // root length off by one
        Assert.False(Sacn.TryReadData(With(good, 124, 0x00), out _, out _));             // property count does not match
        Assert.False(Sacn.TryReadData(With(good, 118, 0xA2), out _, out _));             // not address and data type 0xa1
        Assert.False(Sacn.TryReadData(good.AsSpan(0, 400), out _, out _));               // cut short
        Assert.Equal(SacnKind.OtherStartCode, Sacn.KindOf(With(good, 125, 0xDD)));
        Assert.Equal(SacnKind.None, Sacn.KindOf(new byte[10]));
        Assert.Equal("239.255.0.1", Sacn.GroupOf(1).ToString());
        Assert.Equal("239.255.249.255", Sacn.GroupOf(63999).ToString());
    }

    [Fact]
    public void TheHighestPriorityWinsEqualOnesMergeHighestAndATerminatedSourceGivesWay()
    {
        var input = new SacnDmxInput();
        input.Want([new DmxChannel(1, 1, 0), new DmxChannel(1, 2, 3)]);
        var events = new List<InputEvent>();
        Assert.Equal(SacnInputResult.Dmx, input.Receive(Data(ConsoleA, 1, 1, 100, Levels((1, 200), (2, 1), (3, 0))), AddressA, 0, events));
        Assert.Equal([(200, "10.0.0.5"), (256, "10.0.0.5")], events.OrderBy(e => e.Address.Number).Select(e => (e.Raw, e.Origin)));

        // B at priority 150 wins with lower levels.
        events.Clear();
        input.Receive(Data(ConsoleB, 1, 1, 150, Levels((1, 20))), AddressB, 1_000_000, events);
        Assert.Equal([(20, "10.0.0.6"), (0, "10.0.0.6")], events.OrderBy(e => e.Address.Number).Select(e => (e.Raw, e.Origin)));

        // B terminates: A takes over again.
        events.Clear();
        Assert.Equal(SacnInputResult.Terminated, input.Receive(Data(ConsoleB, 1, 2, 150, Levels(), terminated: true), AddressB, 2_000_000, events));
        Assert.Equal([200, 256], events.OrderBy(e => e.Address.Number).Select(e => e.Raw));

        // C at A's priority: highest takes precedence per channel, a 16-bit pair as one value.
        events.Clear();
        input.Receive(Data(ConsoleC, 1, 1, 100, Levels((1, 100), (2, 1), (3, 5))), AddressB, 3_000_000, events);
        Assert.Equal([261], events.Select(e => e.Raw));
        Assert.Equal(16, events[0].Bits);
        Assert.Equal(261 / 65535.0, events[0].Value, 12);
    }

    [Fact]
    public void LatePacketsAreDroppedByTheStandardsRule()
    {
        var input = new SacnDmxInput();
        input.Want([new DmxChannel(1, 1, 0)]);
        var events = new List<InputEvent>();
        input.Receive(Data(ConsoleA, 1, 10, 100, Levels((1, 1))), AddressA, 0, events);
        // 0 to 19 behind the last: late.
        Assert.Equal(SacnInputResult.OutOfOrder, input.Receive(Data(ConsoleA, 1, 10, 100, Levels((1, 2))), AddressA, 1, events));
        Assert.Equal(SacnInputResult.OutOfOrder, input.Receive(Data(ConsoleA, 1, 9, 100, Levels((1, 3))), AddressA, 2, events));
        Assert.Equal(SacnInputResult.OutOfOrder, input.Receive(Data(ConsoleA, 1, 247, 100, Levels((1, 4))), AddressA, 3, events));
        // 20 or more behind is a restarted source; ahead (across the wrap) is in order.
        Assert.Equal(SacnInputResult.Dmx, input.Receive(Data(ConsoleA, 1, 246, 100, Levels((1, 5))), AddressA, 4, events));
        Assert.Equal(SacnInputResult.Dmx, input.Receive(Data(ConsoleA, 1, 2, 100, Levels((1, 6))), AddressA, 5, events));
        Assert.Equal(3, input.Dropped);
        Assert.Equal([1, 5, 6], events.Select(e => e.Raw));
    }

    [Fact]
    public void ASilentSourceIsGoneAfterTwoAndAHalfSecondsAndTheLastOneLeftHolds()
    {
        var input = new SacnDmxInput();
        input.Want([new DmxChannel(1, 1, 0)]);
        var events = new List<InputEvent>();
        input.Receive(Data(ConsoleA, 1, 1, 100, Levels((1, 50))), AddressA, 0, events);
        input.Receive(Data(ConsoleB, 1, 1, 120, Levels((1, 90))), AddressB, 1_000_000_000, events);
        events.Clear();
        // A is kept alive; B falls silent and is gone 2.5 s after it was last heard.
        input.Receive(Data(ConsoleA, 1, 2, 100, Levels((1, 50))), AddressA, 3_000_000_000, events);
        input.Tick(3_400_000_000, events);
        Assert.Empty(events);
        input.Tick(3_500_000_000, events);
        Assert.Equal([50], events.Select(e => e.Raw));
        Assert.Equal([ConsoleA], input.Sources[1].Select(s => s.Cid));
        // A goes too: the channel holds (no event).
        events.Clear();
        input.Tick(6_000_000_000, events);
        Assert.Empty(events);
        Assert.False(input.Sources.ContainsKey(1));
    }

    [Fact]
    public void PreviewDataIsIgnoredUnlessAcceptedAndOtherStartCodesAreNotLevels()
    {
        var input = new SacnDmxInput();
        input.Want([new DmxChannel(1, 1, 0)]);
        var events = new List<InputEvent>();
        Assert.Equal(SacnInputResult.Preview, input.Receive(Data(ConsoleA, 1, 1, 100, Levels((1, 9)), preview: true), AddressA, 0, events));
        Assert.Equal(1, input.Previews);
        Assert.Empty(events);
        input.AcceptPreview = true;
        Assert.Equal(SacnInputResult.Dmx, input.Receive(Data(ConsoleA, 1, 2, 100, Levels((1, 9)), preview: true), AddressA, 1, events));
        Assert.Equal([9], events.Select(e => e.Raw));
        var alternate = Data(ConsoleA, 1, 3, 100, Levels((1, 77)));
        alternate[125] = 0xDD;
        Assert.Equal(SacnInputResult.Ignored, input.Receive(alternate, AddressA, 2, events));
        // A universe nobody reads is not tracked.
        Assert.Equal(SacnInputResult.Ignored, input.Receive(Data(ConsoleA, 2, 1, 100, Levels((1, 1))), AddressA, 3, events));
    }

    [Fact]
    public void SynchronizedDataWaitsForItsSyncPacketOnceOneHasBeenSeen()
    {
        var input = new SacnDmxInput();
        input.Want([new DmxChannel(1, 1, 0)]);
        var events = new List<InputEvent>();
        var sync = new byte[49];
        // No sync packet seen yet: synchronized data applies at once.
        Assert.Equal(SacnInputResult.Dmx, input.Receive(Data(ConsoleA, 1, 1, 100, Levels((1, 10)), sync: 7), AddressA, 0, events));
        Sacn.WriteSync(sync, new SacnSync(ConsoleA, 1, 7));
        Assert.Equal(SacnInputResult.Sync, input.Receive(sync, AddressA, 1_000, events));
        events.Clear();
        Assert.Equal(SacnInputResult.Held, input.Receive(Data(ConsoleA, 1, 2, 100, Levels((1, 20)), sync: 7), AddressA, 2_000, events));
        Assert.Empty(events);
        Sacn.WriteSync(sync, new SacnSync(ConsoleA, 2, 7));
        input.Receive(sync, AddressA, 3_000, events);
        Assert.Equal([20], events.Select(e => e.Raw));
        // Held again, and the syncs stop: 2.5 s after the last one the frame applies.
        events.Clear();
        input.Receive(Data(ConsoleA, 1, 3, 100, Levels((1, 30)), sync: 7), AddressA, 4_000, events);
        input.Receive(Data(ConsoleA, 1, 4, 100, Levels((1, 30)), sync: 7), AddressA, 2_000_000_000, events);
        input.Tick(2_400_000_000, events);
        Assert.Empty(events);
        input.Tick(2_500_003_000, events);
        Assert.Equal([30], events.Select(e => e.Raw));
    }

    [Fact]
    public void AFreshChannelLearnAndTheWinnersSurface()
    {
        var input = new SacnDmxInput { SurfaceOf = (address, cid) => cid == ConsoleB ? "Backup" : address == AddressA ? "Main" : "" };
        input.Want([new DmxChannel(1, 1, 0)]);
        var events = new List<InputEvent>();
        input.Receive(Data(ConsoleA, 1, 1, 100, Levels((1, 40), (5, 3))), AddressA, 0, events);
        Assert.Equal("SACN Main 1 1", events.Single().Address.Format());
        // A channel bound after the console's last move is sent with its next frame.
        input.Want([new DmxChannel(1, 1, 0), new DmxChannel(1, 5, 0)]);
        events.Clear();
        input.Receive(Data(ConsoleA, 1, 2, 100, Levels((1, 40), (5, 3))), AddressA, 1, events);
        Assert.Equal(["SACN Main 1 5"], events.Select(e => e.Address.Format()));
        events.Clear();
        input.Receive(Data(ConsoleB, 1, 1, 200, Levels((1, 40), (5, 3))), AddressB, 2, events);
        // The levels did not change, so nothing fires, though B now sets them.
        Assert.Empty(events);
        // Learning: every channel of any heard universe — each once at its first frame, then those that change.
        input.LearnAll = true;
        input.Receive(Data(ConsoleA, 9, 1, 100, Levels()), AddressA, 3, events);
        Assert.Equal(512, events.Count);
        events.Clear();
        input.Receive(Data(ConsoleA, 9, 2, 100, Levels((300, 8))), AddressA, 4, events);
        Assert.Equal(["SACN Main 9 300"], events.Select(e => e.Address.Format()));
    }
}
