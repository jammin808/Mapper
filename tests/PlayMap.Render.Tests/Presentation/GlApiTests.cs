using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using Xunit.Abstractions;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>decode.glApi on a window system (Xvfb): "egl" makes the desktop context on EGL — its display imports dmabufs, so the
/// render node's VAAPI frames can be sampled in place — and its output windows on EGL too (a shared context comes from one API);
/// "glx" makes even an ES context on GLX; "auto" keeps a desktop context on the window system's own API (GLX under X, WGL on
/// Windows); a word it does not know is the platform's choice, with words, on every platform. Each platform is made, read and
/// disposed on a thread of its own.</summary>
public sealed class GlApiTests(ITestOutputHelper log)
{
    private static readonly GlDeviceOptions Device = new(CheckErrors: true, DebugOutput: false);

    private static void OnOwnThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { Name = "gl api test" };
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("the GL API test failed on its thread", failure);
    }

    /// <summary>The API a desktop context comes up on when nothing else is asked: the window system's own.</summary>
    private static string NativeApi => OperatingSystem.IsWindows() ? "wgl" : OperatingSystem.IsMacOS() ? "cocoa" : "glx";

    [Fact]
    public void EglMakesTheDesktopContextAndItsWindowsOnEglWhoseDisplayImportsDmabufs()
    {
        OnOwnThread(() =>
        {
            using var platform = GpuPlatforms.Create(new PlatformRequest("gl") { GlApi = "egl" }, Device);
            log.WriteLine($"{platform.Api} · {platform.Context.Device.Version} · {platform.Context.Device.Renderer} · '{platform.Words}'");
            Assert.Equal("egl", platform.Api);
            Assert.Equal("", platform.Words);
            Assert.Equal(GlDialect.Core330, platform.Context.Dialect);
            Assert.True(platform.Context.EglWindows);
            Assert.Equal("egl", GlDevice.CurrentBinding());

            // The display imports dmabufs: the decode share is made on it, and says why zero-copy waits on a software GL.
            var share = DmaBufShare.ForCurrentContext(platform.Share, platform.Context.Device.Renderer);
            Assert.NotNull(share);
            log.WriteLine($"{share.Describe} · ready {share.Ready} · '{share.Words}'");
            if (platform.Context.Device.Renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase))
                Assert.StartsWith("the GL is software", share.ZeroCopyWords, StringComparison.Ordinal);

            // An output window shares the context's objects, so it is made on EGL too.
            using (var window = platform.CreatePresenter(new OutputPresenterOptions("gl api test", null, false, 64, 36, VSync: false)))
            {
                Assert.Equal(PresentPath.GlWindow, window.Path);
                using var picture = platform.Context.Device.CreateTexture(64, 36, TextureFormat.Rgba8);
                Assert.True(window.Present(picture).Queued);
            }
            platform.Context.MakeCurrent();
            Assert.Equal("egl", GlDevice.CurrentBinding());
        });
    }

    [Fact]
    public void GlxMakesEvenAnEsContextOnGlx()
    {
        OnOwnThread(() =>
        {
            using var platform = GpuPlatforms.Create(new PlatformRequest("gl", "es300") { GlApi = "GLX" }, Device);
            Assert.Equal("glx", platform.Api);
            Assert.Equal(GlDialect.Es300, platform.Context.Dialect);
            Assert.False(platform.Context.EglWindows);
            Assert.Equal("glx", GlDevice.CurrentBinding());
        });
    }

    [Fact]
    public void AutoKeepsADesktopContextOnTheNativeApiAndAnUnknownWordSaysSo()
    {
        OnOwnThread(() =>
        {
            using (var platform = GpuPlatforms.Create(new PlatformRequest("gl"), Device))
            {
                Assert.Equal(NativeApi, platform.Api);
                Assert.Equal(GlDialect.Core330, platform.Context.Dialect);
                Assert.False(platform.Context.EglWindows);
                Assert.Equal("", platform.Words);
            }
            using (var platform = GpuPlatforms.Create(new PlatformRequest("gl") { GlApi = "wayland" }, Device))
            {
                Assert.Equal(NativeApi, platform.Api);
                Assert.Equal("decode.glApi 'wayland' is none of auto, egl, glx: the context is made the platform's way", platform.Words);
            }
        });
    }
}
