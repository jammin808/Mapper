using PlayMap.Core.Machine;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;
using PlayMap.Engine.Led;
using PlayMap.Engine.Machine;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Media.Audio;
using PlayMap.Net.Led;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>Every deadline thread of a node waits on the node's platform's timer: on a node composed as the exe composes it (its LED
/// part and its air's sound, taken from the exe's composition by reference, beside the machine part over a fake Windows platform, on
/// the machine's monotonic clock) the LED sender's and the audio feeder's timers are made through the platform on their own threads as
/// they start, and STATE reads their kind and counts their waits; the units themselves, pointed at a composed platform, make theirs
/// there too, and the headless pacer its at its first wait. ThreadClaims is one static seam for the process, so this runs alone and
/// puts back the platform it found.</summary>
[Collection(SeamHostCollection.Name)]
public sealed class DeadlineThreadTests
{
    /// <summary>The exe's LED part and air's sound beside the machine part over <paramref name="platform"/>.</summary>
    private static EngineParts Parts(FakePlatform platform)
    {
        var standard = EngineParts.Standard().Extensions;
        return EngineParts.Bare with
        {
            Extensions =
            [
                _ => new MachineParts(new MachineNode(MachineSettings.Default, _ => { }), platform),
                Assert.Single(standard, f => f == LedExtension.Factory),
                Assert.Single(standard, f => f == EngineParts.AirSound),
            ],
        };
    }

    [Fact]
    public async Task AComposedNodesLedSenderAndAudioFeederWaitOnThePlatformsTimersMadeOnTheirOwnThreads()
    {
        var platform = new FakePlatform();
        // The rig's node runs on the machine's monotonic clock, as the exe's does; its air's sound is on (the default device, which a
        // machine without one opens as the null output, whose feeder still runs).
        await using var node = await HostRig.StartAsync("Deadlines", parts: Parts(platform));
        Assert.True(await Eventually.TrueAsync(() => platform.TimersMadeOn.Count >= 2), string.Join(", ", platform.TimersMadeOn));
        // The two threads start side by side, so their order is not the test's.
        Assert.Equal<string>(["PlayMap led-send", "audio out"], platform.TimersMadeOn.Order(StringComparer.Ordinal));

        // TIMER HIGH by default: STATE reads back the kind the platform made, through the composed platform, and counts the waits
        // (read with the engine told the facts moved, so the counters are fresh).
        static System.Text.Json.JsonElement Timer(HostRig node)
        {
            node.Engine.FactsChanged();
            return node.State().GetProperty("machine").GetProperty("platform").GetProperty("timer");
        }
        Assert.True(await Eventually.TrueAsync(() => Timer(node).GetProperty("mode").GetString() == "high"), Timer(node).ToString());
        var waits = Timer(node).GetProperty("waits").GetInt32();
        Assert.True(await Eventually.TrueAsync(() => Timer(node).GetProperty("waits").GetInt32() > waits), $"{waits} waits counted, no more");
    }

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
                // TIMER HIGH by default: the composed platform reads back the kind its platform made, once it has made it.
                Assert.True(await Eventually.TrueAsync(() => rig.Recorded.TimerMode == "high"), rig.Recorded.TimerMode);
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
