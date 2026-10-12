using System.Collections.Concurrent;
using System.Diagnostics;
using PlayMap.Core.Media;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Tests;

/// <summary>The media the tests play, made at test time by the ffmpeg command-line tool into a private temp
/// folder (once per run, each clip on first use). The libraries are found through PLAYMAP_FFMPEG_DIR; when it is
/// not set the tests point it at /opt/ffmpeg9/lib, the folder this repository's CI installs FFmpeg 9 into. The
/// tool is PLAYMAP_FFMPEG_DIR/../bin/ffmpeg, else /opt/ffmpeg9/bin/ffmpeg, else ffmpeg beside the libraries.
/// <para>Every video clip carries its frame index in its picture: the top 40 rows are sixteen 20-pixel blocks,
/// block b white when bit b of the frame number is set, so a test reads back exactly which frame it got.</para></summary>
public static class TestMedia
{
    public const string DefaultLibraryFolder = "/opt/ffmpeg9/lib";

    private static readonly ConcurrentDictionary<string, Lazy<string>> Made = new(StringComparer.Ordinal);
    private static readonly Lazy<string> Folder = new(MakeFolder);

    static TestMedia()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FfmpegLoader.EnvironmentVariable)))
            Environment.SetEnvironmentVariable(FfmpegLoader.EnvironmentVariable, DefaultLibraryFolder);
        FfmpegLoader.EnsureLoaded();
    }

    /// <summary>Points the loader at the libraries and loads them (every test calls this first, directly or
    /// through <see cref="Clip"/>).</summary>
    public static void Ensure() => _ = Folder.Value;

    public static string Dir => Folder.Value;

    /// <summary>The geq expression that paints the frame index into the top 40 rows (and a moving texture below,
    /// so the encoders have something to code).</summary>
    public const string IndexPainter =
        "geq=lum='if(lt(Y,40),if(mod(floor(N/pow(2,floor(X/20))),2),235,16),128+60*sin(X/9+N/5))':cb=128:cr=128";

    /// <summary>A clip by name, made on first use. Names: h264_25.mp4, h264_2997.mp4, h264_long.mp4, hevc.mp4,
    /// hevc10.mp4, vp9.webm, av1.mkv, prores.mov, prores4444a.mov, mjpeg.mov, unknown.avi, still.png, still16.png,
    /// still.exr, still.jpg, seq (a folder of 12 PNGs), tone48.wav, tone44.wav, tone.m4a, av.mp4, notag.wav.</summary>
    public static string Clip(string name)
    {
        Ensure();
        return Made.GetOrAdd(name, n => new Lazy<string>(() => Make(n))).Value;
    }

    private static string MakeFolder()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-media-tests-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A file still open at exit stays behind in the private temp folder; the OS cleans it.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        };
        return dir;
    }

    /// <summary>The ffmpeg command-line tool.</summary>
    public static string Tool()
    {
        var libs = Environment.GetEnvironmentVariable(FfmpegLoader.EnvironmentVariable) ?? DefaultLibraryFolder;
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var candidate in new[]
                 {
                     Path.Combine(libs, "..", "bin", name),
                     Path.Combine("/opt/ffmpeg9/bin", name),
                     Path.Combine(libs, name),
                 })
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException("the ffmpeg tool was not found beside " + libs);
    }

    /// <summary>Runs the ffmpeg tool; throws with its output when it fails.</summary>
    public static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Tool())
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg " + string.Join(' ', args) + " failed: " + err.Result);
    }

    private static string Src(string size, string rate, string seconds) =>
        $"nullsrc=s={size}:r={rate}:d={seconds},format=yuv420p,{IndexPainter}";

    private static string Make(string name)
    {
        var path = Path.Combine(Dir, name);
        switch (name)
        {
            case "h264_25.mp4":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "2"), "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-g", "25", "-bf", "2",
                    "-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", path);
                break;
            case "h264_2997.mp4":
                Run("-f", "lavfi", "-i", Src("320x240", "30000/1001", "2"), "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-g", "30", "-bf", "2",
                    "-pix_fmt", "yuv420p", path);
                break;
            case "h264_long.mp4":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "20"), "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-g", "50", "-bf", "2",
                    "-pix_fmt", "yuv420p", path);
                break;
            case "h264_1080.mp4":
                Run("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=25:d=6", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-g", "50",
                    "-pix_fmt", "yuv420p", path);
                break;
            case "hevc.mp4":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "1"), "-c:v", "libx265", "-preset", "ultrafast", "-crf", "12", "-x265-params", "log-level=error",
                    "-pix_fmt", "yuv420p", "-tag:v", "hvc1", path);
                break;
            case "hevc10.mp4":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "1"), "-c:v", "libx265", "-preset", "ultrafast", "-crf", "12",
                    "-x265-params", "log-level=error", "-pix_fmt", "yuv420p10le", "-tag:v", "hvc1", path);
                break;
            case "vp9.webm":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "1"), "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "0", "-crf", "15",
                    "-pix_fmt", "yuv420p", path);
                break;
            case "av1.mkv":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "1"), "-c:v", "libsvtav1", "-preset", "12", "-crf", "20", "-pix_fmt", "yuv420p", path);
                break;
            case "prores.mov":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "1"), "-c:v", "prores_ks", "-profile:v", "2", path);
                break;
            case "prores4444a.mov":
                Run("-f", "lavfi", "-i",
                    "nullsrc=s=320x240:r=25:d=1,format=yuva444p10le,geq=lum='if(lt(Y,40),if(mod(floor(N/pow(2,floor(X/20))),2),940,64),512)':cb=512:cr=512:a='if(lt(Y,120),1023,0)'",
                    "-c:v", "prores_ks", "-profile:v", "4444", "-pix_fmt", "yuva444p10le", "-alpha_bits", "16", path);
                break;
            case "mjpeg.mov":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "1"), "-c:v", "mjpeg", "-q:v", "2", "-pix_fmt", "yuvj420p", path);
                break;
            case "unknown.avi":
                // An AVI whose four-character code no decoder knows: the MJPG tags in the header renamed.
                Run("-f", "lavfi", "-i", Src("320x240", "25", "1"), "-c:v", "mjpeg", "-q:v", "4", "-pix_fmt", "yuvj420p", path);
                var bytes = File.ReadAllBytes(path);
                for (var i = 0; i + 4 <= Math.Min(bytes.Length, 8192); i++)
                {
                    if (bytes[i] == 'M' && bytes[i + 1] == 'J' && bytes[i + 2] == 'P' && bytes[i + 3] == 'G')
                        bytes[i] = bytes[i + 1] = bytes[i + 2] = bytes[i + 3] = (byte)'Z';
                }
                File.WriteAllBytes(path, bytes);
                break;
            case "still.png":
                Run("-f", "lavfi", "-i", "color=c=0x336699:s=64x48:d=1,format=rgba,geq=r='51':g='102':b='153':a='if(lt(X,32),255,128)'", "-frames:v", "1", path);
                break;
            case "still16.png":
                Run("-f", "lavfi", "-i", "color=s=64x48:d=1,format=gbrp16le,geq=r='40000':g='20000':b='65535'", "-frames:v", "1", "-pix_fmt", "rgb48be", path);
                break;
            case "still.exr":
                // Values above 1 (an HDR still): R 2.5, G 0.25, B 1.
                Run("-f", "lavfi", "-i", "color=s=64x48:d=1,format=gbrpf32le,geq=r='2.5':g='0.25':b='1'", "-frames:v", "1", "-c:v", "exr", "-compression", "0", path);
                break;
            case "still.jpg":
                Run("-f", "lavfi", "-i", "color=c=0x808080:s=64x48:d=1", "-frames:v", "1", "-q:v", "2", path);
                break;
            case "seq":
                Directory.CreateDirectory(path);
                Run("-f", "lavfi", "-i", Src("160x120", "25", "0.48"), "-start_number", "0", Path.Combine(path, "frame_%04d.png"));
                break;
            case "tone48.wav":
                Run("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-ac", "2", "-c:a", "pcm_s16le", path);
                break;
            case "tone44.wav":
                Run("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=2", "-ac", "1", "-c:a", "pcm_s16le", path);
                break;
            case "tone.m4a":
                Run("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-ac", "2", "-c:a", "aac", "-b:a", "128k", path);
                break;
            case "av.mp4":
                Run("-f", "lavfi", "-i", Src("320x240", "25", "2"), "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-shortest", path);
                break;
            case "notag.wav":
                // A WAV whose format tag no decoder knows: the probe must say so in words.
                File.Copy(Clip("tone48.wav"), path, overwrite: true);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
                {
                    fs.Position = 20;
                    fs.Write([0x34, 0x12]);
                }
                break;
            default:
                throw new ArgumentException("no such test clip: " + name, nameof(name));
        }
        return path;
    }

    /// <summary>Reads the frame index painted into a picture (any layout the sources produce).</summary>
    public static unsafe int ReadIndex(VideoFrameLease lease)
    {
        var plane = lease.Planes[0];
        var value = 0;
        for (var bit = 0; bit < 16; bit++)
        {
            var x = bit * 20 + 10;
            const int y = 20;
            if (x >= lease.Width)
                break;
            var row = (byte*)plane.Data + (nint)y * plane.Stride;
            var on = lease.Layout switch
            {
                PixelLayout.Yuv420P or PixelLayout.Yuv422P or PixelLayout.Yuv444P or PixelLayout.Nv12 or PixelLayout.Gray8 => row[x] > 128,
                PixelLayout.P010 => ((ushort*)row)[x] > 32768,
                PixelLayout.Yuv420P10 => ((ushort*)row)[x] > 512,
                PixelLayout.Rgba8 or PixelLayout.Bgra8 => row[x * 4 + 1] > 128,
                PixelLayout.RgbaHalf => (float)((Half*)row)[x * 4 + 1] > 0.5f,
                _ => throw new InvalidOperationException("unknown layout " + lease.Layout),
            };
            if (on)
                value |= 1 << bit;
        }
        return value;
    }
}
