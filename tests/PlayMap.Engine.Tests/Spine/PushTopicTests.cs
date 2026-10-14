using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>A package's push topic reaches a wire session: SUBSCRIBE opens it through the engine's topic table, its in-order and
/// latest-wins lines arrive, a second SUBSCRIBE replaces the first and OFF ends it; an unknown topic is refused with the topics this
/// node serves.</summary>
public sealed class PushTopicTests
{
    private sealed class Subscription(Action onEnd) : IDisposable
    {
        public void Dispose() => onEnd();
    }

    [Fact]
    public async Task AnUnknownTopicIsRefusedWithTheTopicList()
    {
        await using var rig = await HostRig.StartAsync();
        rig.Engine.Routes.Topic("FAKE", (_, _, _) => PushOpen.Refuse("never opened here"));
        await using var c = await rig.WireAsync();

        // The session's own topics first, then every topic the merged packages serve (FAKE among them).
        var refused = await c.AskAsync("@u SUBSCRIBE EVERYTHING");
        Assert.StartsWith("ERR @u EVERYTHING is not a topic (STATE, CONTEXT, PROJECT, OFF, ", refused, StringComparison.Ordinal);
        Assert.EndsWith(")", refused, StringComparison.Ordinal);
        Assert.Contains(", FAKE", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFakeTopicsLinesReachAWireSessionUntilItIsTurnedOff()
    {
        await using var rig = await HostRig.StartAsync();
        IPushSink? sink = null;
        var ended = 0;
        IReadOnlyList<string>? asked = null;
        SessionInfo? who = null;
        rig.Engine.Routes.Topic("FAKE", (args, session, s) =>
        {
            if (args.Count > 0 && args[0] == "NO")
                return PushOpen.Refuse("FAKE NO: refused by its owner");
            asked = args;
            who = session;
            sink = s;
            s.InOrder("FAKE hello");
            return PushOpen.Open(new Subscription(() => Interlocked.Increment(ref ended)));
        });
        await using var c = await rig.WireAsync();

        Assert.Equal("ERR @n FAKE NO: refused by its owner", await c.AskAsync("@n SUBSCRIBE FAKE NO"));
        Assert.Equal("OK @s subscribed fake", await c.AskAsync("@s SUBSCRIBE FAKE A B"));
        Assert.Equal(["A", "B"], asked);
        Assert.Equal("wire", who!.Transport);
        Assert.Equal("FAKE hello", await c.ReadAsync());

        Assert.True(sink!.InOrder("FAKE one"));
        Assert.True(sink.InOrder("FAKE two"));
        sink.Latest("k", "FAKE latest");
        Assert.Equal("FAKE one", await c.ReadAsync());
        Assert.Equal("FAKE two", await c.ReadAsync());
        Assert.Equal("FAKE latest", await c.ReadAsync());

        // A second SUBSCRIBE of the topic replaces the first; OFF ends it.
        Assert.Equal("OK @r subscribed fake", await c.AskAsync("@r SUBSCRIBE FAKE"));
        Assert.Equal("FAKE hello", await c.ReadAsync());
        Assert.Equal(1, Volatile.Read(ref ended));
        Assert.Equal("OK @o pushes off", await c.AskAsync("@o SUBSCRIBE OFF"));
        Assert.Equal(2, Volatile.Read(ref ended));
    }
}
