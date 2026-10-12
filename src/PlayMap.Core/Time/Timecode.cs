using System.Globalization;

namespace PlayMap.Core.Time;

/// <summary>A frame rate as a rational, so 29.97 is 30000/1001 exactly and frame maths never drifts.</summary>
public readonly record struct FrameRate(int Numerator, int Denominator)
{
    public static readonly FrameRate Fps24 = new(24, 1);
    public static readonly FrameRate Fps25 = new(25, 1);
    public static readonly FrameRate Fps2997 = new(30000, 1001);
    public static readonly FrameRate Fps30 = new(30, 1);
    public static readonly FrameRate Fps50 = new(50, 1);
    public static readonly FrameRate Fps5994 = new(60000, 1001);
    public static readonly FrameRate Fps60 = new(60, 1);

    public double Value => Denominator == 0 ? 0 : Numerator / (double)Denominator;

    public bool IsValid => Numerator > 0 && Denominator > 0;

    /// <summary>The nominal integer rate a timecode counts at (30 for 29.97).</summary>
    public int Nominal => (int)Math.Round(Value);

    /// <summary>Drop-frame applies to the NTSC rates 29.97 and 59.94.</summary>
    public bool IsNtsc => Denominator == 1001 && (Numerator == 30000 || Numerator == 60000);

    public long FrameDurationNs => IsValid ? (long)Math.Round(1e9 * Denominator / Numerator) : 0;

    /// <summary>The frame index whose interval contains the time (floor).</summary>
    public long FrameAt(long ns)
    {
        if (!IsValid)
            return 0;
        // Exact rational arithmetic: floor(ns · num / (den · 1e9)).
        var n = (Int128)ns * Numerator;
        var d = (Int128)Denominator * 1_000_000_000;
        var q = n / d;
        if (n % d != 0 && n < 0)
            q -= 1;
        return (long)q;
    }

    /// <summary>The start time of a frame index.</summary>
    public long TimeOf(long frame)
    {
        if (!IsValid)
            return 0;
        // The first nanosecond of the frame (ceiling), so FrameAt(TimeOf(n)) == n exactly.
        var n = (Int128)frame * 1_000_000_000 * Denominator;
        var q = n / Numerator;
        if (n % Numerator != 0 && n > 0)
            q += 1;
        return (long)q;
    }

    /// <summary>Parses "25", "29.97", "59.94", "30000/1001".</summary>
    public static bool TryParse(string? text, out FrameRate rate)
    {
        rate = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        var slash = t.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            if (int.TryParse(t.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                && int.TryParse(t.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0)
            {
                rate = new FrameRate(n, d);
                return true;
            }
            return false;
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0 || v > 1000)
            return false;
        if (Math.Abs(v - 23.976) < 0.01) rate = new FrameRate(24000, 1001);
        else if (Math.Abs(v - 29.97) < 0.01) rate = Fps2997;
        else if (Math.Abs(v - 59.94) < 0.01) rate = Fps5994;
        else if (Math.Abs(v - 119.88) < 0.01) rate = new FrameRate(120000, 1001);
        else if (Math.Abs(v - Math.Round(v)) < 1e-9) rate = new FrameRate((int)Math.Round(v), 1);
        else rate = new FrameRate((int)Math.Round(v * 1000), 1000);
        return true;
    }

    public override string ToString() => Denominator == 1
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : Value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>SMPTE timecode HH:MM:SS:FF (or HH:MM:SS;FF drop-frame) from and to a frame count.</summary>
public static class Timecode
{
    public static string Format(long frame, FrameRate rate, bool dropFrame = false)
    {
        if (frame < 0)
            frame = 0;
        var fps = rate.Nominal;
        if (fps <= 0)
            return "00:00:00:00";
        if (dropFrame && rate.IsNtsc)
        {
            // Drop two (four at 59.94) frame numbers every minute except every tenth minute.
            var drop = fps / 15;
            var perTenMinutes = fps * 600 - drop * 9;
            var perMinute = fps * 60 - drop;
            var tens = frame / perTenMinutes;
            var rem = frame % perTenMinutes;
            frame += drop * 9 * tens + (rem > drop ? drop * ((rem - drop) / perMinute) : 0);
        }
        var ff = frame % fps;
        var totalSeconds = frame / fps;
        var ss = totalSeconds % 60;
        var mm = totalSeconds / 60 % 60;
        var hh = totalSeconds / 3600 % 24;
        var sep = dropFrame && rate.IsNtsc ? ';' : ':';
        return string.Create(CultureInfo.InvariantCulture, $"{hh:00}:{mm:00}:{ss:00}{sep}{ff:00}");
    }

    public static bool TryParse(string? text, FrameRate rate, out long frame)
    {
        frame = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var dropFrame = text.Contains(';', StringComparison.Ordinal);
        var parts = text.Trim().Split(':', ';');
        if (parts.Length != 4)
            return false;
        Span<int> v = stackalloc int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out v[i]))
                return false;
        var fps = rate.Nominal;
        if (fps <= 0 || v[1] > 59 || v[2] > 59 || v[3] >= fps)
            return false;
        var totalMinutes = v[0] * 60L + v[1];
        frame = ((v[0] * 3600L + v[1] * 60L + v[2]) * fps) + v[3];
        if (dropFrame && rate.IsNtsc)
        {
            var drop = fps / 15;
            frame -= drop * (totalMinutes - totalMinutes / 10);
        }
        return true;
    }
}
