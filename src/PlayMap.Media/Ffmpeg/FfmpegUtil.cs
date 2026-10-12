using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace PlayMap.Media.Ffmpeg;

/// <summary>An FFmpeg call failed: what was attempted and FFmpeg's own words for the error.</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException(string message) : base(message)
    {
    }

    public FfmpegException(string message, Exception inner) : base(message, inner)
    {
    }

    public FfmpegException()
    {
    }

    public FfmpegException(string what, int error) : base($"{what}: {FfmpegUtil.ErrorText(error)}")
    {
        Error = error;
    }

    /// <summary>FFmpeg's negative error code (0 when the failure was not an FFmpeg return).</summary>
    public int Error { get; }
}

/// <summary>Small exact helpers over FFmpeg's numbers: error text, rational time bases to nanoseconds and back,
/// C strings.</summary>
public static unsafe class FfmpegUtil
{
    public const long NsPerSecond = 1_000_000_000;

    public static readonly int ErrorAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    /// <summary>FFmpeg's description of an error code.</summary>
    public static string ErrorText(int error)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        if (ffmpeg.av_strerror(error, buffer, size) < 0)
            return "error " + error.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Marshal.PtrToStringUTF8((nint)buffer) ?? "";
    }

    /// <summary>Throws <see cref="FfmpegException"/> for a negative return.</summary>
    public static int Check(int result, string what)
    {
        if (result < 0)
            throw new FfmpegException(what, result);
        return result;
    }

    /// <summary>A timestamp in a time base as nanoseconds, rounded up: the first nanosecond at or after the
    /// instant, the same convention as <see cref="PlayMap.Core.Time.FrameRate.TimeOf"/>, so a frame starting at
    /// an exact rational instant is selected by every nanosecond of its interval and by no nanosecond
    /// before it.</summary>
    public static long ToNs(long ts, AVRational tb)
    {
        if (tb.den == 0)
            return 0;
        var n = (Int128)ts * tb.num * NsPerSecond;
        var q = n / tb.den;
        if (n % tb.den != 0 && n > 0)
            q += 1;
        return (long)q;
    }

    /// <summary>Nanoseconds in a time base, rounded down (the timestamp at or before the instant).</summary>
    public static long FromNs(long ns, AVRational tb)
    {
        if (tb.num == 0)
            return 0;
        var n = (Int128)ns * tb.den;
        var d = (Int128)tb.num * NsPerSecond;
        var q = n / d;
        if (n % d != 0 && n < 0)
            q -= 1;
        return (long)q;
    }

    /// <summary>A duration in a time base as nanoseconds (rounded to nearest).</summary>
    public static long DurationNs(long ticks, AVRational tb) =>
        tb.den == 0 ? 0 : (long)(((Int128)ticks * tb.num * NsPerSecond + tb.den / 2) / tb.den);

    public static string Text(byte* s) => s is null ? "" : Marshal.PtrToStringUTF8((nint)s) ?? "";

    /// <summary>"30000/1001", "25/1"; empty when unknown.</summary>
    public static string RationalText(AVRational r) =>
        r.num > 0 && r.den > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.num / Gcd(r.num, r.den)}/{r.den / Gcd(r.num, r.den)}")
            : "";

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return Math.Abs(a);
    }
}
