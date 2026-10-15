using System.Globalization;
using System.Text;
using PlayMap.Net;

namespace PlayMap.Engine.Companion;

/// <summary>The node's recent show events for automations: the last <see cref="Capacity"/> in the order they were raised, each as one
/// compact JSON object <c>{"seq","at","leaderNs","kind",…fields}</c>, replayed by <c>EVENTS SINCE</c> and pushed in order to every
/// <c>SUBSCRIBE EVENTS</c> session as <c>EVENT {…}</c>. A subscriber that falls behind (its session's bound, or more than
/// <see cref="MaxPerSecond"/> in a second) is closed with an overflow line that says where to replay from: a gap is never silent.
/// Any thread.</summary>
public sealed class EventRing
{
    public const int Capacity = 256;
    public const int MaxPerSecond = 64;

    private readonly Func<long> _nowMs;
    private readonly Lock _gate = new();
    private readonly (long Seq, string Json)[] _ring = new (long, string)[Capacity];
    private readonly List<Subscriber> _subscribers = [];
    private int _count;
    private int _next;
    private long _last;

    /// <param name="nowMs">The wall clock in ms, for the per-second bound (a ManualClock's in tests).</param>
    public EventRing(Func<long> nowMs) => _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));

    /// <summary>The newest event's seq (0 before the first).</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
                return _last;
        }
    }

    /// <summary>Raised after an event was taken (STATE.events moves).</summary>
    public event Action? Changed;

    /// <summary>Takes one event. <paramref name="atUnixMs"/> is when it happened (an item's end at its scheduled instant).</summary>
    public void Add(long seq, string kind, long atUnixMs, long atLeaderNs, string fieldsJson)
    {
        var json = Compose(seq, kind, atUnixMs, atLeaderNs, fieldsJson);
        lock (_gate)
        {
            _ring[_next] = (seq, json);
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
            _last = Math.Max(_last, seq);
            var now = _nowMs();
            for (var i = _subscribers.Count - 1; i >= 0; i--)
                if (!_subscribers[i].Offer(seq, json, now))
                    _subscribers.RemoveAt(i);
        }
        Changed?.Invoke();
    }

    /// <summary>Events after <paramref name="since"/>, oldest first (at most <see cref="Capacity"/>), and how many after it the ring no
    /// longer holds.</summary>
    public (IReadOnlyList<string> Events, long Missed) Since(long since)
    {
        lock (_gate)
        {
            var list = new List<string>(_count);
            var oldest = long.MaxValue;
            for (var i = 0; i < _count; i++)
            {
                var (seq, json) = _ring[(_next - _count + i + Capacity) % Capacity];
                oldest = Math.Min(oldest, seq);
                if (seq > since)
                    list.Add(json);
            }
            var missed = _count == 0 ? Math.Max(0, _last - since) : Math.Max(0, oldest - 1 - since);
            return (list, missed);
        }
    }

    /// <summary>EVENTS' answer: <c>{"seq":N,"missed":M,"events":[…]}</c>.</summary>
    public string Reply(long since)
    {
        var (events, missed) = Since(since);
        var sb = new StringBuilder(64 + events.Sum(e => e.Length + 1));
        sb.Append(CultureInfo.InvariantCulture, $"{{\"seq\":{LastSeq},\"missed\":{missed},\"events\":[");
        for (var i = 0; i < events.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(events[i]);
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>A session's subscription: from now on every event reaches it in order until it is disposed or it overflows.</summary>
    public IDisposable Subscribe(IPushSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var s = new Subscriber(this, sink);
        lock (_gate)
            _subscribers.Add(s);
        return s;
    }

    private static string Compose(long seq, string kind, long atUnixMs, long atLeaderNs, string fieldsJson)
    {
        var sb = new StringBuilder(96 + fieldsJson.Length);
        sb.Append(CultureInfo.InvariantCulture, $"{{\"seq\":{seq},\"at\":{atUnixMs},\"leaderNs\":{atLeaderNs},\"kind\":");
        sb.Append('"').Append(System.Text.Json.JsonEncodedText.Encode(kind).ToString()).Append('"');
        var inner = fieldsJson.Trim();
        if (inner.Length > 2 && inner[0] == '{' && inner[^1] == '}')
            sb.Append(',').Append(inner, 1, inner.Length - 2);
        return sb.Append('}').ToString();
    }

    private void Remove(Subscriber s)
    {
        lock (_gate)
            _subscribers.Remove(s);
    }

    private sealed class Subscriber(EventRing ring, IPushSink sink) : IDisposable
    {
        private readonly Queue<long> _window = new();
        private long _delivered;

        /// <summary>Under the ring's gate: false when the subscription ends here.</summary>
        public bool Offer(long seq, string json, long nowMs)
        {
            while (_window.Count > 0 && nowMs - _window.Peek() >= 1000)
                _window.Dequeue();
            if (_window.Count >= MaxPerSecond)
            {
                sink.InOrder(string.Create(CultureInfo.InvariantCulture,
                    $"EVENTS overflow — more than {MaxPerSecond} events in a second; EVENTS SINCE {_delivered} replays what this session missed, then SUBSCRIBE EVENTS again"));
                return false;
            }
            _window.Enqueue(nowMs);
            if (!sink.InOrder("EVENT " + json))
                return false;   // the session's own bound: its sink said "EVENTS overflow …" already
            _delivered = seq;
            return true;
        }

        public void Dispose() => ring.Remove(this);
    }
}
