using System.Diagnostics;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Hap;

/// <summary>The `hap` program's side on the CPU: which textures a HAP layout's planes are, which mode the pass runs in, its
/// draw, and a CPU twin of its YCoCg maths the tests hold the shader to.</summary>
public static class HapPass
{
    public const int ModeRaw = 0, ModeYCoCg = 1, ModeYCoCgAlpha = 2, ModeAlphaOnly = 3, ModeHdr = 4;

    /// <summary>The pass's mode for a layout (raw for HAP, HAP Alpha and HAP R).</summary>
    public static int Mode(PixelLayout layout) => layout switch
    {
        PixelLayout.Bc3YCoCg => ModeYCoCg,
        PixelLayout.Bc3YCoCgBc4 => ModeYCoCgAlpha,
        PixelLayout.Bc4 => ModeAlphaOnly,
        PixelLayout.Bc6hUfloat or PixelLayout.Bc6hSfloat => ModeHdr,
        _ => ModeRaw,
    };

    /// <summary>The texture format of each plane of a block layout (the second is null for one plane).</summary>
    public static (TextureFormat First, TextureFormat? Second) Formats(PixelLayout layout) => layout switch
    {
        PixelLayout.Bc1 => (TextureFormat.Bc1, null),
        PixelLayout.Bc3 or PixelLayout.Bc3YCoCg => (TextureFormat.Bc3, null),
        PixelLayout.Bc3YCoCgBc4 => (TextureFormat.Bc3, TextureFormat.Bc4),
        PixelLayout.Bc4 => (TextureFormat.Bc4, null),
        PixelLayout.Bc7 => (TextureFormat.Bc7, null),
        PixelLayout.Bc6hUfloat => (TextureFormat.Bc6hUfloat, null),
        PixelLayout.Bc6hSfloat => (TextureFormat.Bc6hSfloat, null),
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "not a block layout"),
    };

    /// <summary>The pass's transfer code for a HAP HDR picture: 0 linear, 1 PQ, 2 HLG, 3 display SDR.</summary>
    public static int Transfer(TransferFunction transfer) => transfer switch
    {
        TransferFunction.Pq => 1,
        TransferFunction.Hlg => 2,
        TransferFunction.Sdr => 3,
        _ => 0,
    };

    /// <summary>The pass writes the picture whole, opaque.</summary>
    private static readonly DrawState Drawn = DrawState.Opaque;

    /// <summary>The pass's draw into pictures of <paramref name="target"/>'s shape.</summary>
    internal static void Pipelines(PipelineList keys, TargetShape target)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Add("hap", Drawn, target);
    }

    /// <summary>Draws the pass of <paramref name="program"/> (the device's <c>hap</c>) into <paramref name="target"/> (the picture's
    /// visible size) in a pass of its own on the frame's list.</summary>
    public static void Draw(ICommandList list, ProgramId program, RenderTarget target, GpuTexture first, GpuTexture? second, int mode,
        double hdrScale, int transfer)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(first);
        using (list.Pass(target, PassDesc.Keep))
            list.Draw(program, Drawn)
                .SetInt("uMode", mode)
                .Set("uHdr", (float)hdrScale, transfer)
                .Texture("uTex0", first)
                .Texture("uTex1", second ?? first)
                .Fullscreen();
        target.Color.Version++;
    }

    /// <summary>The pass's YCoCg maths on one texel (channels 0..1, as sampled), clamped as the shader clamps.</summary>
    public static (float R, float G, float B) YCoCgToRgb(float r, float g, float b, float a)
    {
        var s = b * (255f / 8f) + 1f;
        var co = (r - 0.50196078431373f) / s;
        var cg = (g - 0.50196078431373f) / s;
        var y = a;
        return (Math.Clamp(y + co - cg, 0, 1), Math.Clamp(y + cg, 0, 1), Math.Clamp(y - co - cg, 0, 1));
    }
}

/// <summary>Compressed bytes this process has handed the GPU in uploads of at least <see cref="MinBytes"/>, and the render-thread
/// time they took: the upload term of the HAP layer budget. A smaller upload is not counted, because its fixed cost per call
/// says nothing about the rate a frame of a megabyte or more moves at (on a software rasteriser a tiny frame reads as a few
/// MB/s); until one such upload has run the term is unmeasured (0). Totals only rise; a reader takes the difference of two
/// looks.</summary>
public static class HapUploadMeter
{
    /// <summary>The smallest upload that counts: 10⁶ bytes (a 1080p HAP frame is 1.04 MB).</summary>
    public const long MinBytes = 1_000_000;

    private static long _bytes;
    private static long _ticks;

    public static long Bytes => Interlocked.Read(ref _bytes);

    public static TimeSpan Time => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _ticks));

    /// <summary>MB (10⁶ bytes) per second of counted upload time so far; 0 before any counted upload.</summary>
    public static double MbPerSecond
    {
        get
        {
            var seconds = Time.TotalSeconds;
            return seconds > 0 ? Bytes / 1e6 / seconds : 0;
        }
    }

    internal static void Add(long bytes, long ticks)
    {
        if (bytes < MinBytes)
            return;
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _ticks, ticks);
    }
}
