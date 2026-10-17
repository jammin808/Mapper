using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.App.State;

/// <summary>A preview view's ring as the node names it: the file its pictures go to (null while the node names none), and the node's
/// words when it names none (its refusal of the view's VIEW … SHOW).</summary>
public sealed record PreviewRingName(string? Ring, string? Words);

/// <summary>The ring the node names for each view, the only files the desk's previews read: from the VIEW … SHOW reply
/// (<c>{"view","ring"}</c>) and from STATE <c>machine.views[].ring</c>, the later of the two winning. A view the node names no ring for
/// reads a name without a ring, and its preview shows the stand-in. One per session, emptied when the session's link changes.
/// Thread-safe (the previews read it on every poll).</summary>
public sealed class PreviewNames
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PreviewRingName> _views = new(StringComparer.Ordinal);

    /// <summary>What the node names for a view now; null when it has said nothing of it.</summary>
    public PreviewRingName? Of(string view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
            return _views.GetValueOrDefault(view);
    }

    /// <summary>The reply to <c>VIEW &lt;view&gt; SHOW …</c>: the ring it names, or the node's words why the view is not shown.</summary>
    public void Shown(string view, WireReply reply)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(reply);
        var name = reply.Ok ? new PreviewRingName(RingIn(reply.Text), null) : new PreviewRingName(null, PlayMap.App.Link.DeskSession.Plain(reply.Text));
        lock (_gate)
            _views[view] = name;
    }

    /// <summary>The desk let the view go (<c>VIEW &lt;view&gt; HIDE</c>): its ring is not read any more.</summary>
    public void Hidden(string view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
            _views.Remove(view);
    }

    /// <summary>A STATE push: every view it lists is named with its ring; a view it does not list is shown no more (its ring is let
    /// go, the node's words why kept). A STATE that carries no <c>machine.views</c> names nothing: only the replies do.</summary>
    public void Read(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.ListsViews)
            return;
        lock (_gate)
        {
            foreach (var id in _views.Keys.ToList())
                if (!state.Views.Any(v => string.Equals(v.Id, id, StringComparison.Ordinal)))
                    _views[id] = _views[id] with { Ring = null };
            foreach (var v in state.Views)
                _views[v.Id] = new PreviewRingName(v.Ring.Length > 0 ? v.Ring : null, null);
        }
    }

    /// <summary>Forgets every name (a new link: its node names its own rings).</summary>
    public void Clear()
    {
        lock (_gate)
            _views.Clear();
    }

    /// <summary>The ring a SHOW reply names (<c>{"view","ring"}</c>); null for a reply that names none.</summary>
    private static string? RingIn(string text)
    {
        if (!text.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ring", out var ring)
                && ring.ValueKind == JsonValueKind.String && ring.GetString() is { Length: > 0 } path
                ? path
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
