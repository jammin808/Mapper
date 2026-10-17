using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Sync;

/// <summary>A group member as its subscription named it: the session, its node id, its HELLO name and address, and its output
/// ids (so the leader's sync board names the outputs it reports).</summary>
public sealed record AnchorMember(string SessionId, string Node, string Name, string Address, ImmutableArray<string> Outputs);

/// <summary>A member's answer to an air line: "accepted" (it will run it at the instant), "applied" (at that leader instant),
/// "refused" (its words), or "silent" (no answer in time).</summary>
public sealed record AirReceipt(string Node, string Name, string Stage, long AppliedLeaderNs, string Words);

/// <summary>The leader's side of the group feed (<c>SUBSCRIBE ANCHORS</c>): every subscriber gets, in order, an
/// <c>ANCHOR &lt;group&gt; &lt;playlist&gt; &lt;json&gt;</c> line on each transport change of a synced playlist, and the group's air lines
/// <c>ANCHOR &lt;group&gt; @air {"feed","seq","line","leaderNs"}</c>, which each member runs at that leader instant through its own gate and
/// answers with <c>SYNC RECEIPT</c>. A new subscriber first gets the latest anchor of every playlist and the latest air line of
/// each kind (blackout, master, panic), so a member that comes back rejoins the show where it is. The feed moves lines; what they
/// mean is the engine's. Any thread.</summary>
public sealed class AnchorFeed
{
    /// <summary>The prefix of every line the feed pushes.</summary>
    public const string Word = "ANCHOR";

    /// <summary>The air line's key in place of a playlist id.</summary>
    public const string Air = "@air";

    private readonly Lock _gate = new();
    private readonly List<Subscriber> _subs = [];
    private readonly List<(string Key, string Line)> _latest = [];
    private readonly Dictionary<(long Seq, string Session), Waiting> _waiting = [];
    private long _airSeq;

    /// <summary>Eight hex, new per feed: an air line's sequence is counted per feed, so a member tells a leader that restarted
    /// (counting from one again) from one that hands a line again.</summary>
    public string Instance { get; } = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

    private sealed record Subscriber(AnchorMember Member, IPushSink Sink);

    /// <summary>Receipts kept waiting past their send's wait (members that accepted a line for a later instant), at most this many.</summary>
    private const int LateKept = 256;

    private sealed class Waiting(AnchorMember member, string line)
    {
        public AnchorMember Member { get; } = member;

        public string Line { get; } = line;

        /// <summary>The send stopped waiting: a final receipt from now on is a late one.</summary>
        public bool Late { get; set; }

        public TaskCompletionSource<AirReceipt> Final { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AirReceipt? Accepted { get; set; }
    }

    /// <summary>Raised (outside the feed's lock) when a member joins or leaves.</summary>
    public event Action? MembersChanged;

    /// <summary>A member's receipt for an air line was taken (accepted, or its final stage), with the line's sequence number, on the
    /// thread that heard it.</summary>
    public event Action<long, AirReceipt>? Heard;

    /// <summary>A member's final receipt for a line whose send stopped waiting with it accepted (a line for a later instant): the line
    /// and the receipt.</summary>
    public event Action<string, AirReceipt>? LateReceipt;

    /// <summary>The group's members now, in the order they joined: the subscribers that named their node (a subscriber that names
    /// none only reads the lines — a desk watching the group — and is never waited for).</summary>
    public IReadOnlyList<AnchorMember> Members
    {
        get
        {
            lock (_gate)
                return [.. _subs.Where(s => s.Member.Node.Length > 0).Select(s => s.Member)];
        }
    }

    /// <summary>Every subscriber now, members and readers.</summary>
    public int Subscribers
    {
        get
        {
            lock (_gate)
                return _subs.Count;
        }
    }

    /// <summary>Adds a subscriber (a second subscription of the same session, or a member that came back on a new connection,
    /// replaces the first) and hands it every latest line. Disposing the answer ends it.</summary>
    public IDisposable Subscribe(AnchorMember member, IPushSink sink)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(sink);
        var sub = new Subscriber(member, sink);
        lock (_gate)
        {
            // A member that came back on a new connection (same node, same address) replaces its old subscription.
            _subs.RemoveAll(s => s.Member.SessionId == member.SessionId
                || (member.Node.Length > 0 && s.Member.Node == member.Node && string.Equals(s.Member.Address, member.Address, StringComparison.Ordinal)));
            _subs.Add(sub);
            foreach (var (_, line) in _latest)
                sink.InOrder(line);
        }
        MembersChanged?.Invoke();
        return new Ending(this, sub);
    }

    /// <summary>A synced playlist's transport moved: its anchor to every member, kept as the playlist's latest.</summary>
    public void Publish(string group, string playlistId, string anchorJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentNullException.ThrowIfNull(anchorJson);
        Push(group + " " + playlistId, $"{Word} {group} {playlistId} {anchorJson}");
    }

    /// <summary>Sends an air line to every member at a leader instant and waits (at most <paramref name="wait"/>) for each one's
    /// final receipt; a member that only accepted reads "accepted", one that said nothing "silent". <paramref name="kind"/> keeps
    /// the line as that kind's latest for members that join later (null: not kept). The line is numbered and pushed before this
    /// returns its task (one lock for both), so lines sent one after another reach every member in that order.</summary>
    public async Task<IReadOnlyList<AirReceipt>> SendAirAsync(string group, string? kind, string line, long leaderNs, TimeSpan wait, CancellationToken ct,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        long seq;
        List<Waiting> waits;
        List<Subscriber> dropped;
        lock (_gate)
        {
            seq = ++_airSeq;
            var at = seq;
            var json = JsonText.Write(w =>
            {
                w.WriteStartObject();
                w.WriteString("feed", Instance);
                w.WriteNumber("seq", at);
                w.WriteString("line", line);
                w.WriteNumber("leaderNs", leaderNs);
                w.WriteEndObject();
            });
            waits = [.. _subs.Where(s => s.Member.Node.Length > 0).Select(s => new Waiting(s.Member, line))];
            foreach (var w in waits)
                _waiting[(seq, w.Member.SessionId)] = w;
            dropped = PushLocked(kind is null ? null : group + " " + Air + " " + kind, $"{Word} {group} {Air} {json}");
        }
        if (dropped.Count > 0)
            MembersChanged?.Invoke();
        try
        {
            // One deadline for the whole group: every member has the same span to answer, however many there are.
            try
            {
                await Task.WhenAll(waits.Select(w => w.Final.Task)).WaitAsync(wait, time ?? TimeProvider.System, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Those that did not answer finally read as accepted or silent below.
            }
            var receipts = new List<AirReceipt>(waits.Count);
            foreach (var w in waits)
            {
                receipts.Add(w.Final.Task.IsCompletedSuccessfully ? await w.Final.Task.ConfigureAwait(false)
                    : w.Accepted ?? new AirReceipt(w.Member.Node, w.Member.Name, "silent", 0,
                        string.Create(CultureInfo.InvariantCulture, $"no answer within {wait.TotalSeconds:0.#} s")));
            }
            return receipts;
        }
        finally
        {
            lock (_gate)
            {
                foreach (var w in waits)
                {
                    // A member that accepted a line for a later instant answers when it runs it: that receipt is kept for.
                    if (w.Accepted is not null && !w.Final.Task.IsCompleted)
                        w.Late = true;
                    else
                        _waiting.Remove((seq, w.Member.SessionId));
                }
                foreach (var old in _waiting.Where(kv => kv.Value.Late).OrderBy(kv => kv.Key.Seq).SkipLast(LateKept).Select(kv => kv.Key).ToList())
                    _waiting.Remove(old);
            }
        }
    }

    /// <summary>A member's receipt for an air line, from the session that subscribed: false when that session waits for no such
    /// line (a stray, or one that came too late).</summary>
    public bool Receipt(string sessionId, long seq, string stage, long appliedLeaderNs, string words)
    {
        Waiting? w;
        lock (_gate)
        {
            if (!_waiting.TryGetValue((seq, sessionId), out w))
                return false;
        }
        var receipt = new AirReceipt(w.Member.Node, w.Member.Name, stage, appliedLeaderNs, words);
        if (stage == "accepted")
        {
            w.Accepted = receipt;
            Heard?.Invoke(seq, receipt);
            return true;
        }
        bool late;
        lock (_gate)
        {
            late = w.Late;
            if (late)
                _waiting.Remove((seq, sessionId));
        }
        w.Final.TrySetResult(receipt);
        Heard?.Invoke(seq, receipt);
        if (late)
            LateReceipt?.Invoke(w.Line, receipt);
        return true;
    }

    /// <summary>Forgets every latest line (a node that stops leading); members stay subscribed.</summary>
    public void Forget()
    {
        lock (_gate)
            _latest.Clear();
    }

    private void Push(string? key, string line)
    {
        List<Subscriber> dropped;
        lock (_gate)
            dropped = PushLocked(key, line);
        if (dropped.Count > 0)
            MembersChanged?.Invoke();
    }

    /// <summary>Keeps the line as its key's latest and hands it to every subscriber, under the feed's lock; answers the subscribers an
    /// overflow closed (removed).</summary>
    private List<Subscriber> PushLocked(string? key, string line)
    {
        if (key is not null)
        {
            _latest.RemoveAll(l => l.Key == key);
            _latest.Add((key, line));
        }
        List<Subscriber> dropped = [];
        foreach (var s in _subs)
        {
            // An overflow closed the member's topic: it rejoins with a fresh SUBSCRIBE (and the latest lines).
            if (!s.Sink.InOrder(line))
                dropped.Add(s);
        }
        foreach (var d in dropped)
            _subs.Remove(d);
        return dropped;
    }

    private void End(Subscriber sub)
    {
        bool removed;
        lock (_gate)
            removed = _subs.Remove(sub);
        if (removed)
            MembersChanged?.Invoke();
    }

    private sealed class Ending(AnchorFeed feed, Subscriber sub) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                feed.End(sub);
        }
    }

    /// <summary>Reads an <c>ANCHOR &lt;group&gt; &lt;key&gt; &lt;json&gt;</c> line: false for anything else.</summary>
    public static bool TryParse(string line, out string group, out string key, out string json)
    {
        group = key = json = "";
        if (line is null || !line.StartsWith(Word + " ", StringComparison.Ordinal))
            return false;
        var rest = line[(Word.Length + 1)..];
        var a = rest.IndexOf(' ', StringComparison.Ordinal);
        if (a <= 0)
            return false;
        var b = rest.IndexOf(' ', a + 1);
        if (b <= a + 1)
            return false;
        group = rest[..a];
        key = rest[(a + 1)..b];
        json = rest[(b + 1)..];
        if (json.Length == 0 || json[0] != '{')
            return false;
        return true;
    }

    /// <summary>Reads an air line's JSON: its feed and sequence, the line and the leader instant; false when any is missing.</summary>
    public static bool TryReadAir(string json, out string feed, out long seq, out string line, out long leaderNs)
    {
        feed = "";
        seq = 0;
        line = "";
        leaderNs = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("seq", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out seq)
                || !r.TryGetProperty("leaderNs", out var l) || l.ValueKind != JsonValueKind.Number || !l.TryGetInt64(out leaderNs)
                || !r.TryGetProperty("line", out var t) || t.ValueKind != JsonValueKind.String
                || !r.TryGetProperty("feed", out var f) || f.ValueKind != JsonValueKind.String)
                return false;
            feed = f.GetString() ?? "";
            line = t.GetString() ?? "";
            return line.Length > 0 && !line.Contains('\n', StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
