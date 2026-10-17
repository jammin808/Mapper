using System.Runtime.CompilerServices;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Media.Hw;
using PlayMap.Tests;

namespace PlayMap.Media.Tests.Hw;

/// <summary>The run-time ladder a zero-copy clip decoder publishes through, driven with a fake renderer share: each trigger — an
/// import failure the renderer reported for this source, the renderer's device changing, a first frame it does not import, three
/// starved waits while playing, a hand-back to software — steps it down once with its words, to the rung the ladder names, and
/// it never steps again or back up. Pauses and seeks are not starved waits. The checks that change nothing allocate nothing.</summary>
public sealed class SurfaceLadderTests
{
    private const long Frame = 40_000_000;

    private static (SurfaceLadder Ladder, List<PathStep> Steps, FakeShare Share) Make(DecodePath on = DecodePath.HwDirect)
    {
        var share = new FakeShare(GpuApi.DmaBuf);
        var ladder = new SurfaceLadder("md_a", share, on, Frame);
        var steps = new List<PathStep>();
        ladder.Stepped += steps.Add;
        return (ladder, steps, share);
    }

    private static GpuSurface Nv12(ulong modifier)
    {
        var s = new GpuSurface { Api = GpuApi.DmaBuf, Format = GpuSurfaceFormat.Nv12, PlaneCount = 2 };
        s.Planes[0] = new DmaBufPlane(3, DrmFourcc.R8, 64, 64, 0, 64, modifier);
        s.Planes[1] = new DmaBufPlane(3, DrmFourcc.Gr88, 32, 32, 4096, 64, modifier);
        return s;
    }

    private static void AssertOneStep(List<PathStep> steps, SurfaceLadder ladder, DemotionTrigger trigger, DecodePath to, string words)
    {
        var step = Assert.Single(steps);
        Assert.Equal(("md_a", DecodePath.HwDirect, to, trigger), (step.Key, step.From, step.To, step.Trigger));
        Assert.Contains(words, step.Why, StringComparison.Ordinal);
        Assert.Equal((to, false, step.Why), (ladder.Path, ladder.ZeroCopy, ladder.Why));
        Assert.False(ladder.BeforeMap(), "a source that stepped down publishes no surface again");
        Assert.Single(steps);
    }

    [Fact]
    public void AnImportFailureTheRendererReportedStepsDownBeforeTheNextMap()
    {
        var (ladder, steps, share) = Make();
        Assert.True(ladder.BeforeMap());
        share.ReportImportFailure("md_b", "EGL_BAD_ALLOC");
        Assert.True(ladder.BeforeMap(), "another source's failure is not this one's");
        share.ReportImportFailure("md_a", "eglCreateImageKHR refused a R8 plane (EGL_BAD_MATCH)");
        Assert.False(ladder.BeforeMap());
        AssertOneStep(steps, ladder, DemotionTrigger.ImportFailed, DecodePath.HwCopyBack, "could not import a frame: eglCreateImageKHR refused a R8 plane (EGL_BAD_MATCH)");
    }

    [Fact]
    public void TheRenderersDeviceChangingStepsDown()
    {
        var (ladder, steps, _) = Make();
        ladder.ShareChanged("the render context was rebuilt");
        Assert.False(ladder.BeforeMap());
        AssertOneStep(steps, ladder, DemotionTrigger.DeviceLost, DecodePath.HwCopyBack, "the renderer's device changed: the render context was rebuilt");
    }

    [Fact]
    public void AFirstFrameTheRendererDoesNotImportStepsDownWithItsLayout()
    {
        var (ladder, steps, share) = Make();
        share.Imports = (_, modifier) => modifier == 0;
        Assert.False(ladder.CheckFirst(Nv12(0x0100000000000002)));
        AssertOneStep(steps, ladder, DemotionTrigger.NotImportable, DecodePath.HwCopyBack, "R8 with modifier 0x0100000000000002");
    }

    [Fact]
    public void OnlyTheFirstFrameIsChecked()
    {
        var (ladder, steps, share) = Make();
        Assert.True(ladder.CheckFirst(Nv12(0)));
        share.Imports = (_, _) => false;
        Assert.True(ladder.CheckFirst(Nv12(0)), "checked once; a later refusal comes back as an import failure");
        Assert.Empty(steps);
    }

    [Fact]
    public void ThreeStarvedWaitsWhilePlayingStepDown()
    {
        var (ladder, steps, _) = Make();
        long t = 1_000_000_000;
        for (var i = 0; i < 2; i++)
        {
            ladder.NoSlot(t, playing: true);
            t += 4 * Frame;
            Assert.True(ladder.GotSlot(t));
            t += 1_000_000_000;
        }
        ladder.NoSlot(t, playing: true);
        ladder.NoSlot(t + Frame, playing: true);   // the wait runs from its first refusal
        Assert.False(ladder.GotSlot(t + 4 * Frame));
        AssertOneStep(steps, ladder, DemotionTrigger.Starved, DecodePath.HwCopyBack, "waited over 3 frames for a free surface 3 times in 10 s");
    }

    [Fact]
    public void PausesAndSeeksAreNotStarvedWaits()
    {
        var (ladder, steps, _) = Make();
        long t = 0;
        for (var i = 0; i < 6; i++)
        {
            // Paused: the renderer may hold the pool as long as it likes.
            ladder.NoSlot(t, playing: true);
            ladder.NoSlot(t + Frame, playing: false);
            t += 60_000_000_000;
            Assert.True(ladder.GotSlot(t));
            // A seek drops the wait in progress.
            ladder.NoSlot(t, playing: true);
            ladder.ForgetWait();
            t += 10 * Frame;
            Assert.True(ladder.GotSlot(t));
        }
        Assert.Empty(steps);
        Assert.True(ladder.ZeroCopy);
    }

    [Fact]
    public void AHandBackFromCopyBackIsSoftware()
    {
        var (ladder, steps, _) = Make(DecodePath.HwCopyBack);
        Assert.False(ladder.ZeroCopy);
        ladder.HandedBack("vaapi");
        var step = Assert.Single(steps);
        Assert.Equal((DecodePath.HwCopyBack, DecodePath.Software, DemotionTrigger.HandedBack), (step.From, step.To, step.Trigger));
        Assert.Contains("handed this stream back to software: vaapi", step.Why, StringComparison.Ordinal);
        ladder.HandedBack("vaapi");
        Assert.Single(steps);
    }

    [Fact]
    public void ASourceNotOpenedForARendererIsNeverZeroCopy()
    {
        var ladder = new SurfaceLadder("md_a", null, DecodePath.HwDirect, Frame);
        Assert.False(ladder.ZeroCopy);
        Assert.False(ladder.BeforeMap());
    }

    [Fact]
    public void TheChecksOfAHealthySourceAllocateNothing()
    {
        var (ladder, steps, _) = Make();
        var surface = Nv12(0);
        Checks(ladder, surface, 100);
        Assert.Equal(0, Checks(ladder, surface, 5000));
        Assert.Empty(steps);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long Checks(SurfaceLadder ladder, GpuSurface surface, int rounds)
    {
        var before = Allocations.Start();
        long t = 0;
        for (var i = 0; i < rounds; i++)
        {
            ladder.BeforeMap();
            ladder.CheckFirst(surface);
            ladder.NoSlot(t, true);
            ladder.GotSlot(t + Frame);
            t += Frame;
        }
        return Allocations.Since(before);
    }
}
