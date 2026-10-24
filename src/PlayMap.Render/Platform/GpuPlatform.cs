using System.Globalization;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Core.Presentation;

namespace PlayMap.Render.Platform;

/// <summary>What a node asks of its platform: the present path ("auto" | "gl" | "dxgi") and the GL dialect ("auto" | "core330" |
/// "es300"), from <c>NodeSettings.Present</c>; whether the node runs with no window system (offscreen only); whether the DXGI path
/// renders on WARP (the software rasteriser, for CI); the window-system API of a GL context on Linux (<see cref="GlApi"/>); and words
/// that already explain the choice (a blown fuse), which lead the platform's own.</summary>
public sealed record PlatformRequest(string Path = "auto", string Dialect = "auto", bool Surfaceless = false)
{
    public static readonly PlatformRequest Default = new();

    public bool Warp { get; init; }

    public string Words { get; init; } = "";

    /// <summary>How a windowed GL context is made on Linux (<c>NodeSettings.Decode.GlApi</c>): "auto" (an ES context on EGL, a desktop
    /// one on GLX), "egl" (both on EGL: the display then imports VAAPI's dmabufs), "glx" (both on GLX). A surfaceless context is EGL
    /// whatever this says; other platforms have one API and ignore it.</summary>
    public string GlApi { get; init; } = "auto";
}

/// <summary>The checks a device runs with: its error state read after each resource call and draw (a stall on some drivers), and
/// the driver's debug output subscribed to where it offers one; <see cref="Log"/> takes the notes that are not errors (null drops
/// them). The build's default checks in debug builds only.</summary>
public sealed record GpuDeviceOptions(bool CheckErrors, bool DebugOutput, Action<string>? Log = null)
{
#if DEBUG
    public static readonly GpuDeviceOptions Default = new(true, true);
#else
    public static readonly GpuDeviceOptions Default = new(false, false);
#endif
}

/// <summary>Makes the platform a node asks for. It is the one place the backends are composed: it names the GL backend's platform
/// maker and no other GL type.</summary>
public static class GpuPlatforms
{
    /// <summary>The platform of the asked path and dialect: "dxgi" is the flip-model platform on Windows (experimental, explicit only),
    /// GL with words where it is not there or did not start (the words name the stage it stopped at); "auto" and "gl" are the GL
    /// platform of the asked dialect (auto is desktop 3.3 core) — AUTO is OpenGL on every platform this round. ES on Linux comes up on
    /// EGL, else GLX with words (<see cref="IGpuPlatform.Words"/>); a path or dialect word it does not know is the platform's choice,
    /// with words. Throws with words when no GL context comes up at all.</summary>
    public static IGpuPlatform Create(PlatformRequest request, GpuDeviceOptions device)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Create(request, device, Gl.GlPlatforms.Dxgi(request.Warp));
    }

    /// <summary>As <see cref="Create(PlatformRequest, GpuDeviceOptions)"/>, with the DXGI platform's maker given (null: this machine
    /// has none): tests hand in one that fails at a chosen stage.</summary>
    public static IGpuPlatform Create(PlatformRequest request, GpuDeviceOptions device, Func<GpuDeviceOptions, IGpuPlatform>? dxgi)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(device);
        var words = new List<string>(4);
        if (request.Words.Length > 0)
            words.Add(request.Words);
        var path = request.Path.Trim();
        if (path.Equals("dxgi", StringComparison.OrdinalIgnoreCase))
        {
            if (dxgi is null)
            {
                words.Add("the DXGI path is Windows only: outputs present through OpenGL");
            }
            else
            {
                try
                {
                    return dxgi(device);
                }
                catch (GpuPlatformStartException ex)
                {
                    words.Add($"the DXGI path stopped at {GpuPlatformStartException.Word(ex.Stage)}: {ex.Message}; outputs present through OpenGL");
                }
                catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
                    or PlatformNotSupportedException or BadImageFormatException)
                {
                    words.Add($"the DXGI path did not start: {ex.Message}; outputs present through OpenGL");
                }
            }
        }
        else if (!IsAuto(path) && !path.Equals("gl", StringComparison.OrdinalIgnoreCase))
        {
            words.Add($"present path '{request.Path}' is none of auto, gl, dxgi: outputs present through GL");
        }
        return Gl.GlPlatforms.Create(request, device, words);
    }

    /// <summary>A settings word that leaves the choice to the platform: empty, or "auto".</summary>
    internal static bool IsAuto(string? word) => string.IsNullOrWhiteSpace(word) || word.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
}
