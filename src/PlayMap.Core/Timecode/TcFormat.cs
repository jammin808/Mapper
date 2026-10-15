using System.Globalization;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tc;

/// <summary>A timecode format: the real frame rate (24000/1001, 25, 30000/1001 …) and whether its labels drop frames. A position is
/// a frame index at the real rate, so in a 29.97 DF show 01:00:00;00 is frame 107 892 = 3599.9964 s: content time is real elapsed
/// time and labels are only labels. Above 30 fps, LTC and MTC carry frame pairs.</summary>
public readonly record struct TcFormat(FrameRate Rate, bool DropFrame)
{
    public static readonly TcFormat Fps24 = new(FrameRate.Fps24, false);
    public static readonly TcFormat Fps25 = new(FrameRate.Fps25, false);
    public static readonly TcFormat Fps2997Df = new(FrameRate.Fps2997, true);
    public static readonly TcFormat Fps2997 = new(FrameRate.Fps2997, false);
    public static readonly TcFormat Fps30 = new(FrameRate.Fps30, false);

    /// <summary>The integer rate labels count at: 24, 25, 30, 50, 60.</summary>
    public int LabelFps => Rate.Nominal;

    /// <summary>LTC and MTC carry frame pairs above 30 fps.</summary>
    public bool Pairs => LabelFps > 30;

    public long FrameNs => Rate.FrameDurationNs;

    /// <summary>"25", "29.97df", "29.97", "23.976", "59.94df"…</summary>
    public string Text => Rate.ToString() + (DropFrame ? "df" : "");

    public override string ToString() => Text;

    public static bool TryParse(string? text, out TcFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim().ToLowerInvariant();
        var df = t.EndsWith("df", StringComparison.Ordinal);
        if (df)
            t = t[..^2];
        else if (t.EndsWith("ndf", StringComparison.Ordinal))
            t = t[..^3];
        if (!FrameRate.TryParse(t, out var rate) || rate.Nominal is not (24 or 25 or 30 or 50 or 60))
            return false;
        if (df && !rate.IsNtsc)
            return false;
        format = new TcFormat(rate, df);
        return true;
    }

    /// <summary>The frame of a label; false for a label that does not exist (a drop-frame minute's first two numbers, a field out of
    /// range).</summary>
    public bool TryFrame(int h, int m, int s, int f, out long frame)
    {
        frame = 0;
        var fps = LabelFps;
        if (h is < 0 or > 23 || m is < 0 or > 59 || s is < 0 or > 59 || f < 0 || f >= fps)
            return false;
        var drop = DropFrame ? fps / 15 : 0;
        if (drop > 0 && s == 0 && f < drop && m % 10 != 0)
            return false;
        var totalMinutes = h * 60L + m;
        frame = ((h * 3600L + m * 60L + s) * fps) + f - drop * (totalMinutes - totalMinutes / 10);
        return true;
    }

    /// <summary>The label of a frame (wrapping at 24 h).</summary>
    public (int H, int M, int S, int F) Label(long frame)
    {
        var fps = LabelFps;
        var day = (long)fps * 86400;
        if (DropFrame)
        {
            var drop = fps / 15;
            var perTen = fps * 600 - drop * 9;
            day = perTen * 144L;
            frame = ((frame % day) + day) % day;
            var tens = frame / perTen;
            var rem = frame % perTen;
            var perMinute = fps * 60 - drop;
            frame += drop * 9 * tens + (rem > drop ? drop * ((rem - drop) / perMinute) : 0);
            day = (long)fps * 86400;
        }
        frame = ((frame % day) + day) % day;
        var f = (int)(frame % fps);
        var total = frame / fps;
        return ((int)(total / 3600 % 24), (int)(total / 60 % 60), (int)(total % 60), f);
    }

    /// <summary>"HH:MM:SS:FF", with ';' before the frames when drop frame.</summary>
    public string Format(long frame)
    {
        var (h, m, s, f) = Label(frame);
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}{(DropFrame ? ';' : ':')}{f:00}");
    }

    /// <summary>Reads "HH:MM:SS:FF" (or ';' for drop frame) in this format; false for a label that does not exist.</summary>
    public bool TryParseLabel(string? text, out long frame)
    {
        frame = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split(':', ';', '.');
        if (parts.Length != 4)
            return false;
        Span<int> v = stackalloc int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out v[i]))
                return false;
        return TryFrame(v[0], v[1], v[2], v[3], out frame);
    }

    /// <summary>A position in TC ns: the frame's start plus a phase of a frame.</summary>
    public long Ns(long frame, double phase = 0) => Rate.TimeOf(frame) + (long)Math.Round(phase * FrameNs);

    /// <summary>The frame a TC ns lies in.</summary>
    public long FrameOf(long tcNs) => Rate.FrameAt(tcNs);

    /// <summary>The format of a label rate and its drop flag as LTC, MTC or Art-Net carry it (the real rate is NTSC's for drop frame,
    /// and for a non-drop 30 when measured so; a configured format overrides).</summary>
    public static TcFormat OfLabel(int labelFps, bool dropFrame) => labelFps switch
    {
        24 => Fps24,
        25 => Fps25,
        30 when dropFrame => Fps2997Df,
        30 => Fps30,
        50 => new TcFormat(FrameRate.Fps50, false),
        60 when dropFrame => new TcFormat(FrameRate.Fps5994, true),
        _ => new TcFormat(FrameRate.Fps60, false),
    };

    /// <summary>The format of an ArtTimeCode or MTC rate code: 0 film 24, 1 EBU 25, 2 29.97 drop, 3 SMPTE 30.</summary>
    public static TcFormat OfRateCode(int code) => code switch { 0 => Fps24, 1 => Fps25, 2 => Fps2997Df, _ => Fps30 };
}
