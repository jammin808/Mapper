using System.Collections.Immutable;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Machine;

/// <summary>A deadline thread's timer: the platform in use makes it for waits on the machine's monotonic clock, which its timers wait
/// on; a wait on any other clock (a test's hand-moved one) is the runtime's own on that clock, and the platform makes none. ThreadClaims
/// is one static seam for the process, so this runs alone and puts back the platform it found.</summary>
[Collection(ThreadClaimsCollection.Name)]
public sealed class ThreadTimerTests
{
    /// <summary>A platform that claims nothing and counts the timers it makes.</summary>
    private sealed class Timers : IMachinePlatform
    {
        public List<IDeadlineTimer> Made { get; } = [];

        public string Name => "timers";

        public string Words => "";

        public ImmutableArray<Claim> ClaimProcess(MachineSettings settings) => [];

        public IThreadClaim ClaimThread(ThreadRole role) => new UnclaimedThread(role, "counted");

        public IDeadlineTimer CreateTimer()
        {
            var timer = new RuntimeDeadlineTimer();
            Made.Add(timer);
            return timer;
        }

        public IDisposable KeepAwake() => new Held();

        public NodeIdentity Identity() => NodeIdentity.Unread;

        public CapabilitySnapshot Capabilities() => CapabilitySnapshot.Empty;

        public ImmutableArray<MachineFact> Facts() => [];

        public void Dispose()
        {
            // Each timer is its thread's to dispose.
        }

        private sealed class Held : IDisposable
        {
            public void Dispose()
            {
                // Nothing is held.
            }
        }
    }

    [Fact]
    public void AWaitOnTheMachinesClockIsThePlatformsTimerAndAWaitOnAnyOtherClockTheRuntimesOnThatClock()
    {
        var was = ThreadClaims.Platform;
        using var platform = new Timers();
        try
        {
            ThreadClaims.Use(platform);
            using (var machine = ThreadClaims.CreateTimer(MonoClock.Instance))
                Assert.Same(Assert.Single(platform.Made), machine);

            var hand = new ManualClock(1_000_000_000);
            using var other = ThreadClaims.CreateTimer(hand);
            Assert.IsType<RuntimeDeadlineTimer>(other);
            Assert.Single(platform.Made);
            // On the hand-moved clock: the instant it is at answers at once, one ahead waits for the cancel.
            Assert.True(other.WaitUntil(1_000_000_000));
            using var cancel = new ManualResetEvent(true);
            Assert.False(other.WaitUntil(1_000_000_001, cancel));
        }
        finally
        {
            if (was is not null)
                ThreadClaims.Use(was);
        }
    }
}
