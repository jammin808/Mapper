using PlayMap.Core.Wire;

namespace PlayMap.Net;

/// <summary>A session's subscriptions to a package's push topics (SUBSCRIBE INDEX, EVENTS …): one per topic, a second SUBSCRIBE of a
/// topic replacing the first, all ended by SUBSCRIBE OFF or the session's close. Used by one session's reader at a time.</summary>
internal sealed class TopicSubscriptions : IDisposable
{
    /// <summary>In-order lines a topic may leave unwritten before it is closed with an overflow line.</summary>
    public const int InOrderBound = 256;

    private readonly Dictionary<string, IDisposable?> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HeldSink> _held = [];

    /// <summary>The session's own topics; every other word is asked of the endpoint's <see cref="IPushTopics"/>.</summary>
    public static bool IsOwn(string topic) => topic.ToUpperInvariant() is "STATE" or "CONTEXT" or "PROJECT" or "OFF";

    /// <summary>Opens a topic through the endpoint: true with the topic's canonical name, false with the words of the refusal (an
    /// endpoint with no topics refuses every one).</summary>
    public bool TryOpen(IWireEndpoint endpoint, string topic, IReadOnlyList<string> args, SessionInfo session,
        Func<string, IPushSink> sinkFor, out string words)
    {
        if (endpoint is not IPushTopics topics)
        {
            words = Commands.WireWords.Subscribe;
            return false;
        }
        var name = topics.Topics.FirstOrDefault(t => t.Equals(topic, StringComparison.OrdinalIgnoreCase)) ?? topic.ToUpperInvariant();
        // What the topic says while it opens waits for the session's reply (OK first, then the topic's lines): Release.
        var held = new HeldSink(sinkFor(name));
        if (!topics.TryOpen(name, args, session, held, out var subscription, out words))
        {
            subscription?.Dispose();
            return false;
        }
        _held.Add(held);
        if (_open.Remove(name, out var previous))
            previous?.Dispose();
        _open[name] = subscription;
        words = name;
        return true;
    }

    /// <summary>After the session's reply to SUBSCRIBE: the lines its topics said while they opened, then everything they say.</summary>
    public void Release()
    {
        foreach (var h in _held)
            h.Release();
        _held.Clear();
    }

    public void Dispose()
    {
        foreach (var s in _open.Values)
            s?.Dispose();
        _open.Clear();
        Release();
    }
}

/// <summary>A topic's sink that holds what it is told until it is released, then passes everything through in the order it was said.</summary>
internal sealed class HeldSink(IPushSink inner) : IPushSink
{
    private readonly Lock _gate = new();
    private List<(string? Key, string Line)>? _held = [];

    public void Latest(string key, string line)
    {
        lock (_gate)
        {
            if (_held is null)
            {
                inner.Latest(key, line);
                return;
            }
            _held.RemoveAll(h => string.Equals(h.Key, key, StringComparison.Ordinal));
            _held.Add((key, line));
        }
    }

    public bool InOrder(string line)
    {
        lock (_gate)
        {
            if (_held is null)
                return inner.InOrder(line);
            _held.Add((null, line));
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_held is null)
                return;
            foreach (var (key, line) in _held)
            {
                if (key is null)
                    inner.InOrder(line);
                else
                    inner.Latest(key, line);
            }
            _held = null;
        }
    }
}

/// <summary>A wire session's sink for one topic: latest-wins lines through the writer's keyed slots, in-order lines bounded; the
/// first line past the bound closes the topic with "&lt;TOPIC&gt; overflow" (the session's own replies are never cut by it).</summary>
internal sealed class WireTopicSink(LineWriter writer, string topic) : IPushSink
{
    private volatile bool _overflowed;

    public void Latest(string key, string line) => writer.PushKeyed(topic + "\u0000" + key, line);

    public bool InOrder(string line)
    {
        if (_overflowed)
            return false;
        if (writer.TrySay(line, TopicSubscriptions.InOrderBound))
            return true;
        _overflowed = true;
        writer.Say($"{topic} overflow — this session read too slowly; SUBSCRIBE {topic} again");
        return false;
    }
}

/// <summary>A WebSocket session's pushes from its topics, drained by the session's own pump: in-order lines bounded per topic, and
/// latest-wins lines by key. Each becomes a frame {"type":"push","topic":…,"line":…}.</summary>
internal sealed class PushQueue : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<(string Topic, string Line)> _inOrder = new();
    private readonly Dictionary<string, (string Topic, string Line)> _latest = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0, 1);

    public IPushSink SinkFor(string topic) => new Sink(this, topic);

    /// <summary>Waits until something is queued.</summary>
    public Task WaitAsync(CancellationToken ct) => _signal.WaitAsync(ct);

    /// <summary>Everything queued now, in order, as frames.</summary>
    public List<byte[]> Drain()
    {
        var frames = new List<byte[]>();
        lock (_gate)
        {
            while (_inOrder.TryDequeue(out var item))
                frames.Add(Frame(item.Topic, item.Line));
            foreach (var item in _latest.Values)
                frames.Add(Frame(item.Topic, item.Line));
            _latest.Clear();
        }
        return frames;
    }

    public static byte[] Frame(string topic, string line) => JsonText.WriteBytes(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "push");
        w.WriteString("topic", topic);
        w.WriteString("line", line);
        w.WriteEndObject();
    });

    public void Dispose() => _signal.Dispose();

    private void Wake()
    {
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    private sealed class Sink(PushQueue queue, string topic) : IPushSink
    {
        private volatile bool _overflowed;

        public void Latest(string key, string line)
        {
            lock (queue._gate)
            {
                queue._latest[topic + "\u0000" + key] = (topic, line);
                queue.Wake();
            }
        }

        public bool InOrder(string line)
        {
            if (_overflowed)
                return false;
            lock (queue._gate)
            {
                var queued = queue._inOrder.Count(i => ReferenceEquals(i.Topic, topic));
                if (queued >= TopicSubscriptions.InOrderBound)
                {
                    _overflowed = true;
                    queue._inOrder.Enqueue((topic, $"{topic} overflow — this session read too slowly; SUBSCRIBE {topic} again"));
                    queue.Wake();
                    return false;
                }
                queue._inOrder.Enqueue((topic, line));
                queue.Wake();
                return true;
            }
        }
    }
}
