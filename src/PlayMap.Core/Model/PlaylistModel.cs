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
