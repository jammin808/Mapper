using PlayMap.Core.Time;

namespace PlayMap.Engine.Tests.Host;

/// <summary>Nodes on one moved clock stand for machines on one time server: they read one wall however far apart in real time
/// they start, an offset on a node's clock being another machine's boot and not its time, and the wall moves as the clock does.</summary>
public sealed class HostWallTests
{
    private const long Second = 1_000_000_000L;

    [Fact]
    public async Task TwoNodesOnOneMovedClockStartedApartInRealTimeReadOneWall()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var first = await HostRig.StartAsync("FOH-A", mono: clock);
        // Real time passes between the two starts; the moved clock does not.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        await using var second = await HostRig.StartAsync("FOH-B", mono: new OffsetClock(clock, 5 * 3600 * Second));

        Assert.Equal(first.Engine.Services.Wall.UnixMs, second.Engine.Services.Wall.UnixMs);
        clock.Advance(90 * Second);
        Assert.Equal(first.Engine.Services.Wall.UnixMs, second.Engine.Services.Wall.UnixMs);
    }
}
