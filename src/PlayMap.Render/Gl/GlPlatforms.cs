using PlayMap.Core.Gpu;
using PlayMap.Render.Gl.Windows;
using PlayMap.Render.Platform;
using Silk.NET.GLFW;

namespace PlayMap.Render.Gl;

/// <summary>The GL backend's platforms as <see cref="GpuPlatforms"/> makes them: a GL context of the asked dialect and window-system
/// API, hidden or surfaceless, and on Windows the DXGI flip-model platform through ANGLE.</summary>
internal static class GlPlatforms
{
    /// <summary>The GL platform of the request's dialect ("auto" is desktop 3.3 core) and window-system API, with the words already
    /// said first; a word it does not know is the platform's choice, with words. Throws with words when no GL context comes up (a
    /// window system's refusal as <see cref="PlatformNotSupportedException"/>).</summary>
    public static IGpuPlatform Create(PlatformRequest request, GpuDeviceOptions device, List<string> words)
    {
        var asked = GlDialects.Parse(request.Dialect);
        if (asked is null && !GpuPlatforms.IsAuto(request.Dialect))
            words.Add($"GL dialect '{request.Dialect}' is none of auto, core330, es300: the context is desktop 3.3 core");
        var options = GlDeviceOptions.From(device, asked ?? GlDialect.Core330);
        var api = GlApis.Parse(request.GlApi);
        if (api is null)
            words.Add($"decode.glApi '{request.GlApi}' is none of auto, egl, glx: the context is made the platform's way");
        RenderContext context;
        try
        {
            context = request.Surfaceless ? RenderContext.CreateSurfaceless(options) : RenderContext.CreateHidden(options, api ?? GlApi.Auto);
        }
        catch (GlfwException ex)
        {
            throw new PlatformNotSupportedException(ex.Message, ex);
        }
        if (context.Words.Length > 0)
            words.Add(context.Words);
        return new GlPlatform(context, string.Join("; ", words));
    }

    /// <summary>This machine's DXGI platform maker: the flip-model platform through ANGLE on Windows (on WARP when asked), none
    /// elsewhere.</summary>
    public static Func<GpuDeviceOptions, IGpuPlatform>? Dxgi(bool warp) => OperatingSystem.IsWindows() ? AngleMaker(warp) : null;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Func<GpuDeviceOptions, IGpuPlatform> AngleMaker(bool warp) => o => AnglePlatform.Create(GlDeviceOptions.From(o, GlDialect.Es300), warp);
}
