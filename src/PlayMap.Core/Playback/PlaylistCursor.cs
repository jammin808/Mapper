using PlayMap.Core.Model;

namespace PlayMap.Core.Playback;

/// <summary>A playlist's running position: which item is current, its clip transport, and what plays next.
/// Immutable; the engine's playback lane holds one per playlist and replaces it on every verb and tick.</summary>
public sealed record PlaylistCursor(string PlaylistId, int Index, string? ItemId, ClipTransport Clip, long Seq)
{
    public static PlaylistCursor Idle(string playlistId) => new(playlistId, -1, null, ClipTransport.Empty, 0);

    public bool IsIdle => Index < 0;

    /// <summary>The item index the end of the current item leads to: -1 = stop, same index = loop/hold.</summary>
    public static int NextIndex(Playlist list, int index)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (index < 0 || index >= list.Items.Length)
            return list.Items.Length > 0 ? 0 : -1;
        var item = list.Items[index];
        return item.End switch
        {
            EndAction.Loop or EndAction.Hold => index,
            EndAction.Stop => -1,
            EndAction.Goto => list.Items.IndexOfId(item.GotoItemId) is var g and >= 0 ? g : -1,
            _ => index + 1 < list.Items.Length ? index + 1 : list.Loop ? 0 : -1,
        };
    }

    /// <summary>The item after the current one in list order (wrapping when the list loops) — what NEXT
    /// goes to, whatever the item's end action says.</summary>
    public static int StepIndex(Playlist list, int index, int step)
    {
        ArgumentNullException.ThrowIfNull(list);
        var n = list.Items.Length;
        if (n == 0)
            return -1;
        var i = index + step;
        if (i >= n)
            return list.Loop ? i % n : -1;
        if (i < 0)
            return list.Loop ? ((i % n) + n) % n : 0;
        return i;
    }

    /// <summary>Cues an item: loaded, held at its in point. The clip's length comes from the item's out
    /// point, its duration (stills, pages, generators) or the media's probe.</summary>
    public PlaylistCursor CueItem(Playlist list, int index, long mediaDurationNs)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (index < 0 || index >= list.Items.Length)
            return this with { Index = -1, ItemId = null, Clip = ClipTransport.Empty, Seq = Seq + 1 };
        var item = list.Items[index];
        var outNs = item.OutNs > 0 ? item.OutNs
            : item.DurationNs > 0 ? item.InNs + item.DurationNs
            : mediaDurationNs > 0 ? mediaDurationNs : 0;
        var clip = ClipTransport.Load(item.Id, item.InNs, outNs, item.End == EndAction.Loop, Clip.Seq);
        if (Math.Abs(item.Rate - 1) > 1e-9)
            clip = clip with { Anchor = clip.Anchor with { Rate = item.Rate } };
        return new PlaylistCursor(list.Id, index, item.Id, clip, Seq + 1);
    }
}
