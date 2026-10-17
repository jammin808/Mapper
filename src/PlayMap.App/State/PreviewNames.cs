using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.App.State;

/// <summary>A preview view's ring as the node names it: the file its pictures go to (null while the node names none), and the node's
/// words when it names none (its refusal of the view's VIEW … SHOW).</summary>
public sealed record PreviewRingName(string? Ring, string? Words);

/// <summary>The ring the node names for each view, the only files the desk's previews read (and only the node's own rings for the view
/// in this machine's preview folder: <see cref="PlayMap.App.Controls.RingPreviewSource"/>): from the VIEW … SHOW reply
/// (<c>{"view","ring","seq"}</c>) and from STATE <c>machine.views[].ring</c>, the newer of the two winning — a STATE whose seq is below
/// the reply's was written before the SHOW ran, and changes nothing of that view. A view the node names no ring for reads a name without
/// a ring, and its preview shows the stand-in. One per session, emptied when the session's link changes. Thread-safe (the previews read
/// it on every poll).</summary>
public sealed class PreviewNames
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PreviewRingName> _views = new(StringComparer.Ordinal);

    /// <summary>Per view, the STATE seq from which STATE shows it as its last SHOW reply named it: an older STATE changes nothing of it.</summary>
    private readonly Dictionary<string, long> _heldUntil = new(StringComparer.Ordinal);

    private long _lastSeq = long.MinValue;

    /// <summary>What the node names for a view now; null when it has said nothing of it.</summary>
    public PreviewRingName? Of(string view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
            return _views.GetValueOrDefault(view);
    }

    /// <summary>The reply to <c>VIEW &lt;view&gt; SHOW …</c>: the ring it names, or the node's words why the view is not shown. A reply
    /// that carries the STATE seq from which STATE shows the view holds that view's name against any older STATE.</summary>
    public void Shown(string view, WireReply reply)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(reply);
        var (ring, seq) = reply.Ok ? Read(reply.Text) : (null, null);
        var name = reply.Ok ? new PreviewRingName(ring, null) : new PreviewRingName(null, PlayMap.App.Link.DeskSession.Plain(reply.Text));
        lock (_gate)
        {
            _views[view] = name;
            if (seq is { } from)
                _heldUntil[view] = from;
            else
                _heldUntil.Remove(view);
        }
    }

    /// <summary>The desk let the view go (<c>VIEW &lt;view&gt; HIDE</c>): its ring is not read any more.</summary>
    public void Hidden(string view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            _views.Remove(view);
            _heldUntil.Remove(view);
        }
    }

    /// <summary>A STATE push: every view it lists is named with its ring; a view it does not list is shown no more (its ring is let
    /// go, the node's words why kept). A view whose SHOW reply STATE has not caught up with (this STATE's seq below the reply's) keeps
    /// the reply's name. A STATE that carries no <c>machine.views</c> names nothing: only the replies do. A STATE counting below the
    /// last one read is a node that started again (its seq counts from anew): no reply before it holds anything back.</summary>
    public void Read(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (state.Seq < _lastSeq)
                _heldUntil.Clear();
            _lastSeq = state.Seq;
            if (!state.ListsViews)
                return;
            foreach (var id in _views.Keys.ToList())
                if (!state.Views.Any(v => string.Equals(v.Id, id, StringComparison.Ordinal)) && !HeldBack(id, state.Seq))
                    _views[id] = _views[id] with { Ring = null };
            foreach (var v in state.Views)
                if (!HeldBack(v.Id, state.Seq))
                    _views[v.Id] = new PreviewRingName(v.Ring.Length > 0 ? v.Ring : null, null);
        }
    }

    /// <summary>Forgets every name (a new link: its node names its own rings).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _views.Clear();
            _heldUntil.Clear();
            _lastSeq = long.MinValue;
        }
    }

    /// <summary>True when a STATE of this seq is older than the view's last SHOW reply (it changes nothing of the view); a STATE as new
    /// lets the hold go. Under the gate.</summary>
    private bool HeldBack(string view, long seq)
    {
        if (!_heldUntil.TryGetValue(view, out var from))
            return false;
        if (seq < from)
            return true;
        _heldUntil.Remove(view);
        return false;
    }

    /// <summary>The ring a SHOW reply names (<c>{"view","ring","seq"}</c>; null for a reply that names none) and the STATE seq it
    /// carries (null when it carries none).</summary>
    private static (string? Ring, long? Seq) Read(string text)
    {
        if (!text.TrimStart().StartsWith('{'))
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);
            var ring = root.TryGetProperty("ring", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } path ? path : null;
            long? seq = root.TryGetProperty("seq", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt64(out var n) ? n : null;
            return (ring, seq);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
