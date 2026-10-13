using System.Diagnostics;
using System.Globalization;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;
using PlayMap.Media;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The render host tests run one at a time, apart from every other test: each owns a GL context on
/// its own thread, and one of them takes the process's display away for a moment.</summary>
[CollectionDefinition("render host", DisableParallelization = true)]
public sealed class RenderHostCollection;

/// <summary>An engine on a hand-moved clock with a render host started on it (headless unless asked), a
/// node id of its own (the preview rings are files shared by every process of this user), and the helpers
/// the tests read pictures with.</summary>
internal sealed class HostRig : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private HostRig(ManualClock clock, EngineCore engine, RenderHost host)
    {
        Clock = clock;
        Engine = engine;
        Host = host;
    }

    public ManualClock Clock { get; }
    public EngineCore Engine { get; }
    public RenderHost Host { get; }

    public static HostRig Start(Project project, IMediaBackend? media = null, RenderHostOptions? options = null)
    {
        var host = RenderHost.TryCreate(options ?? RenderHostOptions.Default with { Windows = false }, out var why)
            ?? throw new InvalidOperationException("no render host: " + why);
        var clock = new ManualClock(5_000_000_000);
        var services = EngineServices.ForTests(clock);
        var node = Guid.NewGuid().ToString("N")[..8];
        services = services with { Settings = services.Settings with { NodeId = node }, Outputs = host, Media = media };
        var engine = new EngineCore(services, project);
        host.Start(engine);
        return new HostRig(clock, engine, host);
    }

    public async Task<CommandReply> Run(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    public async Task Ok(string line)
    {
        var r = await Run(line);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
    }

    /// <summary>An output's picture after the render thread has drawn a couple more frames.</summary>
    public async Task<byte[]> Read(string outputId)
    {
        await Host.FramesAsync(2).WaitAsync(Patience);
        return await Host.ReadOutputAsync(outputId).WaitAsync(Patience) ?? throw new InvalidOperationException($"{outputId} has drawn nothing");
    }

    /// <summary>Reads until the picture satisfies the condition (a decoder opening, a build landing).</summary>
    public async Task<byte[]> ReadUntil(string outputId, Func<byte[], bool> good)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var px = await Read(outputId);
            if (good(px))
                return px;
            Assert.True(waited.Elapsed < Patience, $"{outputId} never showed what was expected");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Engine.DisposeAsync();
    }

    public static (int R, int G, int B) At(byte[] bgra, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    public static Output Screen(string id, SourceRef source, int w, int h, OutputKind kind = OutputKind.Virtual, string display = "") =>
        new(id, id, kind, display, w, h, 60, OutputMode.Source, null, source, new RectD(0, 0, w, h),
            OutputWarp.None, BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [], new RectD(0, 0, w, h), 0, true);

    public static Project WithOutputs(params Output[] outputs) => Project.CreateDefault("Test") with { Outputs = [.. outputs] };
}

/// <summary>The media edge for tests: library items open through PlayMap.Media as the node's own adapter would.</summary>
internal sealed class MediaFiles : IMediaBackend
{
    private int _opened;

    /// <summary>Sources opened (on the provider's workers, two at once when an item prerolls the next).</summary>
    public int Opened => Volatile.Read(ref _opened);

    public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) =>
        throw new NotSupportedException("tests import nothing");

    public IVideoSource? Open(MediaItem item)
    {
        Interlocked.Increment(ref _opened);
        return MediaSources.Open(item);
    }

    public MediaReadiness Readiness(MediaItem item) => new(true, true, true, true, "ready");

    public string? PathForHash(string contentHash) => null;
}

/// <summary>Clips made at test time by the ffmpeg tool, each frame carrying its index: the top 24 rows are
/// eight 8-pixel blocks, block b white when bit b of the frame number is set.</summary>
internal static class Clips
{
    private static readonly Lazy<string> Folder = new(() => Directory.CreateTempSubdirectory("playmap-stage-tests-").FullName);

    public static string Ffmpeg()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FfmpegLoader.EnvironmentVariable)))
            Environment.SetEnvironmentVariable(FfmpegLoader.EnvironmentVariable, "/opt/ffmpeg9/lib");
        FfmpegLoader.EnsureLoaded();
        var libs = Environment.GetEnvironmentVariable(FfmpegLoader.EnvironmentVariable)!;
        var tool = Path.GetFullPath(Path.Combine(libs, "..", "bin", "ffmpeg"));
        return File.Exists(tool) ? tool : "/opt/ffmpeg9/bin/ffmpeg";
    }

    /// <summary>A 64 × 48 H.264 clip at 25 fps, <paramref name="seconds"/> long, every frame its index painted.</summary>
    public static string Counted(double seconds)
    {
        var path = Path.Combine(Folder.Value, string.Create(CultureInfo.InvariantCulture, $"counted-{seconds:0.##}.mp4"));
        if (File.Exists(path))
            return path;
        var painter = "geq=lum='if(lt(Y,24),if(mod(floor(N/pow(2,floor(X/8))),2),235,16),128)':cb=128:cr=128";
        var args = string.Create(CultureInfo.InvariantCulture,
            $"-hide_banner -loglevel error -y -f lavfi -i color=c=black:s=64x48:r=25:d={seconds} -vf {painter} -c:v libx264 -preset ultrafast -qp 0 -g 25 -pix_fmt yuv420p \"{path}\"");
        using var p = Process.Start(new ProcessStartInfo(Ffmpeg(), args) { RedirectStandardError = true, UseShellExecute = false })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && File.Exists(path), "ffmpeg: " + err);
        return path;
    }

    /// <summary>A 64 × 48 H.264 clip at 25 fps of one flat colour (an ffmpeg colour word or 0xRRGGBB).</summary>
    public static string Solid(string color, double seconds)
    {
        var path = Path.Combine(Folder.Value, string.Create(CultureInfo.InvariantCulture, $"solid-{color.TrimStart('0', 'x')}-{seconds:0.##}.mp4"));
        if (File.Exists(path))
            return path;
        var args = string.Create(CultureInfo.InvariantCulture,
            $"-hide_banner -loglevel error -y -f lavfi -i color=c={color}:s=64x48:r=25:d={seconds} -c:v libx264 -preset ultrafast -qp 0 -g 25 -pix_fmt yuv444p \"{path}\"");
        using var p = Process.Start(new ProcessStartInfo(Ffmpeg(), args) { RedirectStandardError = true, UseShellExecute = false })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && File.Exists(path), "ffmpeg: " + err);
        return path;
    }

    /// <summary>The frame index a counted clip's picture carries (read from a BGRA output of its width).</summary>
    public static int IndexIn(byte[] bgra, int width)
    {
        var n = 0;
        for (var b = 0; b < 8; b++)
            if (HostRig.At(bgra, width, b * 8 + 4, 12).G > 128)
                n |= 1 << b;
        return n;
    }
}
