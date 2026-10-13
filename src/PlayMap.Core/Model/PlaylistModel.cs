using System.Collections.Immutable;

namespace PlayMap.Core.Model;

/// <summary>What happens when an item reaches its out point.</summary>
public enum EndAction
{
    /// <summary>Go to the next item (or the first, when the playlist loops).</summary>
    Next,
    /// <summary>Play this item again from its in point.</summary>
    Loop,
    /// <summary>Hold the last frame until told otherwise.</summary>
    Hold,
    /// <summary>Stop and go to black.</summary>
    Stop,
    /// <summary>Jump to the item named by <see cref="PlaylistItem.GotoItemId"/>.</summary>
    Goto,
}

public enum TransitionKind
{
    Cut,
    Fade,
    /// <summary>Through black.</summary>
    Dip,
}

/// <summary>One entry in a playlist. Times are nanoseconds of the source's own timeline; a still, a page or
/// a generator plays for <see cref="DurationNs"/> (0 = until told).</summary>
public sealed record PlaylistItem(
    string Id,
    SourceRef Source,
    string Label,
    long InNs,
    /// <summary>0 = the source's end.</summary>
    long OutNs,
    long DurationNs,
    EndAction End,
    string? GotoItemId,
    TransitionKind Transition,
    long TransitionNs,
    /// <summary>Playback speed (1 = normal).</summary>
    double Rate,
    /// <summary>0..1.</summary>
    double Volume) : IEntity
{
    public static PlaylistItem For(string id, SourceRef source, string label) =>
        new(id, source, label, 0, 0, 0, EndAction.Next, null, TransitionKind.Cut, 0, 1.0, 1.0);
}

/// <summary>An ordered run of items with its own transport. A node plays any number of playlists at once
/// (each feeds whatever surfaces and layers name it); Patterns can read, build and drive every one.</summary>
public sealed record Playlist(
    string Id,
    string Name,
    ImmutableArray<PlaylistItem> Items,
    bool Loop,
    /// <summary>When set, this playlist's transport follows the sync group's leader on the network.</summary>
    string? SyncGroup) : IEntity;

/// <summary>Playlists that play playlists: an item whose source is another playlist shows that playlist's current
/// picture, so long as no chain of them comes back to a playlist already on it (that would be a picture of itself).
/// Edits that would close such a loop are refused; a loop already in a project (an old file, a hand edit) draws black
/// there and is named by <see cref="Loop"/>.</summary>
public static class PlaylistNesting
{
    /// <summary>Why an item of the playlist <paramref name="ownerId"/> may not show <paramref name="source"/>, or null.</summary>
    public static string? Problem(Project project, string ownerId, SourceRef source)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != SourceKind.Playlist)
            return null;
        var owner = project.FindPlaylist(ownerId)?.Name ?? ownerId;
        if (string.Equals(source.Id, ownerId, StringComparison.Ordinal))
            return $"{owner} cannot play itself";
        if (Reaches(project, source.Id, ownerId))
            return $"{project.FindPlaylist(source.Id)?.Name ?? source.Id} already plays {owner} — a playlist cannot play itself through another";
        return null;
    }

    /// <summary>True when <paramref name="from"/> plays <paramref name="to"/> through its items, directly or through other playlists.</summary>
    public static bool Reaches(Project project, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(project);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            if (project.FindPlaylist(stack.Pop()) is not { } list)
                continue;
            foreach (var item in list.Items)
            {
                if (item.Source.Kind != SourceKind.Playlist)
                    continue;
                if (string.Equals(item.Source.Id, to, StringComparison.Ordinal))
                    return true;
                if (seen.Add(item.Source.Id))
                    stack.Push(item.Source.Id);
            }
        }
        return false;
    }

    /// <summary>The loop a playlist's pictures run into ("Show → Loop A → Loop B → Loop A"), or null when every chain
    /// from it ends.</summary>
    public static string? Loop(Project project, string playlistId)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = new List<string>();
        return Walk(project, playlistId, path);
    }

    private static string? Walk(Project project, string id, List<string> path)
    {
        if (path.Contains(id, StringComparer.Ordinal))
            return string.Join(" → ", path.Append(id).Select(p => project.FindPlaylist(p)?.Name ?? p));
        if (project.FindPlaylist(id) is not { } list)
            return null;
        path.Add(id);
        foreach (var item in list.Items)
            if (item.Source.Kind == SourceKind.Playlist && Walk(project, item.Source.Id, path) is { } loop)
                return loop;
        path.RemoveAt(path.Count - 1);
        return null;
    }
}
