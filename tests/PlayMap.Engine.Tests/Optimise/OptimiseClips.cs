using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Optimise;

/// <summary>Clips the optimise tests make at test time with FFmpeg's command-line tool, from its own test sources and only
/// LGPL-compatible encoders (rawvideo, ffv1, mpeg4, aac, pcm; FFmpeg's own hap encoder as the quality reference) — never a
/// GPL encoder, and nothing is committed. Also the tool's decode of a file back to raw RGBA, and ffprobe's stream facts.</summary>
internal static class OptimiseClips
{
    public static string Tool(string name = "ffmpeg")
    {
        FfmpegFactAttribute.UseDefaultLibraries();
        var ffmpeg = FfmpegFactAttribute.Tool() ?? throw new InvalidOperationException("no ffmpeg tool");
        return name == "ffmpeg" ? ffmpeg : Path.Combine(Path.GetDirectoryName(ffmpeg)!, name);
    }

    /// <summary>Runs the tool with these words and answers its standard output; fails the test with its errors when it fails.</summary>
    public static byte[] Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(Tool(tool)) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var output = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(output);
        var err = p.StandardError.ReadToEnd();
        copy.GetAwaiter().GetResult();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{tool} {string.Join(' ', args)}: {err}");
        return output.ToArray();
    }

    /// <summary>A lossless RGBA clip of FFmpeg's testsrc2 (with the research's noise, alls=12 temporal, when asked), <paramref name="frames"/> frames at 25 fps.</summary>
    public static string TestSource(string folder, int width, int height, int frames, bool noisy)
    {
        var path = Path.Combine(folder, noisy ? "noisy.nut" : "clean.nut");
        var source = string.Create(CultureInfo.InvariantCulture, $"testsrc2=s={width}x{height}:r=25");
        var filter = noisy ? "noise=alls=12:allf=t,format=rgba" : "format=rgba";
        Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", source, "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
            "-vf", filter, "-c:v", "rawvideo", path);
        return path;
    }

    /// <summary>An MPEG-4 Part 2 clip (FFmpeg's own LGPL encoder) of testsrc2 with a 440 Hz tone in AAC, as an .mp4.</summary>
    public static string Mpeg4WithAudio(string folder, int width, int height, double seconds, string name = "clip.mp4")
    {
        var path = Path.Combine(folder, name);
        var c = CultureInfo.InvariantCulture;
        Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", string.Create(c, $"testsrc2=s={width}x{height}:r=25:d={seconds}"),
            "-f", "lavfi", "-i", string.Create(c, $"sine=frequency=440:sample_rate=48000:duration={seconds}"),
            "-c:v", "mpeg4", "-q:v", "2", "-c:a", "aac", "-b:a", "128k", "-shortest", path);
        return path;
    }

    /// <summary>A file decoded by the tool to raw RGBA frames.</summary>
    public static byte[] Rgba(string path) =>
        Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgba", "-");

    /// <summary>FFmpeg's own HAP encoder on a file ("hap" or "hap_q").</summary>
    public static string FfmpegHap(string source, string format, string output)
    {
        Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-c:v", "hap", "-format", format, output);
        return output;
    }

    /// <summary>The average PSNR over RGB of two equal runs of raw RGBA frames.</summary>
    public static double AveragePsnr(byte[] a, byte[] b, int width, int height)
    {
        var frame = width * height * 4;
        Assert.Equal(a.Length, b.Length);
        var n = a.Length / frame;
        double sum = 0;
        for (var i = 0; i < n; i++)
            sum += Core.Media.Optimise.Psnr.Rgb(a.AsSpan(i * frame, frame), b.AsSpan(i * frame, frame));
        return sum / n;
    }

    /// <summary>ffprobe's streams of a file: codec, codec tag, start time and duration (seconds) per stream.</summary>
    public static List<(string Type, string Codec, string Tag, double Start, double Duration)> Streams(string path)
    {
        var json = Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_type,codec_name,codec_tag_string,start_time,duration", "-of", "json", path);
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, string, string, double, double)>();
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            list.Add((s.GetProperty("codec_type").GetString()!, s.TryGetProperty("codec_name", out var c) ? c.GetString()! : "",
                s.GetProperty("codec_tag_string").GetString()!, Seconds(s, "start_time"), Seconds(s, "duration")));
        }
        return list;
    }

    private static double Seconds(JsonElement s, string key) =>
        s.TryGetProperty(key, out var v) && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
}

/// <summary>A thread that owns a hidden GL context, for code that must run where its context is current.</summary>
internal sealed class GlThread : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = [];
    private readonly Thread _thread;
    private Render.Gl.RenderContext? _context;

    public GlThread()
    {
        var ready = new TaskCompletionSource();
        _thread = new Thread(() =>
        {
            try
            {
                _context = Render.Gl.RenderContext.CreateHidden(new Render.Gl.GlDeviceOptions(true, false));
                ready.SetResult();
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }
            foreach (var work in _work.GetConsumingEnumerable())
                work();
            _context.Dispose();
        }) { IsBackground = true, Name = "optimise gl" };
        _thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    public Render.Gl.RenderContext Context => _context!;

    public T Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        _work.Add(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join();
        _work.Dispose();
    }
}
