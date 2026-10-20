using PlayMap.Core.Machine;
using PlayMap.Core.Time;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Media.Audio;
using PlayMap.Net.Led;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>Every deadline thread of a node waits on the node's platform's timer: the LED sender's and the audio feeder's, each made on
/// its own thread as it starts, and the headless pacer's at its first wait, all for instants on the machine's monotonic clock; the
/// composed platform reads each one's kind and counts their waits' lateness. ThreadClaims is one static seam for the process, so this
/// runs alone and puts back the platform it found.</summary>
[Collection(SeamHostCollection.Name)]
public sealed class DeadlineThreadTests
{
    [Fact]
    public async Task TheLedSendersTheAudioFeedersAndThePacersTimersAreThePlatformsMadeOnTheirOwnThreads()
    {
        var was = ThreadClaims.Platform;
        await using var rig = new MachineRig(MachineRig.WithoutDisplay());
        try
        {
            ThreadClaims.Use(rig.Recorded);
            var led = new LedSender(new LedSenderOptions());
            try
            {
                led.Start();
                using var audio = AudioOut.OpenNull("tests have no sound card");
                Assert.True(await Eventually.TrueAsync(() => rig.Platform.TimersMadeOn.Count == 2), string.Join(", ", rig.Platform.TimersMadeOn));
                Assert.Equal<string>(["PlayMap led-send", "audio out"], rig.Platform.TimersMadeOn.Order(StringComparer.Ordinal));
                // TIMER HIGH by default: the composed platform reads back the kind its platform made.
                Assert.Equal("high", rig.Recorded.TimerMode);
                // The feeder's 5 ms waits reach their instants, and the composed platform counts each one's lateness.
                Assert.True(await Eventually.TrueAsync(() => rig.Recorded.Lateness.Waits >= 3), $"{rig.Recorded.Lateness.Waits} waits counted");

                using var pacer = new HeadlessPacing();
                using var wake = new AutoResetEvent(false);
                pacer.WaitUntil(MonoClock.Instance.NowNs - 1, wake);
                Assert.Equal(3, rig.Platform.TimersMadeOn.Count);
            }
            finally
            {
                await led.DisposeAsync();
            }
        }
        finally
        {
            if (was is not null)
                ThreadClaims.Use(was);
        }
    }
}
