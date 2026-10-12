using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;

namespace PlayMap.Media.Ffmpeg;

/// <summary>One line FFmpeg logged, with its level (FFmpeg's AV_LOG_* numbers: 16 error, 24 warning, 32 info).</summary>
public readonly record struct FfmpegLogLine(int Level, string Text);

/// <summary>FFmpeg's log, routed away from stderr: lines at or above <see cref="Level"/> go to
/// <see cref="Message"/> (the engine's journal subscribes) and a ring of the last lines; a thread may capture
/// everything FFmpeg says on it for a moment (device format listings arrive this way). The callback runs on
/// FFmpeg's threads, formats the line into a stack buffer and never throws back into native code.</summary>
public static unsafe class FfmpegLog
{
    private const int RingSize = 64;
    private static readonly Lock Gate = new();
    private static readonly FfmpegLogLine[] Ring = new FfmpegLogLine[RingSize];
    private static long _written;
    private static int _level = ffmpeg.AV_LOG_WARNING;
    private static long _lost;
    private static int _installed;

    [ThreadStatic]
    private static List<FfmpegLogLine>? _capture;

    /// <summary>Raised for each line at or above <see cref="Level"/>, on the thread FFmpeg logged from.</summary>
    public static event Action<FfmpegLogLine>? Message;

    /// <summary>The most verbose level delivered (AV_LOG_WARNING by default).</summary>
    public static int Level
    {
        get => Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, value);
    }

    /// <summary>Lines that could not be delivered (a subscriber threw).</summary>
    public static long Lost => Interlocked.Read(ref _lost);

    /// <summary>The last lines delivered, oldest first.</summary>
    public static IReadOnlyList<FfmpegLogLine> Recent()
    {
        lock (Gate)
        {
            var n = (int)Math.Min(_written, RingSize);
            var list = new List<FfmpegLogLine>(n);
            for (var i = _written - n; i < _written; i++)
                list.Add(Ring[i % RingSize]);
            return list;
        }
    }

    /// <summary>Captures every line FFmpeg logs on the calling thread (any level up to info) until the scope
    /// ends.</summary>
    public static CaptureScope Capture(out List<FfmpegLogLine> lines)
    {
        lines = [];
        var previous = _capture;
        _capture = lines;
        return new CaptureScope(previous);
    }

    /// <summary>Ends a capture and restores the one it nested in.</summary>
    public sealed class CaptureScope(List<FfmpegLogLine>? previous) : IDisposable
    {
        public void Dispose() => _capture = previous;
    }

    /// <summary>Installs the callback once the libraries are loaded (the loader calls it).</summary>
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        var util = FfmpegLoader.Handle("avutil");
        if (util == 0 || !NativeLibrary.TryGetExport(util, "av_log_format_line2", out var format))
        {
            // Without the formatter the lines cannot be read; keep FFmpeg quiet but for errors.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            return;
        }
        LogFormatter.Bind(format);
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);
        ffmpeg.av_log_set_callback(new av_log_set_callback_callback_func
        {
            Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, void>)&OnLog,
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(void* avcl, int level, byte* fmt, byte* vl)
    {
        try
        {
            var capture = _capture;
            if (level > Volatile.Read(ref _level) && (capture is null || level > ffmpeg.AV_LOG_INFO))
                return;
            const int size = 1024;
            var line = stackalloc byte[size];
            var prefix = 1;
            var n = LogFormatter.Format(avcl, level, fmt, vl, line, size, &prefix);
            if (n <= 0)
                return;
            var text = Encoding.UTF8.GetString(line, Math.Min(n, size - 1)).TrimEnd('\n', '\r');
            if (text.Length == 0)
                return;
            var entry = new FfmpegLogLine(level, text);
            capture?.Add(entry);
            if (level > Volatile.Read(ref _level))
                return;
            lock (Gate)
            {
                Ring[_written % RingSize] = entry;
                _written++;
            }
            Message?.Invoke(entry);
        }
        catch (Exception)
        {
            // Nothing may unwind into FFmpeg's C frames; a line lost from the log is the lesser harm, and counted.
            Interlocked.Increment(ref _lost);
        }
    }
}

/// <summary>FFmpeg's line formatter, bound by address from avutil (kept apart from the log's event so the
/// function-pointer call sits in a type of its own).</summary>
internal static unsafe class LogFormatter
{
    private static nint _formatLine;

    public static void Bind(nint address) => _formatLine = address;

    public static int Format(void* avcl, int level, byte* fmt, byte* vl, byte* line, int size, int* prefix)
    {
        var f = (delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, byte*, int, int*, int>)_formatLine;
        return f is null ? 0 : f(avcl, level, fmt, vl, line, size, prefix);
    }
}
