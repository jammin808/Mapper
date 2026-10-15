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
/// <c>ANCHOR &lt;group&gt; @air {"seq","line","leaderNs"}</c>, which each member runs at that leader instant through its own gate and
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

    private sealed record Subscriber(AnchorMember Member, IPushSink Sink);

    private sealed class Waiting(AnchorMember member)
    {
        public AnchorMember Member { get; } = member;

        public TaskCompletionSource<AirReceipt> Final { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AirReceipt? Accepted { get; set; }
    }

    /// <summary>Raised (outside the feed's lock) when a member joins or leaves.</summary>
    public event Action? MembersChanged;

    /// <summary>The subscribers now, in the order they joined.</summary>
    public IReadOnlyList<AnchorMember> Members
    {
        get
        {
            lock (_gate)
                return [.. _subs.Select(s => s.Member)];
        }
    }

    /// <summary>Adds a subscriber (a second subscription of the same session replaces the first) and hands it every latest line.
    /// Disposing the answer ends it.</summary>
    public IDisposable Subscribe(AnchorMember member, IPushSink sink)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(sink);
        var sub = new Subscriber(member, sink);
        lock (_gate)
        {
            _subs.RemoveAll(s => s.Member.SessionId == member.SessionId);
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
    /// the line as that kind's latest for members that join later (null: not kept).</summary>
    public async Task<IReadOnlyList<AirReceipt>> SendAirAsync(string group, string? kind, string line, long leaderNs, TimeSpan wait, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        var seq = Interlocked.Increment(ref _airSeq);
        var json = JsonText.Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("seq", seq);
            w.WriteString("line", line);
            w.WriteNumber("leaderNs", leaderNs);
            w.WriteEndObject();
        });
        var text = $"{Word} {group} {Air} {json}";
        List<Waiting> waits;
        lock (_gate)
        {
            waits = [.. _subs.Select(s => new Waiting(s.Member))];
            foreach (var w in waits)
                _waiting[(seq, w.Member.SessionId)] = w;
        }
        Push(kind is null ? null : group + " " + Air + " " + kind, text);
        try
        {
            var receipts = new List<AirReceipt>(waits.Count);
            foreach (var w in waits)
            {
                try
                {
                    receipts.Add(await w.Final.Task.WaitAsync(wait, ct).ConfigureAwait(false));
                }
                catch (TimeoutException)
                {
                    receipts.Add(w.Accepted ?? new AirReceipt(w.Member.Node, w.Member.Name, "silent", 0,
                        string.Create(CultureInfo.InvariantCulture, $"no answer within {wait.TotalSeconds:0.#} s")));
                }
            }
            return receipts;
        }
        finally
        {
            lock (_gate)
            {
                foreach (var w in waits)
                    _waiting.Remove((seq, w.Member.SessionId));
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
            w.Accepted = receipt;
        else
            w.Final.TrySetResult(receipt);
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
        List<Subscriber> dropped = [];
        lock (_gate)
        {
            if (key is not null)
            {
                _latest.RemoveAll(l => l.Key == key);
                _latest.Add((key, line));
            }
            foreach (var s in _subs)
            {
                // An overflow closed the member's topic: it rejoins with a fresh SUBSCRIBE (and the latest lines).
                if (!s.Sink.InOrder(line))
                    dropped.Add(s);
            }
            foreach (var d in dropped)
                _subs.Remove(d);
        }
        if (dropped.Count > 0)
            MembersChanged?.Invoke();
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

    /// <summary>Reads an air line's JSON: its sequence, the line and the leader instant; false when any is missing.</summary>
    public static bool TryReadAir(string json, out long seq, out string line, out long leaderNs)
    {
        seq = 0;
        line = "";
        leaderNs = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("seq", out var s) || !s.TryGetInt64(out seq)
                || !r.TryGetProperty("leaderNs", out var l) || !l.TryGetInt64(out leaderNs)
                || !r.TryGetProperty("line", out var t) || t.ValueKind != JsonValueKind.String)
                return false;
            line = t.GetString() ?? "";
            return line.Length > 0 && !line.Contains('\n', StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
