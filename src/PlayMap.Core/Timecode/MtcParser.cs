namespace PlayMap.Core.Tc;

/// <summary>What an observation of timecode is: the end of an LTC frame, an MTC quarter frame, an Art-Net packet, or a locate (an MTC
/// full frame: the code is parked there).</summary>
public enum TcObsKind { FrameEnd, QuarterFrame, Packet, Locate }

/// <summary>One observation of a timecode source: at a monotonic instant the code was at <see cref="Frame"/> plus <see cref="Phase"/>
/// of a frame, in a format, running forward (+1), backward (−1) or parked (0).</summary>
public readonly record struct TcObservation(long MonoNs, long Frame, double Phase, TcFormat Format, int Direction, TcObsKind Kind, uint UserBits,
    bool ClockFlag)
{
    /// <summary>The position in TC ns.</summary>
    public long TcNs => Format.Ns(Frame, Phase);
}

/// <summary>MIDI Time Code in (MIDI 1.0 Detailed Specification, MIDI Time Code): quarter frames F1 0nnn dddd carry the time in eight
/// pieces over two frames (frames, seconds, minutes, hours, low then high nibble; piece 7 holds the rate code). A complete
/// sequence gives the code C, the time at piece 0; each piece k is then an observation at C + k/4 frames at its own arrival
/// (forward: pieces 0→7; backward: 7→0). A full frame message is a locate: the code is parked there. Pieces out of order restart
/// the sequence.</summary>
public sealed class MtcParser
{
    private readonly int[] _pieces = new int[8];
    private readonly long[] _arrived = new long[8];
    private int _expect = -1;
    private int _count;
    private int _direction;

    /// <summary>Complete sequences decoded.</summary>
    public long Sequences { get; private set; }

    /// <summary>Pieces that broke a sequence.</summary>
    public long OutOfOrder { get; private set; }

    /// <summary>A quarter frame; complete sequences add eight observations to <paramref name="into"/>.</summary>
    public void QuarterFrame(int piece, int value, long monoNs, List<TcObservation> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (piece is < 0 or > 7)
            return;
        if (_expect >= 0 && piece != _expect)
        {
            if (_count > 0)
                OutOfOrder++;
            _count = 0;
            _direction = 0;
        }
        if (_count == 0)
        {
            _direction = piece == 0 ? 1 : piece == 7 ? -1 : 0;
            if (_direction == 0)
            {
                _expect = -1;
                return;
            }
        }
        _pieces[piece] = value & 0x0F;
        _arrived[piece] = monoNs;
        _count++;
        _expect = piece + _direction;
        if (_count < 8)
            return;
        Sequences++;
        _count = 0;
        _expect = _direction > 0 ? 0 : 7;
        var frames = _pieces[0] | ((_pieces[1] & 0x1) << 4);
        var seconds = _pieces[2] | ((_pieces[3] & 0x3) << 4);
        var minutes = _pieces[4] | ((_pieces[5] & 0x3) << 4);
        var hours = _pieces[6] | ((_pieces[7] & 0x1) << 4);
        var format = TcFormat.OfRateCode((_pieces[7] >> 1) & 0x3);
        if (!format.TryFrame(hours, minutes, seconds, frames, out var c))
            return;
        // Forward, the sequence spans two frames from C; backward it ran from C + 2 down to C.
        for (var k = 0; k < 8; k++)
        {
            var at = _direction > 0 ? k / 4.0 : (8 - k) / 4.0;
            var whole = (long)Math.Floor(at);
            into.Add(new TcObservation(_arrived[k], c + whole, at - whole, format, _direction, TcObsKind.QuarterFrame, 0, false));
        }
        into.Sort((a, b) => a.MonoNs.CompareTo(b.MonoNs));
    }

    /// <summary>A full frame (F0 7F dev 01 01 hh mm ss ff F7): a locate, parked at that frame.</summary>
    public static TcObservation? FullFrame(int rateCode, int hours, int minutes, int seconds, int frames, long monoNs)
    {
        var format = TcFormat.OfRateCode(rateCode);
        return format.TryFrame(hours, minutes, seconds, frames, out var f)
            ? new TcObservation(monoNs, f, 0, format, 0, TcObsKind.Locate, 0, false)
            : null;
    }
}
