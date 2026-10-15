using System.Text.Json;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net;

namespace PlayMap.Engine.Tests.Tc;

/// <summary>A wire listener hears a rolling timecode with nothing playing: while the code rolls the node counts as animating for its
/// transports (its own "a playlist plays" stays false), so STATE reaches the listener at least once a second, each push saying the
/// code as it is then; a while after the code stops the node is still again.</summary>
public sealed class TimecodePushTests
{
    private static bool IsState(string line) => line.StartsWith("STATE ", StringComparison.Ordinal);

    private static string Text(string push)
    {
        using var d = JsonDocument.Parse(push["STATE ".Length..]);
        return d.RootElement.GetProperty("timecode").GetProperty("sources")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task AStateListenerHearsTheRollingCodeEverySecondWhileNothingPlays()
    {
        await using var rig = await HostRig.StartAsync();
        var activity = (INodeActivity)rig.Engine;
        await rig.OkAsync("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        await rig.OkAsync("TIMECODE INTERNAL Rehearsal PLAY");

        await using var c = await rig.WireAsync();
        Assert.Equal("OK @s subscribed state", await c.AskAsync("@s SUBSCRIBE STATE"));
        var first = Text(await c.PushAsync(IsState));
        // Nothing but the code moves: the pushes that follow come from the once-a-second look, each with the code then.
        var second = Text(await c.PushAsync(l => IsState(l) && Text(l) != first, 5000));
        var third = Text(await c.PushAsync(l => IsState(l) && Text(l) != second && Text(l) != first, 5000));
        Assert.True(string.CompareOrdinal(first, second) < 0 && string.CompareOrdinal(second, third) < 0, $"{first}, {second}, {third}");
        Assert.True(activity.IsAnimating);
        Assert.False(rig.Engine.IsAnimating);

        await rig.OkAsync("TIMECODE INTERNAL Rehearsal STOP");
        Assert.True(await Eventually.TrueAsync(() => !activity.IsAnimating), "the stopped code still counted as moving");
    }
}
