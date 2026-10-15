using PlayMap.Core.Control;
using PlayMap.Core.Dmx;

namespace PlayMap.Core.Tests.Dmx;

/// <summary>Art-Net 4 input as a state machine (Art-Net 4 specification: ArtDmx's Sequence field — 0 disables reordering, otherwise a
/// late packet is discarded; ArtSync; ArtPoll answered by a unicast ArtPollReply after a random delay of up to 1 s; ArtTimeCode).</summary>
public sealed class ArtNetInTests
{
    private const uint Desk = 0x0A00_0014;

    private static byte[] Dmx(byte sequence, int universe, params byte[] channels)
    {
        var p = new byte[18 + 512];
        var n = ArtNet.WriteDmx(p, sequence, universe, channels);
        return p[..n];
    }

    [Fact]
    public void ALatePacketIsDroppedAndZeroDisablesTheCheck()
    {
        var input = new ArtNetDmxInput(1);
        input.Want([new DmxChannel(0, 1, 0)]);
        var events = new List<InputEvent>();
        Assert.Equal(ArtNetInputResult.Dmx, input.Receive(Dmx(250, 0, 100), Desk, 1, events, out _));
        Assert.Equal(ArtNetInputResult.Dmx, input.Receive(Dmx(251, 0, 110), Desk, 2, events, out _));
        // 249 is behind 251: (249 − 251) mod 256 = 254, late.
        Assert.Equal(ArtNetInputResult.OutOfOrder, input.Receive(Dmx(249, 0, 50), Desk, 3, events, out _));
        // Forward across the wrap is in order (255 → 1 is +2), and 0 disables the check.
        Assert.Equal(ArtNetInputResult.Dmx, input.Receive(Dmx(255, 0, 120), Desk, 4, events, out _));
        Assert.Equal(ArtNetInputResult.Dmx, input.Receive(Dmx(1, 0, 121), Desk, 5, events, out _));
        Assert.Equal(ArtNetInputResult.Dmx, input.Receive(Dmx(0, 0, 122), Desk, 6, events, out _));
        Assert.Equal(1, input.Dropped);
        Assert.Equal([100, 110, 120, 121, 122], events.Select(e => e.Raw));
        Assert.Equal("ARTNET 0 1", events[0].Address.Format());
    }

    [Fact]
    public void OnlyChangedBoundChannelsBecomeEventsAndAFinePartnerMakesSixteenBits()
    {
        var input = new ArtNetDmxInput(1);
        input.Want([new DmxChannel(3, 101, 102)]);
        var events = new List<InputEvent>();
        var channels = new byte[110];
        channels[100] = 0x80;
        channels[101] = 0x01;
        input.Receive(Dmx(0, 3, channels), Desk, 1, events, out _);
        input.Receive(Dmx(0, 3, channels), Desk, 2, events, out _);
        var e = Assert.Single(events);
        Assert.Equal((0x8001, (byte)16, "ARTNET 3 101+102"), (e.Raw, e.Bits, e.Address.Format()));
        Assert.Equal(0x8001 / 65535.0, e.Value, 9);
    }

    [Fact]
    public void AChannelBoundAfterTheConsolesLastMoveTakesItsLevelAtTheNextRefresh()
    {
        var input = new ArtNetDmxInput(1);
        input.Want([new DmxChannel(0, 1, 0)]);
        var events = new List<InputEvent>();
        input.Receive(Dmx(0, 0, 10, 20), Desk, 1, events, out _);
        Assert.Equal([1], events.Select(e => e.Address.Number));
        events.Clear();
        input.Want([new DmxChannel(0, 1, 0), new DmxChannel(0, 2, 0)]);
        input.Receive(Dmx(0, 0, 10, 20), Desk, 2, events, out _);
        var fresh = Assert.Single(events);
        Assert.Equal((2, 20), (fresh.Address.Number, fresh.Raw));
        events.Clear();
        input.Receive(Dmx(0, 0, 10, 20), Desk, 3, events, out _);
        Assert.Empty(events);
    }

    [Fact]
    public void AfterAnArtSyncFramesWaitForTheNextAndFourSecondsWithoutOneReleaseThem()
    {
        var input = new ArtNetDmxInput(1);
        input.Want([new DmxChannel(0, 1, 0)]);
        var events = new List<InputEvent>();
        var sync = new byte[14];
        ArtNet.WriteSync(sync);
        Assert.Equal(ArtNetInputResult.Sync, input.Receive(sync, Desk, 1_000_000_000, events, out _));
        Assert.Equal(ArtNetInputResult.Held, input.Receive(Dmx(0, 0, 200), Desk, 1_010_000_000, events, out _));
        Assert.Empty(events);
        input.Receive(sync, Desk, 1_020_000_000, events, out _);
        Assert.Equal(200, Assert.Single(events).Raw);
        events.Clear();
        input.Receive(Dmx(0, 0, 10), Desk, 1_030_000_000, events, out _);
        input.Tick(4_000_000_000, events);
        Assert.Empty(events);
        input.Tick(5_020_000_000, events);
        Assert.Equal(10, Assert.Single(events).Raw);
        Assert.False(input.Synced(5_020_000_000));
    }

    [Fact]
    public void APollIsAnsweredUnicastWithinOneSecond()
    {
        var input = new ArtNetDmxInput(42);
        var poll = new byte[14];
        ArtNet.WritePoll(poll);
        var events = new List<InputEvent>();
        var dues = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            var at = i * 2_000_000_000L;
            Assert.Equal(ArtNetInputResult.Poll, input.Receive(poll, Desk + (uint)i, at, events, out _));
            var due = input.TakeDuePolls(at + ArtNetDmxInput.MaxPollDelayNs);
            var d = Assert.Single(due);
            Assert.Equal(Desk + (uint)i, d.To);
            Assert.InRange(d.DueNs - at, 0, ArtNetDmxInput.MaxPollDelayNs);
            dues.Add(d.DueNs - at);
        }
        // The delays are spread (a seeded random), not all at once.
        Assert.True(dues.Distinct().Count() > 40);
        Assert.Empty(input.TakeDuePolls(long.MaxValue));
    }

    [Fact]
    public void APollReplyNamesTheNodeAndItsUniverses()
    {
        var reply = new byte[ArtNet.PollReplyLength];
        var n = ArtNet.WritePollReply(reply, new ArtPollReplyInfo([127, 4, 0, 1], "PlayMap", "Stage left (PlayMap media server)", "#0001 [0000] control input", [0x0103], 0x00FF));
        Assert.Equal(239, n);
        Assert.Equal(ArtOp.PollReply, ArtNet.OpOf(reply));
        Assert.Equal("PlayMap", ArtNet.ShortNameOf(reply));
        Assert.Equal((0x01, 0x00, 1, 0x03, 0x08), (reply[18], reply[19], reply[173], reply[190], reply[212]));
        // A reply speaks for one Net and Sub-Net: a universe of another (0x0215: net 2, sub-net 1) is not passed off as one of these.
        ArtNet.WritePollReply(reply, new ArtPollReplyInfo([127, 4, 0, 1], "PlayMap", "", "", [0x0103, 0x0215, 0x0109], 0x00FF));
        Assert.Equal((0x01, 0x00, 2, 0x03, 0x09, 0), (reply[18], reply[19], reply[173], reply[190], reply[191], reply[192]));
    }

    [Fact]
    public void ArtTimeCodeIsReadAndAMalformedOneCounted()
    {
        var input = new ArtNetDmxInput(1);
        var packet = new byte[ArtNet.TimeCodeLength];
        ArtNet.WriteTimeCode(packet, new ArtTimeCode(4, 3, 2, 1, 1, 0));
        var events = new List<InputEvent>();
        Assert.Equal(ArtNetInputResult.TimeCode, input.Receive(packet, Desk, 1, events, out var tc));
        Assert.Equal(new ArtTimeCode(4, 3, 2, 1, 1, 0), tc);
        packet[18] = 7;
        Assert.Equal(ArtNetInputResult.Malformed, input.Receive(packet, Desk, 2, events, out _));
        Assert.Equal(1, input.Malformed);
        Assert.Equal(ArtNetInputResult.Ignored, input.Receive("hello world!"u8, Desk, 3, events, out _));
    }
}
