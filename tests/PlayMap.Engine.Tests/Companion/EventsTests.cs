using System.Text.Json;
using PlayMap.Engine.Companion;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>EVENTS on a manual clock: events keep the order they were raised in, EVENTS SINCE replays what came after a seq, the ring
/// holds the last 256 and says how many it no longer has, a subscriber hears each as EVENT in order, one that reads too slowly or is
/// sent more than 64 in a second is closed with an overflow line that says where to replay from, and the node's faults raise
/// fault.raised and fault.cleared.</summary>
public sealed class EventsTests
{
    private static List<JsonElement> EventsOf(string reply) => [.. CompanionRig.Json(reply).GetProperty("events").EnumerateArray()];

    [Fact]
    public async Task EventsKeepTheirOrderAndEventsSinceReplays()
    {
        await using var rig = new CompanionRig();
        var t0 = rig.Engine.LeaderNowNs;
        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            rig.Engine.RaiseEvent("test.step", t0, w => w.WriteNumber("n", n));
            rig.Clock.Advance(1_000_000);
        }

        var all = CompanionRig.Json(await rig.Ok("EVENTS"));
        var events = all.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(5, events.Count);
        Assert.Equal([1, 2, 3, 4, 5], events.Select(e => e.GetProperty("n").GetInt32()));
        var seqs = events.Select(e => e.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs[^1], all.GetProperty("seq").GetInt64());
        Assert.All(events, e => Assert.Equal("test.step", e.GetProperty("kind").GetString()));
        Assert.Equal(seqs[^1], rig.State().GetProperty("events").GetProperty("seq").GetInt64());

        var since = EventsOf(await rig.Ok("EVENTS SINCE " + seqs[2]));
        Assert.Equal([4, 5], since.Select(e => e.GetProperty("n").GetInt32()));
        Assert.Empty(EventsOf(await rig.Ok("EVENTS SINCE " + seqs[^1])));
        Assert.False((await rig.Run("EVENTS SINCE soon")).Ok);
    }

    [Fact]
    public async Task AnEventIsTimedWhenItHappenedNotWhenTheRingHeardIt()
    {
        await using var rig = new CompanionRig();
        var at = rig.Engine.LeaderNowNs;
        var wallAt = rig.Engine.Services.Wall.UnixMs;
        rig.Clock.Advance(250_000_000);   // the event is told 250 ms after it happened
        rig.Engine.RaiseEvent("item.end", at, w => w.WriteString("item", "it00000001"));
        var e = Assert.Single(EventsOf(await rig.Ok("EVENTS")));
        Assert.Equal(at, e.GetProperty("leaderNs").GetInt64());
        Assert.Equal(wallAt, e.GetProperty("at").GetInt64());
        Assert.Equal("it00000001", e.GetProperty("item").GetString());
    }

    [Fact]
    public async Task TheRingHoldsTheLast256AndSaysWhatItMissed()
    {
        await using var rig = new CompanionRig();
        for (var i = 0; i < EventRing.Capacity + 44; i++)
            rig.Engine.RaiseEvent("test.many", rig.Engine.LeaderNowNs);
        var reply = CompanionRig.Json(await rig.Ok("EVENTS SINCE 0"));
        Assert.Equal(EventRing.Capacity, reply.GetProperty("events").GetArrayLength());
        Assert.Equal(44, reply.GetProperty("missed").GetInt64());
    }

    [Fact]
    public async Task ASubscriberHearsEveryEventInOrder()
    {
        await using var rig = new CompanionRig();
        var sink = new RecordingSink();
        using var sub = rig.Open("EVENTS", sink);
        rig.Engine.RaiseEvent("test.a", rig.Engine.LeaderNowNs);
        rig.Engine.RaiseEvent("test.b", rig.Engine.LeaderNowNs);
        rig.Engine.RaiseEvent("test.c", rig.Engine.LeaderNowNs);
        var kinds = sink.Lines.Where(l => l.StartsWith("EVENT ", StringComparison.Ordinal))
            .Select(l => CompanionRig.Json(l[6..]).GetProperty("kind").GetString()).ToList();
        Assert.Equal(["test.a", "test.b", "test.c"], kinds);
        Assert.False(((PlayMap.Net.IPushTopics)rig.Engine).TryOpen("EVENTS", ["NOW"], CompanionRig.Paired, new RecordingSink(), out _, out var words));
        Assert.Contains("EVENTS SINCE", words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThan64InASecondClosesTheSubscriberWithWhereToReplayFrom()
    {
        await using var rig = new CompanionRig();
        var sink = new RecordingSink();
        using var sub = rig.Open("EVENTS", sink);
        for (var i = 0; i < EventRing.MaxPerSecond; i++)
            rig.Engine.RaiseEvent("test.burst", rig.Engine.LeaderNowNs);
        var last = rig.Engine.Companion.Events.LastSeq;
        rig.Engine.RaiseEvent("test.burst", rig.Engine.LeaderNowNs);
        var overflow = Assert.Single(sink.Lines, l => l.StartsWith("EVENTS overflow", StringComparison.Ordinal));
        Assert.Contains("EVENTS SINCE " + last, overflow, StringComparison.Ordinal);
        Assert.Equal(EventRing.MaxPerSecond, sink.Lines.Count(l => l.StartsWith("EVENT ", StringComparison.Ordinal)));

        // Closed: a second later nothing more reaches it; a new subscription hears again.
        rig.Clock.Advance(2_000_000_000);
        rig.Engine.RaiseEvent("test.after", rig.Engine.LeaderNowNs);
        Assert.DoesNotContain(sink.Lines, l => l.Contains("test.after", StringComparison.Ordinal));
        var again = new RecordingSink();
        using var sub2 = rig.Open("EVENTS", again);
        rig.Engine.RaiseEvent("test.after", rig.Engine.LeaderNowNs);
        Assert.Single(again.Lines);
    }

    [Fact]
    public async Task ASessionThatReadsTooSlowlyIsClosed()
    {
        await using var rig = new CompanionRig();
        var sink = new RecordingSink { Bound = 3 };
        using var sub = rig.Open("EVENTS", sink);
        for (var i = 0; i < 6; i++)
        {
            rig.Engine.RaiseEvent("test.slow", rig.Engine.LeaderNowNs);
            rig.Clock.Advance(100_000_000);
        }
        // The fourth line was refused by the session's bound: the subscription ended there and was offered nothing after it.
        Assert.Equal(3, sink.Lines.Count);
        Assert.Equal(4, sink.InOrderCount);
    }

    [Fact]
    public async Task TheNodesFaultsRaiseAndClearEvents()
    {
        var faults = new Faults();
        await using var rig = new CompanionRig(s => s with { Faults = faults });
        faults.Now = [new NodeFault("mdns", "the network announcement could not open")];
        rig.Engine.FactsChanged();
        faults.Now = [];
        rig.Engine.FactsChanged();
        var events = EventsOf(await rig.Ok("EVENTS"));
        var raised = events.Single(e => e.GetProperty("kind").GetString() == "fault.raised");
        Assert.Equal("mdns", raised.GetProperty("part").GetString());
        var cleared = events.Single(e => e.GetProperty("kind").GetString() == "fault.cleared");
        Assert.True(cleared.GetProperty("seq").GetInt64() > raised.GetProperty("seq").GetInt64());
    }

    private sealed class Faults : INodeFaults
    {
        public volatile IReadOnlyList<NodeFault> Now = [];

        IReadOnlyList<NodeFault> INodeFaults.Faults() => Now;
    }
}
