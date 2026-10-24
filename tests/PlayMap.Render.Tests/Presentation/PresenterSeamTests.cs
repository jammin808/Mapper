using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Core.Presentation;
using PlayMap.Core.Time;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>The presenter seam: the renderer presents through <see cref="IOutputPresenter"/> whatever stands behind it — the GL window
/// (every existing window test runs through it unchanged), or a presenter of the test's own whose presents return on a scripted
/// 60 Hz vblank — and each output's clock and ledger read what the presenter says.</summary>
[Collection("gpu")]
public sealed class PresenterSeamTests(BackendFixture backends)
{
    /// <summary>A presenter that keeps the pictures it was given and waits, as a vsync'd swap does, for the next vblank of a 60 Hz
    /// grid on the monotonic clock (the one after it when asked to be late), and says it returned just after that vblank — the
    /// instant it scripted, whenever its thread got the CPU back (how the fit copes with late stamps is VblankClockTests').</summary>
    private sealed class ScriptedPresenter : IOutputPresenter
    {
        private const double Period = 1e9 / 60;
        private readonly long _start = MonoClock.Instance.NowNs;
        private GpuTexture? _recorded;

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

        public void Record(ICommandList list, GpuTexture picture) => _recorded = picture;

        public void RecordBlack(ICommandList list) => _recorded = null;

        public PresentResult Present() => _recorded is { } picture ? Show(picture) : PresentResult.Done(LastSwapNs);

        private PresentResult Show(GpuTexture picture)
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
        backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(GpuTiming: false));
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
            Assert.Empty(leg.TakeErrors());
        });
    }

    [Fact]
    public void TheFirstPresenterPacesAndTheOthersPresentAtOnceUnmeasured()
    {
        var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 64, 36), Scene.SourceOutput("op2", SourceRef.Solid("#996633"), 64, 36)]);
        backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(GpuTiming: false));
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
}
