using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using PlayMap.Render.Timing;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>The presenter seam: the renderer presents through <see cref="IOutputPresenter"/> whatever stands behind it — the GL window
/// (every existing window test runs through it unchanged), or a presenter of the test's own whose presents return on a scripted
/// 60 Hz vblank — and each output's clock and ledger read what the presenter says.</summary>
[Collection("gl")]
public sealed class PresenterSeamTests(GlFixture gl)
{
    /// <summary>A presenter that keeps the pictures it was given and waits, as a vsync'd swap does, for the next vblank of a 60 Hz
    /// grid on the monotonic clock (the one after it when asked to be late), and says it returned just after that vblank — the
    /// instant it scripted, whenever its thread got the CPU back (how the fit copes with late stamps is VblankClockTests').</summary>
    private sealed class ScriptedPresenter : IOutputPresenter
    {
        private const double Period = 1e9 / 60;
        private readonly long _start = MonoClock.Instance.NowNs;

        public int Width => 64;
        public int Height => 36;
        public bool IsClosing => false;
        public PresentPath Path => PresentPath.GlWindow;
        public int SwapInterval { get; private set; }
        public long LastSwapNs { get; private set; }
        public int Presents { get; private set; }
        public int Observed { get; private set; }
        public (int W, int H) LastPicture { get; private set; }
        public bool LateNext { get; set; }
        public bool Full { get; set; }

        public void SetSwapInterval(int interval) => SwapInterval = interval;

        public PresentResult Present(GpuTexture picture)
        {
            if (Full)
                return new PresentResult(false, MonoClock.Instance.NowNs, PresentProblem.Held);
            Presents++;
            LastPicture = (picture.Width, picture.Height);
            var now = MonoClock.Instance.NowNs;
            var k = (long)Math.Floor((now - _start) / Period) + (LateNext ? 2 : 1);
            LateNext = false;
            var returns = _start + (long)Math.Round(k * Period) + 100_000;
            var spin = new SpinWait();
            while (MonoClock.Instance.NowNs < returns)
                spin.SpinOnce(sleep1Threshold: -1);
            LastSwapNs = returns;
            return PresentResult.Done(LastSwapNs);
        }

        public PresentResult PresentBlack() => PresentResult.Done(LastSwapNs);

        public void Observe(PresentClock clock) => Observed++;

        public void Dispose()
        {
            // Holds nothing.
        }
    }

    [Fact]
    public void TheRendererPresentsThroughAnyPresenterAndItsClockReadsWhatThePresenterSays()
    {
        var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 64, 36)]);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var presenter = new ScriptedPresenter();
            engine.Attach("op1", presenter, 60);
            Assert.Equal(1, presenter.SwapInterval);
            Assert.Same(presenter, engine.PresenterOf("op1"));
            for (var f = 0; f < 90; f++)
            {
                presenter.LateNext = f == 60;
                engine.Render(Scene.Inputs(project, 1 + f / 60.0));
            }
            Assert.Equal(90, presenter.Presents);
            Assert.Equal(90, presenter.Observed);
            Assert.Equal((64, 36), presenter.LastPicture);
            var present = engine.Present("op1")!.Value;
            Assert.Equal(PresentPath.GlWindow, present.Path);
            Assert.True(present.Measured);
            Assert.True(present.Settled);
            // The clock reads what the presenter says: the grid's rate, and the frame made late counted (a test thread that comes
            // to a present after its vblank waits for the next one, a miss of its own; the exact counting is VblankClockTests').
            Assert.InRange(present.RefreshHz, 59.8, 60.2);
            Assert.Equal(90, present.Presented);
            Assert.InRange(present.MissedVblanks, 1, 10);
            Assert.NotNull(engine.NextVblankNs("op1", MonoClock.Instance.NowNs));

            // A full queue holds the picture: counted, never a block.
            presenter.Full = true;
            engine.Render(Scene.Inputs(project, 3));
            Assert.Equal(1, engine.Present("op1")!.Value.Held);

            // Detached, the output counts offscreen against its own refresh.
            engine.Detach("op1");
            Assert.Null(engine.PresenterOf("op1"));
            engine.Render(Scene.Inputs(project, 3.1));
            Assert.Equal(PresentPath.Offscreen, engine.Present("op1")!.Value.Path);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void TheFirstPresenterPacesAndTheOthersPresentAtOnceUnmeasured()
    {
        var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 64, 36), Scene.SourceOutput("op2", SourceRef.Solid("#996633"), 64, 36)]);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var first = new ScriptedPresenter();
            using var second = new ScriptedPresenter();
            engine.Attach("op1", first, 60);
            engine.Attach("op2", second, 60);
            Assert.Equal(1, first.SwapInterval);
            Assert.Equal(0, second.SwapInterval);
            for (var f = 0; f < 40; f++)
                engine.Render(Scene.Inputs(project, 1 + f / 60.0));
            Assert.True(engine.Present("op1")!.Value.Measured);
            Assert.False(engine.Present("op2")!.Value.Measured);
            Assert.Null(engine.NextVblankNs("op2", MonoClock.Instance.NowNs));

            // The pacer goes: the other paces now, and its clock is the measured one.
            engine.Detach("op1");
            Assert.Equal(1, second.SwapInterval);
            engine.Render(Scene.Inputs(project, 2));
            Assert.True(engine.Present("op2")!.Value.Measured);
        });
    }

    [Fact]
    public void ASurfacelessPlatformRefusesPresentersWithWords()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var platform = GpuPlatforms.Create(PlatformRequest.Default with { Surfaceless = true }, new GlDeviceOptions(true, false));
        Assert.Equal("gl-surfaceless", platform.Name);
        Assert.Equal(PresentPath.Offscreen, platform.Path);
        Assert.Empty(platform.Monitors());
        Assert.Equal(Core.Media.GpuApi.OpenGl, platform.Share.Api);
        Assert.Contains("egl-surfaceless", platform.Share.Describe, StringComparison.Ordinal);
        var refused = Assert.Throws<PlatformNotSupportedException>(() => platform.CreatePresenter(new OutputPresenterOptions("x", null, false, 64, 36)));
        Assert.Contains("no window system", refused.Message, StringComparison.Ordinal);
        Assert.Equal("", platform.Words);
    }

    [Fact]
    public void ASurfacelessContextGoingLeavesAnotherOnTheSameDisplayWorking()
    {
        if (!OperatingSystem.IsLinux())
            return;
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

    [Fact]
    public void SettingsWordsThePlatformDoesNotKnowAreSaidNotIgnored()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var platform = GpuPlatforms.Create(new PlatformRequest("warp", "gles9", Surfaceless: true), new GlDeviceOptions(true, false));
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
                using var platform = GpuPlatforms.Create(new PlatformRequest(Dialect: "es300"), new GlDeviceOptions(true, true));
                var c = platform.Context;
                Assert.Equal(GlDialect.Es300, c.Dialect);
                // On Linux ES is made on EGL (or on GLX, saying so); elsewhere the window system's own API makes it and has nothing to say.
                if (OperatingSystem.IsLinux())
                    Assert.True(c.Api == "egl" || platform.Words.StartsWith("ES through GLX", StringComparison.Ordinal), $"{c.Api}: {platform.Words}");
                else
                    Assert.Equal((OperatingSystem.IsWindows() ? "wgl" : "cocoa", ""), (c.Api, platform.Words));
                var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 64, 36)]);
                using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: c.Device.HasTimerQuery));
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
