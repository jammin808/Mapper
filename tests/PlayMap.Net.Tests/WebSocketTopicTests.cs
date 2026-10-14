using System.Net.WebSockets;
using System.Text.Json;
using PlayMap.Core.Wire;
using PlayMap.Net.Http;

namespace PlayMap.Net.Tests;

/// <summary>A package's push topic on a screen's WebSocket: SUBSCRIBE answers its receipt first, then what the topic said while it
/// opened and everything after as push frames; the topic's owner refuses with its words; SUBSCRIBE OFF ends the topic.</summary>
public sealed class WebSocketTopicTests
{
    /// <summary>The node with one push topic, FAKE, whose sink and end the test holds.</summary>
    private sealed class TopicNode(FakeEndpoint inner) : IWireEndpoint, IPushTopics
    {
        private sealed class Subscription(TopicNode node) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref node._ended);
        }

        private int _ended;

        public IPushSink? Sink { get; private set; }

        public SessionInfo? Who { get; private set; }

        public int Ended => Volatile.Read(ref _ended);

        public IReadOnlyCollection<string> Topics => ["FAKE"];

        public bool TryOpen(string topic, IReadOnlyList<string> args, SessionInfo session, IPushSink sink, out IDisposable? subscription, out string words)
        {
            subscription = null;
            if (topic != "FAKE")
            {
                words = $"{topic} is not a topic (STATE, CONTEXT, PROJECT, OFF, FAKE)";
                return false;
            }
            if (args.Count > 0 && args[0] == "NO")
            {
                words = "FAKE NO: refused by its owner";
                return false;
            }
            Who = session;
            Sink = sink;
            sink.InOrder("FAKE hello");
            subscription = new Subscription(this);
            words = "";
            return true;
        }

        public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct) => inner.HandleAsync(line, session, ct);

        public (string Json, long Revision) State() => inner.State();

        public string Context() => inner.Context();

        public (string Json, long Revision) ProjectJson() => inner.ProjectJson();

        public event Action<long>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public event Action<long>? ProjectChanged
        {
            add => inner.ProjectChanged += value;
            remove => inner.ProjectChanged -= value;
        }
    }

    private static async Task<JsonElement> NextAsync(ClientWebSocket ws, string type)
    {
        var text = await HttpHostTests.ReceiveUntilAsync(ws, m => m.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ATopicsLinesFollowTheReceiptAsPushFramesUntilItIsTurnedOff()
    {
        var node = new TopicNode(new FakeEndpoint());
        var host = new HttpHost(TestSettings.Make(trustLoopback: true), node, TestSettings.Wall(),
            new HttpHostOptions { Port = 0, BeatEvery = TimeSpan.FromSeconds(30) });
        await using (host)
        {
            await host.StartAsync();
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + host.Port + "/ws"), CancellationToken.None);
            await NextAsync(ws, "state");

            await HttpHostTests.SendAsync(ws, "{\"cmd\":\"SUBSCRIBE FAKE NO\",\"id\":\"n1\"}");
            var refused = await NextAsync(ws, "receipt");
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Equal("FAKE NO: refused by its owner", refused.GetProperty("error").GetString());

            await HttpHostTests.SendAsync(ws, "{\"cmd\":\"SUBSCRIBE FAKE\",\"id\":\"s1\"}");
            // The receipt first, then what the topic said while it opened.
            using var firstDoc = JsonDocument.Parse(await HttpHostTests.ReceiveAsync(ws));
            var first = firstDoc.RootElement;
            Assert.Equal("receipt", first.GetProperty("type").GetString());
            Assert.Equal("s1", first.GetProperty("id").GetString());
            Assert.True(first.GetProperty("ok").GetBoolean());
            Assert.Equal("subscribed fake", first.GetProperty("effect").GetString());
            var hello = await NextAsync(ws, "push");
            Assert.Equal("FAKE", hello.GetProperty("topic").GetString());
            Assert.Equal("FAKE hello", hello.GetProperty("line").GetString());
            Assert.Equal("ws", node.Who!.Transport);

            Assert.True(node.Sink!.InOrder("FAKE one"));
            Assert.Equal("FAKE one", (await NextAsync(ws, "push")).GetProperty("line").GetString());

            await HttpHostTests.SendAsync(ws, "{\"cmd\":\"SUBSCRIBE OFF\",\"id\":\"o1\"}");
            var off = await NextAsync(ws, "receipt");
            Assert.True(off.GetProperty("ok").GetBoolean());
            Assert.Equal(1, node.Ended);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
    }
}
