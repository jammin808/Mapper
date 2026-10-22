using System.Runtime.Versioning;
using PlayMap.Tests;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>Every deadline thread (the render thread's pacer, the LED sender, each audio feeder) records how late it woke in the
/// platform's one window after each wait, and the health sampler reads it: the window's lock is made with its wait event, so a first
/// wait behind another thread's record allocates nothing, at whatever frame it comes.</summary>
[SupportedOSPlatform("windows")]
public sealed class LatenessTests
{
    [WindowsFact]
    public void TheLatenessWindowsLockIsMadeWithItsWaitEvent()
    {
        var probe = Assert.Single(LockWaitProbe.FirstWaits(new Lateness()));
        Assert.EndsWith(" 0 B", probe, StringComparison.Ordinal);
    }
}
