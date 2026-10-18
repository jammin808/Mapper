using PlayMap.Core.Bus;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>Round 3's engine seams before their packages fill them: each null implementation does nothing and says why, so a node
/// composes every seam from the start.</summary>
public sealed class RoundThreeSeamTests
{
    [Fact]
    public void NoObserverObservesNothingAndSaysWhy()
    {
        IAirObserver observer = NoAirObserver.Instance;
        Assert.False(observer.Running);
        Assert.Equal("the air observer is not on this node yet: no output is observed", observer.Words);
        Assert.Null(observer.Observation("op_1"));
        var heard = 0;
        void Listen(AirObservation _)
        {
            heard += 1;
        }
        observer.Observed += Listen;
        observer.Observed -= Listen;
        Assert.Equal(0, heard);
        var reading = new AirObservation("op_1", "stale", Light.Red, 1_234_570, -1, 89_012_362_000, 0, "the counter has stood for 6 frames");
        Assert.Equal(reading, reading with { });
    }

    [Fact]
    public void NoGpuLifecycleHoldsNothingAndSaysWhy()
    {
        IGpuLifecycle lifecycle = NoGpuLifecycle.Instance;
        var incident = new GpuIncident(3, "DXGI_ERROR_DEVICE_HUNG", "582.16", "fx op_2 #3", "output op_2", "none", "", 1_760_600_000_000);
        lifecycle.Releasing(3, incident);
        Assert.Equal("DXGI_ERROR_DEVICE_HUNG", incident.Reason);
        Assert.Throws<ArgumentNullException>(() => lifecycle.Rebuilt(4, null!));
        Assert.Equal("nothing on this node is made again with the render device yet", NoGpuLifecycle.Words);
    }

    [Fact]
    public void NoPreviewShareRefusesEveryShareWithItsWordsAndReleasesNothing()
    {
        IPreviewShare share = NoPreviewShare.Instance;
        Assert.Null(share.Share("v_stage", 1280, 720, 4242, SessionInfo.Desk("t"), out var words));
        Assert.Equal("shared previews are not on this node yet: every view goes through its ring", words);
        Assert.Equal(words, share.Words);
        Assert.False(share.Release("v_stage"));
    }

    [Fact]
    public async Task NothingToArmAnswersAtOnceSayingSo()
    {
        IShowArmHook hook = NothingToArm.Instance;
        Assert.Equal("none", hook.Name);
        Assert.Equal(TimeSpan.Zero, hook.Deadline);
        Assert.Equal("nothing is prepared at arm on this node yet", await hook.ArmingAsync(CancellationToken.None));
        Assert.Equal("", await hook.DisarmingAsync(CancellationToken.None));
    }
}
