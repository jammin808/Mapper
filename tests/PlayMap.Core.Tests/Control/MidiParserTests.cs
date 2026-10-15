using PlayMap.Core.Control;
using PlayMap.Core.Control.Midi;
using PlayMap.Core.Tc;

namespace PlayMap.Core.Tests.Control;

/// <summary>A MIDI port's bytes into events, from the MIDI 1.0 Detailed Specification (4.2: running status, real-time bytes, Note On
/// velocity 0; the control change table for 14-bit pairs (0–31 with 32–63) and the registered/non-registered parameter numbers 98–101
/// with data entry 6/38 and increment/decrement 96/97) and from MIDI Show Control 1.1's and MIDI Machine Control's message formats.</summary>
public sealed class MidiParserTests
{
    internal sealed class Sink : IMidiSink
    {
        public List<InputEvent> Events { get; } = [];
        public List<(int Piece, int Value)> Quarters { get; } = [];
        public List<(int Device, int Rate, int H, int M, int S, int F)> Full { get; } = [];
        public List<(int Device, uint Bits)> User { get; } = [];
        public List<byte> RealtimeBytes { get; } = [];
        public int Sysexes { get; private set; }

        public void Input(in InputEvent e) => Events.Add(e);
        public void QuarterFrame(int piece, int value, long monoNs) => Quarters.Add((piece, value));
        public void FullFrame(int device, int rateCode, int hours, int minutes, int seconds, int frames, long monoNs) => Full.Add((device, rateCode, hours, minutes, seconds, frames));
        public void UserBits(int device, uint bits, int flags, long monoNs) => User.Add((device, bits));
        public void Realtime(byte status, long monoNs) => RealtimeBytes.Add(status);
        public void Sysex(ReadOnlySpan<byte> message, long monoNs) => Sysexes++;
    }

    private static (MidiParser Parser, Sink Sink) Make(string surface = "")
    {
        var sink = new Sink();
        return (new MidiParser(sink, surface, "test port"), sink);
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    [Fact]
    public void RunningStatusCarriesTheLastStatusAndVelocityZeroIsARelease()
    {
        var (p, s) = Make("APC40");
        p.Feed(Hex("90 3C 64 3E 64 3C 00 80 3E 40"), 1);
        Assert.Equal([InputKind.Press, InputKind.Press, InputKind.Release, InputKind.Release], s.Events.Select(e => e.Kind));
        Assert.Equal([60, 62, 60, 62], s.Events.Select(e => e.Address.Number));
        Assert.All(s.Events, e => Assert.Equal("APC40", e.Address.Surface));
        Assert.Equal(100 / 127.0, s.Events[0].Value, 9);
        Assert.Equal("MIDI APC40 NOTE 1 60", s.Events[0].Address.Format());
    }

    [Fact]
    public void RealTimeBytesBetweenDataBytesGoToTheClockAndNeverBreakAMessage()
    {
        var (p, s) = Make();
        p.Feed(Hex("B0 F8 07 FA 64"), 1);
        Assert.Equal([0xF8, 0xFA], s.RealtimeBytes);
        var cc = Assert.Single(s.Events);
        Assert.Equal((MidiFamily.Cc, 7, 100), (cc.Address.Family, cc.Address.Number, cc.Raw));
    }

    [Fact]
    public void A14BitPairEmitsOnItsLsbAndALoneMsbAfterFiveMilliseconds()
    {
        var (p, s) = Make();
        p.Feed(Hex("B0 01 40 21 10"), 1_000_000);
        var cc14 = Assert.Single(s.Events, e => e.Address.Family == MidiFamily.Cc14);
        Assert.Equal((1, (0x40 << 7) | 0x10, (byte)14), (cc14.Address.Number, cc14.Raw, cc14.Bits));
        Assert.Equal(((0x40 << 7) | 0x10) / 16383.0, cc14.Value, 9);
        // The plain CCs pass too, so a binding on CC 1 still works.
        Assert.Equal(2, s.Events.Count(e => e.Address.Family == MidiFamily.Cc));

        s.Events.Clear();
        p.Feed(Hex("B0 02 20"), 10_000_000);
        p.Flush(14_000_000);
        Assert.DoesNotContain(s.Events, e => e.Address.Family == MidiFamily.Cc14);
        p.Flush(15_000_000);
        var alone = Assert.Single(s.Events, e => e.Address.Family == MidiFamily.Cc14);
        Assert.Equal((2, 0x20 << 7), (alone.Address.Number, alone.Raw));
    }

    [Fact]
    public void AnNrpnIsSelectedThenEnteredAndAnRpnIncrementIsATick()
    {
        var (p, s) = Make();
        p.Feed(Hex("B3 63 01 62 02 06 10 26 05"), 1);
        var nrpn = Assert.Single(s.Events, e => e.Address.Family == MidiFamily.Nrpn);
        Assert.Equal((4, 130, (16 << 7) | 5), (nrpn.Address.Channel, nrpn.Address.Number, nrpn.Raw));
        Assert.Equal("MIDI NRPN 4 130", nrpn.Address.Format());

        s.Events.Clear();
        p.Feed(Hex("B3 65 00 64 00 60 01 61 02"), 2);
        var ticks = s.Events.Where(e => e.Address.Family == MidiFamily.Rpn).ToList();
        Assert.Equal([InputKind.Relative, InputKind.Relative], ticks.Select(e => e.Kind));
        Assert.Equal([1.0, -2.0], ticks.Select(e => e.Value));

        // The null parameter (127/127) deselects: data entry is a plain CC again.
        s.Events.Clear();
        p.Feed(Hex("B3 65 7F 64 7F 06 33"), 3);
        Assert.DoesNotContain(s.Events, e => e.Address.Family is MidiFamily.Nrpn or MidiFamily.Rpn);
    }

    [Fact]
    public void BendIsFourteenBitsWithAnExactCentreAndProgramAndPressureRead()
    {
        var (p, s) = Make();
        p.Feed(Hex("E0 00 40 E0 7F 7F C5 05 D2 40 A1 3C 22"), 1);
        Assert.Equal(0.5, s.Events[0].Value);
        Assert.Equal(1.0, s.Events[1].Value, 9);
        Assert.Equal((MidiFamily.Program, InputKind.Program, 6, 5), (s.Events[2].Address.Family, s.Events[2].Kind, s.Events[2].Address.Channel, s.Events[2].Address.Number));
        Assert.Equal((MidiFamily.Pressure, 3), (s.Events[3].Address.Family, s.Events[3].Address.Channel));
        Assert.Equal((MidiFamily.PolyPressure, 60, 0x22), (s.Events[4].Address.Family, s.Events[4].Address.Number, s.Events[4].Raw));
    }

    /// <summary>MIDI Show Control 1.1 (the GO example): F0 7F &lt;device&gt; 02 &lt;command format&gt; &lt;command&gt; &lt;Q number&gt;
    /// 00 &lt;Q list&gt; F7 — format 01 is lighting, command 01 GO, cue 235.6 in list 36.6; and MIDI Machine Control: F0 7F &lt;device&gt;
    /// 06 &lt;command&gt; F7 — 02 PLAY to every device (7F).</summary>
    [Fact]
    public void ShowControlAndMachineControlBecomeTriggersSplitAcrossReads()
    {
        var (p, s) = Make();
        var go = Hex("F0 7F 01 02 01 01 32 33 35 2E 36 00 33 36 2E 36 F7");
        p.Feed(go.AsSpan(0, 7), 1);
        Assert.Empty(s.Events);
        p.Feed(go.AsSpan(7), 2);
        var msc = Assert.Single(s.Events);
        Assert.Equal((MidiFamily.Msc, 1, "GO", InputKind.Trigger), (msc.Address.Family, msc.Address.Number, msc.Address.Path, msc.Kind));
        Assert.Equal("MIDI MSC 1 GO", msc.Address.Format());
        Assert.Contains("235.6 36.6", msc.Origin, StringComparison.Ordinal);

        s.Events.Clear();
        p.Feed(Hex("F0 7F 7F 06 02 F7 F0 7F 7F 06 01 F7"), 3);
        Assert.Equal(["PLAY", "STOP"], s.Events.Select(e => e.Address.Path));
        Assert.All(s.Events, e => Assert.Equal(127, e.Address.Number));
        Assert.Equal(3, s.Sysexes);
    }

    [Fact]
    public void AnUnterminatedSysexIsDroppedByTheNextStatus()
    {
        var (p, s) = Make();
        p.Feed(Hex("F0 7F 01 02 01 01 90 3C 64"), 1);
        Assert.Equal(0, s.Sysexes);
        var note = Assert.Single(s.Events);
        Assert.Equal(InputKind.Press, note.Kind);
    }
}

/// <summary>MIDI Time Code in (MIDI 1.0 Detailed Specification, MIDI Time Code: quarter frames F1 0nnn dddd; full message
/// F0 7F &lt;device&gt; 01 01 hr mn sc fr F7 with hr = 0rrhhhhh; user bits F0 7F &lt;device&gt; 01 02 u1…u9 F7), for any device id.</summary>
public sealed class MtcTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x10)]
    [InlineData(0x7F)]
    public void AFullFrameReadsForAnyDeviceIdAsALocate(int device)
    {
        var sink = new MidiParserTests.Sink();
        var p = new MidiParser(sink);
        // 01:02:03:04 at 25 fps: rate code 01 → hr = 0 01 00001.
        p.Feed([0xF0, 0x7F, (byte)device, 0x01, 0x01, 0x21, 0x02, 0x03, 0x04, 0xF7], 5);
        var full = Assert.Single(sink.Full);
        Assert.Equal((device, 1, 1, 2, 3, 4), full);
        var obs = MtcParser.FullFrame(full.Rate, full.H, full.M, full.S, full.F, 5);
        Assert.NotNull(obs);
        Assert.Equal((TcObsKind.Locate, 0, "01:02:03:04"), (obs!.Value.Kind, obs.Value.Direction, obs.Value.Format.Format(obs.Value.Frame)));
        Assert.Equal(TcFormat.Fps25, obs.Value.Format);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x10)]
    [InlineData(0x7F)]
    public void UserBitsReadForAnyDeviceId(int device)
    {
        var sink = new MidiParserTests.Sink();
        var p = new MidiParser(sink);
        p.Feed([0xF0, 0x7F, (byte)device, 0x01, 0x02, 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8, 0x0, 0xF7], 1);
        Assert.Equal((device, 0x87654321u), Assert.Single(sink.User));
    }

    [Fact]
    public void EightQuarterFramesForwardGiveTheCodeAtPieceZeroAndAnObservationPerPiece()
    {
        var sink = new MidiParserTests.Sink();
        var p = new MidiParser(sink);
        // 01:02:03:04 at 25 fps, pieces 0–7 a quarter frame (10 ms) apart.
        var pieces = Hex("F1 04 F1 10 F1 23 F1 30 F1 42 F1 50 F1 61 F1 72");
        for (var i = 0; i < 8; i++)
            p.Feed(pieces.AsSpan(i * 2, 2), 1_000_000_000 + i * 10_000_000L);
        Assert.Equal(8, sink.Quarters.Count);
        var mtc = new MtcParser();
        var obs = new List<TcObservation>();
        for (var i = 0; i < 8; i++)
            mtc.QuarterFrame(sink.Quarters[i].Piece, sink.Quarters[i].Value, 1_000_000_000 + i * 10_000_000L, obs);
        Assert.Equal(8, obs.Count);
        Assert.Equal(1, mtc.Sequences);
        Assert.All(obs, o => Assert.Equal((1, TcObsKind.QuarterFrame), (o.Direction, o.Kind)));
        Assert.Equal("01:02:03:04", obs[0].Format.Format(obs[0].Frame));
        // Piece k is the code plus k quarter frames.
        Assert.Equal(obs[0].TcNs + 3 * TcFormat.Fps25.FrameNs / 4, obs[3].TcNs);
        Assert.Equal(obs[0].TcNs + TcFormat.Fps25.FrameNs + 3 * TcFormat.Fps25.FrameNs / 4, obs[7].TcNs);
    }

    [Fact]
    public void PiecesOutOfOrderRestartTheSequence()
    {
        var mtc = new MtcParser();
        var obs = new List<TcObservation>();
        mtc.QuarterFrame(0, 4, 1, obs);
        mtc.QuarterFrame(1, 0, 2, obs);
        mtc.QuarterFrame(3, 0, 3, obs);
        Assert.Empty(obs);
        Assert.Equal(1, mtc.OutOfOrder);
    }
}
