using System.Text;

namespace PlayMap.Core.Control.Midi;

/// <summary>Where a MIDI port's parsed messages go: control events, MIDI Time Code, real-time bytes and raw SysEx. Called on the
/// thread that feeds the parser; never block.</summary>
public interface IMidiSink
{
    void Input(in InputEvent e);

    /// <summary>An MTC quarter frame (F1 0nnn dddd): piece 0–7 and its 4-bit value.</summary>
    void QuarterFrame(int piece, int value, long monoNs);

    /// <summary>An MTC full frame (F0 7F dev 01 01 hh mm ss ff F7): the rate code (0 24, 1 25, 2 29.97 DF, 3 30) and the label.</summary>
    void FullFrame(int device, int rateCode, int hours, int minutes, int seconds, int frames, long monoNs);

    /// <summary>MTC user bits (F0 7F dev 01 02 u1…u9 F7): the eight 4-bit groups packed, and the binary-group flags.</summary>
    void UserBits(int device, uint bits, int flags, long monoNs);

    /// <summary>A real-time byte (F8 clock, FA start, FB continue, FC stop, FE active sensing, FF reset): never a control.</summary>
    void Realtime(byte status, long monoNs);

    /// <summary>A whole SysEx message (F0 … F7), for the monitor.</summary>
    void Sysex(ReadOnlySpan<byte> message, long monoNs);
}

/// <summary>One MIDI port's byte stream into events (MIDI 1.0 Detailed Specification 4.2): running status, real-time bytes between
/// any data bytes, Note On with velocity 0 as a release, 14-bit CC pairs (CC n and n+32, n 0–31) with a 5 ms wait for the LSB,
/// NRPN/RPN selection with data entry and increment/decrement, pitch bend, program, pressure, and SysEx assembled into a 4 KiB
/// buffer: MIDI Show Control (F0 7F dev 02 …), MIDI Machine Control (F0 7F dev 06 …) and MIDI Time Code full frames and user
/// bits (F0 7F dev 01 01|02 …) for any device id. Plain CCs are always emitted too, so a binding on CC 6 still works. State is
/// per port; not thread-safe (one feeder).</summary>
public sealed class MidiParser
{
    /// <summary>How long an MSB waits for its LSB before it is emitted alone.</summary>
    public const long PairWaitNs = 5_000_000;

    private const int MaxSysex = 4096;

    private readonly IMidiSink _sink;
    private readonly string _surface;
    private readonly string _origin;
    private readonly byte[] _sysex = new byte[MaxSysex];
    private readonly byte[] _data = new byte[2];
    private readonly ChannelState[] _channels = new ChannelState[16];
    private int _sysexLength = -1;
    private byte _status;
    private int _dataCount;

    /// <param name="surface">The surface this port is ("" = none), stamped on every event.</param>
    /// <param name="origin">The port's name, for the monitor.</param>
    public MidiParser(IMidiSink sink, string surface = "", string origin = "")
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _surface = surface ?? "";
        _origin = origin ?? "";
        for (var i = 0; i < 16; i++)
            _channels[i] = new ChannelState();
    }

    /// <summary>SysEx messages longer than the buffer, dropped whole.</summary>
    public long Overflows { get; private set; }

    /// <summary>Feeds bytes as they arrive (whole messages or any split of a stream).</summary>
    public void Feed(ReadOnlySpan<byte> bytes, long monoNs)
    {
        foreach (var b in bytes)
            Byte(b, monoNs);
    }

    /// <summary>Emits what waited past its deadline (an MSB with no LSB). Call often (the control thread's tick).</summary>
    public void Flush(long nowNs)
    {
        for (var ch = 0; ch < 16; ch++)
        {
            var s = _channels[ch];
            for (var cc = 0; cc < 32; cc++)
            {
                if (s.MsbAt[cc] >= 0 && nowNs - s.MsbAt[cc] >= PairWaitNs)
                {
                    s.MsbAt[cc] = -1;
                    Emit(MidiFamily.Cc14, ch, cc, InputKind.Axis, s.Msb[cc] << 7, 14, nowNs);
                }
            }
            if (s.DataAt >= 0 && nowNs - s.DataAt >= PairWaitNs)
            {
                s.DataAt = -1;
                EmitParameter(ch, s, s.DataMsb << 7, nowNs);
            }
        }
    }

    private void Byte(byte b, long monoNs)
    {
        if (b >= 0xF8)
        {
            _sink.Realtime(b, monoNs);
            return;
        }
        if (b == 0xF0)
        {
            _sysexLength = 0;
            _sysex[_sysexLength++] = b;
            _status = 0;
            return;
        }
        if (b == 0xF7)
        {
            if (_sysexLength > 0)
            {
                if (_sysexLength < MaxSysex)
                {
                    _sysex[_sysexLength++] = b;
                    Sysex(_sysex.AsSpan(0, _sysexLength), monoNs);
                }
                else
                {
                    Overflows++;
                }
            }
            _sysexLength = -1;
            return;
        }
        if (b >= 0x80)
        {
            // Any other status ends a SysEx that never closed.
            _sysexLength = -1;
            _status = b;
            _dataCount = 0;
            if (b == 0xF6)
                _status = 0;
            return;
        }
        if (_sysexLength >= 0)
        {
            if (_sysexLength < MaxSysex)
                _sysex[_sysexLength++] = b;
            else
                _sysexLength = MaxSysex;
            return;
        }
        if (_status == 0)
            return;
        _data[_dataCount++] = b;
        var needed = DataBytes(_status);
        if (_dataCount < needed)
            return;
        _dataCount = 0;
        Message(_status, _data[0], needed > 1 ? _data[1] : (byte)0, monoNs);
        // System common messages cancel running status.
        if (_status >= 0xF0)
            _status = 0;
    }

    private static int DataBytes(byte status) => status switch
    {
        >= 0x80 and < 0xC0 => 2,
        >= 0xC0 and < 0xE0 => 1,
        >= 0xE0 and < 0xF0 => 2,
        0xF1 or 0xF3 => 1,
        0xF2 => 2,
        _ => 0,
    };

    private void Message(byte status, byte d1, byte d2, long monoNs)
    {
        var ch = status & 0x0F;
        switch (status & 0xF0)
        {
            case 0x80:
                Emit(MidiFamily.Note, ch, d1, InputKind.Release, d2, 7, monoNs);
                return;
            case 0x90:
                Emit(MidiFamily.Note, ch, d1, d2 == 0 ? InputKind.Release : InputKind.Press, d2, 7, monoNs);
                return;
            case 0xA0:
                Emit(MidiFamily.PolyPressure, ch, d1, InputKind.Axis, d2, 7, monoNs);
                return;
            case 0xB0:
                ControlChange(ch, d1, d2, monoNs);
                return;
            case 0xC0:
                Emit(MidiFamily.Program, ch, d1, InputKind.Program, d1, 7, monoNs);
                return;
            case 0xD0:
                Emit(MidiFamily.Pressure, ch, 0, InputKind.Axis, d1, 7, monoNs);
                return;
            case 0xE0:
                Emit(MidiFamily.Bend, ch, 0, InputKind.Axis, d1 | (d2 << 7), 14, monoNs);
                return;
        }
        if (status == 0xF1)
            _sink.QuarterFrame((d1 >> 4) & 0x07, d1 & 0x0F, monoNs);
    }

    private void ControlChange(int ch, int cc, int value, long monoNs)
    {
        var s = _channels[ch];
        Emit(MidiFamily.Cc, ch, cc, InputKind.Axis, value, 7, monoNs);
        if (cc < 32)
        {
            // A new MSB clears the LSB (MIDI 1.0: the LSB is reset when the MSB changes).
            s.Msb[cc] = value;
            s.MsbAt[cc] = monoNs;
        }
        else if (cc < 64 && s.MsbAt[cc - 32] >= 0)
        {
            s.MsbAt[cc - 32] = -1;
            Emit(MidiFamily.Cc14, ch, cc - 32, InputKind.Axis, (s.Msb[cc - 32] << 7) | value, 14, monoNs);
        }
        switch (cc)
        {
            case 99:
                s.Nrpn = true;
                s.ParamMsb = value;
                s.DataAt = -1;
                return;
            case 98:
                s.Nrpn = true;
                s.ParamLsb = value;
                s.DataAt = -1;
                return;
            case 101:
                s.Nrpn = false;
                s.ParamMsb = value;
                s.DataAt = -1;
                return;
            case 100:
                s.Nrpn = false;
                s.ParamLsb = value;
                s.DataAt = -1;
                return;
            case 6 when s.Selected:
                s.DataMsb = value;
                s.DataAt = monoNs;
                return;
            case 38 when s.Selected:
                s.DataAt = -1;
                EmitParameter(ch, s, (s.DataMsb << 7) | value, monoNs);
                return;
            case 96 or 97 when s.Selected:
                var ticks = Math.Max(1, value) * (cc == 96 ? 1 : -1);
                Emit(s.Nrpn ? MidiFamily.Nrpn : MidiFamily.Rpn, ch, s.Parameter, InputKind.Relative, ticks, 7, monoNs, ticks);
                return;
        }
    }

    private void EmitParameter(int ch, ChannelState s, int raw, long monoNs) =>
        Emit(s.Nrpn ? MidiFamily.Nrpn : MidiFamily.Rpn, ch, s.Parameter, InputKind.Axis, raw, 14, monoNs);

    private void Emit(MidiFamily family, int ch, int number, InputKind kind, int raw, byte bits, long monoNs, double? value = null)
    {
        var v = value ?? (bits == 14 ? raw / 16383.0 : raw / 127.0);
        // Pitch bend's centre (8192) reads exactly 0.5.
        if (family == MidiFamily.Bend && Math.Abs(raw - 8192) <= 1)
            v = 0.5;
        var address = new InputAddress(InputSource.Midi, _surface, family, ch + 1, number, 0, 0, "");
        _sink.Input(new InputEvent(address, kind, v, raw, bits, monoNs) { Origin = _origin });
    }

    private void Sysex(ReadOnlySpan<byte> m, long monoNs)
    {
        _sink.Sysex(m, monoNs);
        // Universal real-time: F0 7F <device> <sub-id 1> <sub-id 2> … F7.
        if (m.Length < 6 || m[1] != 0x7F)
            return;
        var device = m[2];
        switch (m[3])
        {
            case 0x01 when m[4] == 0x01 && m.Length >= 10:
                // MTC full message: hr = 0rrhhhhh.
                _sink.FullFrame(device, (m[5] >> 5) & 0x03, m[5] & 0x1F, m[6], m[7], m[8], monoNs);
                return;
            case 0x01 when m[4] == 0x02 && m.Length >= 15:
            {
                uint bits = 0;
                for (var i = 0; i < 8; i++)
                    bits |= (uint)(m[5 + i] & 0x0F) << (4 * i);
                _sink.UserBits(device, bits, m[13] & 0x0F, monoNs);
                return;
            }
            case 0x02 when m.Length >= 7:
            {
                // MIDI Show Control: F0 7F <device> 02 <format> <command> <data> F7.
                var word = AsMsc(m[5]);
                if (word.Length > 0)
                    Trigger(MidiFamily.Msc, device, word, Data(m[6..^1]), monoNs);
                return;
            }
            case 0x06 when m.Length >= 6:
            {
                // MIDI Machine Control command: F0 7F <device> 06 <command> … F7.
                var word = AsMmc(m[4]);
                if (word.Length > 0)
                    Trigger(MidiFamily.Mmc, device, word, "", monoNs);
                return;
            }
        }
    }

    private static string AsMsc(byte command) => InputAddress.MscCommands.FirstOrDefault(kv => kv.Value == command).Key ?? "";

    private static string AsMmc(byte command) => InputAddress.MmcCommands.FirstOrDefault(kv => kv.Value == command).Key ?? "";

    /// <summary>MSC data: the cue number, list and path as ASCII, separated by 00.</summary>
    private static string Data(ReadOnlySpan<byte> data)
    {
        var b = new StringBuilder();
        foreach (var x in data)
            b.Append(x == 0 ? ' ' : (char)x);
        return b.ToString().Trim();
    }

    private void Trigger(MidiFamily family, int device, string command, string data, long monoNs)
    {
        var address = new InputAddress(InputSource.Midi, _surface, family, 0, device, 0, 0, command);
        _sink.Input(new InputEvent(address, InputKind.Trigger, 1, device, 7, monoNs) { Origin = data.Length > 0 ? $"{_origin} {data}".Trim() : _origin });
    }

    private sealed class ChannelState
    {
        public readonly int[] Msb = new int[32];
        public readonly long[] MsbAt = Enumerable.Repeat(-1L, 32).ToArray();
        public bool Nrpn;
        public int ParamMsb = 127;
        public int ParamLsb = 127;
        public int DataMsb;
        public long DataAt = -1;

        public int Parameter => (ParamMsb << 7) | ParamLsb;

        /// <summary>A parameter is selected (127/127 is the null RPN/NRPN).</summary>
        public bool Selected => !(ParamMsb == 127 && ParamLsb == 127);
    }
}
