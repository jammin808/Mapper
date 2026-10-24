using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Core.Presentation;
using PlayMap.Render.Engine;
using PlayMap.Render.Gl;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>The GL backend's platforms and windows: an output window made on the tests' context presents every frame and its pacer
/// counts them; a surfaceless platform refuses presenters with words; a surfaceless context going leaves another on the same display
/// working; path and dialect words a platform does not know are said, not ignored; an ES context made through the window system
/// renders and presents.</summary>
[Collection("gpu")]
public sealed class GlPlatformTests(BackendFixture backends)
{
    [Fact]
    public void AnAttachedWindowPresentsEveryFrameAndItsPacerCountsThem()
    {
        var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 64, 36)]);
        backends.RunGl(c =>
        {
            Assert.NotEmpty(c.Monitors());
            using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(GpuTiming: false));
            using (var window = OutputWindow.Create(c, new OutputWindowOptions("Output 1", Borderless: false, VSync: false, Width: 64, Height: 36)))
            {
                // Its presents are measured against its display's refresh, not a fixed 60.
                engine.Attach("op1", window, 50);
                Assert.Equal(50, engine.Stats("op1").RefreshHz, 1);
                Assert.Equal(1, window.SwapInterval);
                for (var f = 0; f < 5; f++)
                {
                    engine.Render(Scene.Inputs(project, 1 + f / 60.0));
                    c.PumpEvents();
                }
                Assert.Equal(5, engine.Stats("op1").Presented);
                engine.Detach("op1");
            }
            var px = new byte[64 * 36 * 4];
            engine.Render(Scene.Inputs(project, 2));
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((0x33, 0x66, 0x99), Pixels.At(px, 64, 10, 10));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [LinuxFact]
    [Trait("Category", "Linux")]
    public void ASurfacelessPlatformRefusesPresentersWithWords()
    {
        using var platform = GpuPlatforms.Create(PlatformRequest.Default with { Surfaceless = true }, new GpuDeviceOptions(true, false));
        Assert.Equal("gl-surfaceless", platform.Name);
        Assert.Equal(PresentPath.Offscreen, platform.Path);
        Assert.Empty(platform.Monitors());
        Assert.Equal(Core.Media.GpuApi.OpenGl, platform.Share.Api);
        Assert.Contains("egl-surfaceless", platform.Share.Describe, StringComparison.Ordinal);
        var refused = Assert.Throws<PlatformNotSupportedException>(() => platform.CreatePresenter(new OutputPresenterOptions("x", null, false, 64, 36)));
        Assert.Contains("no window system", refused.Message, StringComparison.Ordinal);
        Assert.Equal("", platform.Words);
    }

    [LinuxFact]
    [Trait("Category", "Linux")]
    public void ASurfacelessContextGoingLeavesAnotherOnTheSameDisplayWorking()
    {
        // Every surfaceless context shares the process's one EGL display: the second one going (on its own thread) must not
        // terminate the display under the first.
        using var first = RenderContext.CreateSurfaceless(new GlDeviceOptions(true, false));
        string? secondRenderer = null;
        var thread = new Thread(() =>
        {
            using var second = RenderContext.CreateSurfaceless(new GlDeviceOptions(true, false));
            secondRenderer = second.Device.Renderer;
        }) { Name = "second surfaceless context" };
        thread.Start();
        thread.Join();
        Assert.False(string.IsNullOrEmpty(secondRenderer));
        Assert.True(first.DisplayLive, "the second context's going terminated the display the first one lives on");

        first.MakeCurrent();
        var d = first.Device;
        using var target = d.CreateRenderTarget(4, 4, TextureFormat.Rgba8);
        d.BindTarget(target);
        d.Clear(0, 1, 0, 1);
        var px = new byte[4 * 4 * 4];
        d.ReadPixels(target, px);
        Assert.Equal(0, px[2]);   // red
        Assert.Equal(255, px[1]); // green
        Assert.Equal(0, px[0]);   // blue
        Assert.Empty(d.TakeErrors());
    }

    /// <summary>Unknown path and dialect words are every platform's: made surfaceless where the platform has it (EGL, Linux), else on
    /// the window system's hidden context.</summary>
    [Fact]
    public void SettingsWordsThePlatformDoesNotKnowAreSaidNotIgnored()
    {
        using var platform = GpuPlatforms.Create(new PlatformRequest("warp", "gles9", Surfaceless: OperatingSystem.IsLinux()),
            new GpuDeviceOptions(true, false));
        Assert.Equal(GlDialect.Core330, platform.Context.Dialect);
        Assert.Contains("present path 'warp' is none of auto, gl, dxgi", platform.Words, StringComparison.Ordinal);
        Assert.Contains("GL dialect 'gles9' is none of auto, core330, es300", platform.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEsContextMadeThroughTheWindowSystemRendersAndPresents()
    {
        // GLFW makes the ES context on EGL here (or on GLX, saying so); output windows share it the same way.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var platform = GpuPlatforms.Create(new PlatformRequest(Dialect: "es300"), new GpuDeviceOptions(true, true));
                var c = platform.Context;
                Assert.Equal(GlDialect.Es300, c.Dialect);
                // On Linux ES is made on EGL (or on GLX, saying so); elsewhere the window system's own API makes it and has nothing to say.
                if (OperatingSystem.IsLinux())
                    Assert.True(c.Api == "egl" || platform.Words.StartsWith("ES through GLX", StringComparison.Ordinal), $"{c.Api}: {platform.Words}");
                else
                    Assert.Equal((OperatingSystem.IsWindows() ? "wgl" : "cocoa", ""), (c.Api, platform.Words));
                var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 64, 36)]);
                using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(GpuTiming: c.Device.HasTimerQuery));
                using (var window = platform.CreatePresenter(new OutputPresenterOptions("ES output", null, false, 64, 36, VSync: false)))
                {
                    engine.Attach("op1", window, 60);
                    for (var f = 0; f < 3; f++)
                    {
                        engine.Render(Scene.Inputs(project, 1 + f / 60.0));
                        platform.PumpEvents();
                    }
                    Assert.Equal(3, engine.Present("op1")!.Value.Presented);
                    engine.Detach("op1");
                }
                var px = new byte[64 * 36 * 4];
                engine.Render(Scene.Inputs(project, 2));
                Assert.True(engine.ReadOutput("op1", px));
                Assert.Equal((0x33, 0x66, 0x99), Pixels.At(px, 64, 10, 10));
                Assert.Empty(c.Device.TakeErrors());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { Name = "es window test" };
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("the ES window context failed", failure);
    }
}
