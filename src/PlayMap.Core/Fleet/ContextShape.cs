using System.Globalization;
using PlayMap.Core.Bus;

namespace PlayMap.Core.Fleet;

/// <summary>A projector with its device's facts folded in: the worse of its calibration's light and its device's, words that say both,
/// and the output that drives it ("" when none does).</summary>
public sealed record FoldedProjector(string Id, string Name, Light Light, string Words, string Output);

/// <summary>The context's order for Patterns' Eye, which draws the first twelve of a node's items after putting reds, then ambers,
/// first (its sort is stable, so the order given here decides among equals): <c>show</c>, <c>machine</c>, the outputs (six at most one
/// by one; beyond six, one <c>outputs</c> item for the green ones and every other output by itself), the projectors (each with its
/// device's facts folded in), the playlists that play (two or more idle ones as one <c>playlists</c> item), <c>mirror:*</c>,
/// <c>sync*</c>, <c>media</c>, <c>preflight</c>, <c>link:*</c>, everything else, <c>fault:*</c>. Every item stays in the context (an
/// assistant reads up to 200): what an aggregate stands for, and a device folded into its projector, move to the end, after the
/// faults. Links are kept; an aggregate gets the show's link to it.</summary>
public static class ContextShape
{
    /// <summary>The things Patterns' Eye draws of one node.</summary>
    public const int Window = 12;

    /// <summary>Outputs drawn one by one before the green ones fold into one item.</summary>
    public const int OutputsShown = 6;

    public static void Shape(List<ContextItem> items, List<ContextLink> links, IReadOnlyList<FoldedProjector> projectors)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(projectors);
        var folds = projectors.ToDictionary(p => "projector:" + p.Id, StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (folds.TryGetValue(items[i].Id, out var f))
                items[i] = items[i] with { Light = f.Light, State = f.Words };
        }
        // A device that controls a projector on the list is said in the projector's words.
        foreach (var l in links)
        {
            if (l.Kind == "controls" && l.From.StartsWith("device:", StringComparison.Ordinal) && folds.ContainsKey(l.To))
                folded.Add(l.From);
        }

        var buckets = new List<ContextItem>[Buckets];
        for (var b = 0; b < Buckets; b++)
            buckets[b] = [];
        var tail = new List<ContextItem>();
        foreach (var item in items)
        {
            if (folded.Contains(item.Id))
                tail.Add(item);
            else
                buckets[BucketOf(item)].Add(item);
        }

        var outputs = buckets[OutputsBucket];
        if (outputs.Count > OutputsShown)
        {
            var green = outputs.Where(o => o.Light == Light.Green).ToList();
            if (green.Count > 0)
            {
                var rest = outputs.Where(o => o.Light != Light.Green).ToList();
                outputs.Clear();
                outputs.Add(new ContextItem("outputs", "outputs", "Outputs", Light.Green,
                    Say($"{green.Count} of {green.Count + rest.Count} outputs green")));
                outputs.AddRange(rest);
                tail.InsertRange(0, green);
                links.Add(new ContextLink("show", "outputs", "controls", Light.Green));
            }
        }

        var playlists = buckets[PlaylistsBucket];
        var idle = playlists.Where(p => p.State == "idle").ToList();
        if (idle.Count >= 2)
        {
            playlists.RemoveAll(p => p.State == "idle");
            playlists.Add(new ContextItem("playlists", "playlists", "Playlists", Light.Grey,
                Say($"{idle.Count} idle: {string.Join(", ", idle.Take(4).Select(p => p.Label))}{(idle.Count > 4 ? ", …" : "")}")));
            tail.AddRange(idle);
        }

        items.Clear();
        foreach (var b in buckets)
            items.AddRange(b);
        items.AddRange(tail);
    }

    /// <summary>The order Patterns' Eye draws a node's items in: reds, then ambers, then the given order (a stable sort); the first
    /// <see cref="Window"/> are drawn.</summary>
    public static IReadOnlyList<ContextItem> EyeWindow(IReadOnlyList<ContextItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.OrderByDescending(i => i.Light == Light.Red).ThenByDescending(i => i.Light == Light.Amber).Take(Window).ToList();
    }

    private const int Buckets = 12;
    private const int OutputsBucket = 2;
    private const int PlaylistsBucket = 4;

    private static int BucketOf(ContextItem i)
    {
        var id = i.Id;
        if (id == "show")
            return 0;
        if (id == "machine")
            return 1;
        if (id.StartsWith("output:", StringComparison.Ordinal))
            return OutputsBucket;
        if (id.StartsWith("projector:", StringComparison.Ordinal))
            return 3;
        if (id.StartsWith("playlist:", StringComparison.Ordinal))
            return PlaylistsBucket;
        if (id.StartsWith("mirror:", StringComparison.Ordinal) || id.StartsWith("backup:", StringComparison.Ordinal))
            return 5;
        if (id == "sync" || id.StartsWith("sync:", StringComparison.Ordinal))
            return 6;
        if (id == "media")
            return 7;
        if (id == "preflight")
            return 8;
        if (id.StartsWith("link:", StringComparison.Ordinal))
            return 9;
        return id.StartsWith("fault:", StringComparison.Ordinal) ? 11 : 10;
    }

    private static string Say(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
