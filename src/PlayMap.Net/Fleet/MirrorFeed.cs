namespace PlayMap.Net.Fleet;

/// <summary>A backup as its mirror subscription named it: the session, its node id, its HELLO name and address.</summary>
public sealed record MirrorMember(string SessionId, string Node, string Name, string Address);

/// <summary>A primary's side of <c>SUBSCRIBE MIRROR &lt;node&gt;</c>: every subscriber gets, in order, the <c>MIRROR …</c> lines the primary
/// publishes; a new subscriber first gets the latest line of every key (the hello, the project's revision, each playlist's anchor,
/// each kind of air), in the order the keys were first published, so a backup that comes back rejoins the show where it is. A key's
/// later line replaces its latest in place. The feed moves lines; the engine says what they mean. Any thread.</summary>
public sealed class MirrorFeed
{
    public const string Word = "MIRROR";

    private readonly Lock _gate = new();
    private readonly List<(MirrorMember Member, IPushSink Sink)> _subs = [];
    private readonly List<(string Key, string Line)> _latest = [];
    private long _airSeq;

    /// <summary>Eight hex, new per feed: a backup tells a primary that restarted (counting air lines from one again) from one that
    /// hands a line again.</summary>
    public string Instance { get; } = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

    /// <summary>Raised (outside the lock) when a backup joins or leaves.</summary>
    public event Action? MembersChanged;

    public IReadOnlyList<MirrorMember> Members
    {
        get
        {
            lock (_gate)
                return [.. _subs.Select(s => s.Member)];
        }
    }

    /// <summary>Adds a backup (a second subscription of the same node from the same address replaces the first) and hands it every
    /// latest line. Disposing the answer ends it.</summary>
    public IDisposable Subscribe(MirrorMember member, IPushSink sink)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(sink);
        var entry = (member, sink);
        lock (_gate)
        {
            _subs.RemoveAll(s => s.Member.SessionId == member.SessionId
                || (s.Member.Node == member.Node && string.Equals(s.Member.Address, member.Address, StringComparison.Ordinal)));
            _subs.Add(entry);
            foreach (var (_, line) in _latest)
                sink.InOrder(line);
        }
        MembersChanged?.Invoke();
        return new Ending(this, entry);
    }

    /// <summary>A line kept as its key's latest and pushed to every backup now.</summary>
    public void Publish(string key, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        lock (_gate)
        {
            var i = _latest.FindIndex(k => k.Key == key);
            if (i >= 0)
                _latest[i] = (key, line);
            else
                _latest.Add((key, line));
            Push(line);
        }
    }

    /// <summary>An air line, numbered under the feed's lock (so lines published one after another reach every backup in that order)
    /// and kept as its kind's latest; answers its sequence.</summary>
    public long PublishAir(string kind, Func<long, string> line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            var seq = ++_airSeq;
            var text = line(seq);
            var key = "air:" + kind;
            var i = _latest.FindIndex(k => k.Key == key);
            if (i >= 0)
                _latest[i] = (key, text);
            else
                _latest.Add((key, text));
            Push(text);
            return seq;
        }
    }

    /// <summary>A key's latest line is forgotten (another feed carries that kind now); nothing is pushed.</summary>
    public void Drop(string key)
    {
        lock (_gate)
            _latest.RemoveAll(k => k.Key == key);
    }

    /// <summary>A line for one backup only (none kept); false when that node is not subscribed.</summary>
    public bool SendTo(string node, string line)
    {
        lock (_gate)
        {
            var sent = false;
            foreach (var (m, sink) in _subs)
            {
                if (m.Node == node)
                    sent |= sink.InOrder(line);
            }
            return sent;
        }
    }

    /// <summary>The latest lines are forgotten (a node that stops being a primary starts its next feed afresh).</summary>
    public void Forget()
    {
        lock (_gate)
            _latest.Clear();
    }

    private void Push(string line)
    {
        foreach (var (_, sink) in _subs)
            sink.InOrder(line);
    }

    private void End((MirrorMember, IPushSink) entry)
    {
        bool removed;
        lock (_gate)
            removed = _subs.Remove(entry);
        if (removed)
            MembersChanged?.Invoke();
    }

    private sealed class Ending(MirrorFeed feed, (MirrorMember, IPushSink) entry) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                feed.End(entry);
        }
    }
}
