using System.Globalization;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;

namespace PlayMap.Core.Media.Optimise;

/// <summary>Which file of an item a node plays. With <c>use: "auto"</c>: a version whose codec this node's GPU samples, not marked
/// for review, whose file is here — the highest measured quality first, then the newest; else the source. <c>"source"</c> plays
/// the source; a version id plays that version when this node can, else the source with the words why. Pure.</summary>
public static class VersionChoice
{
    public static (MediaVersion? Version, string Why) Pick(MediaItem item, BlockFormats formats, Func<string, bool> present)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(present);
        if (item.Versions.IsDefaultOrEmpty)
            return (null, "no versions: the source plays");
        if (string.Equals(item.Use, "source", StringComparison.Ordinal))
            return (null, "use is source");
        if (!string.Equals(item.Use, "auto", StringComparison.Ordinal))
        {
            var named = item.Versions.FirstOrDefault(v => string.Equals(v.Id, item.Use, StringComparison.Ordinal));
            if (named is null)
                return (null, $"use names {item.Use}, which is not a version of {item.Name}: the source plays");
            var why = Unplayable(named, formats, present);
            return why is null ? (named, $"use names {named.Id}") : (null, why + ": the source plays");
        }
        MediaVersion? best = null;
        var reasons = new List<string>();
        foreach (var v in item.Versions)
        {
            if (v.Review)
            {
                reasons.Add($"{v.Id} waits for a person (below the quality floor)");
                continue;
            }
            if (Unplayable(v, formats, present) is { } why)
            {
                reasons.Add(why);
                continue;
            }
            if (best is null || v.Quality.PsnrAvg > best.Quality.PsnrAvg
                || (v.Quality.PsnrAvg.Equals(best.Quality.PsnrAvg) && v.CreatedUnixMs > best.CreatedUnixMs))
                best = v;
        }
        if (best is not null)
            return (best, string.Create(CultureInfo.InvariantCulture, $"{Words(best)}, present, {best.Quality.PsnrAvg:0.0} dB"));
        return (null, string.Join("; ", reasons) + ": the source plays");
    }

    /// <summary>The item as this node plays it: the picked version's bytes and facts in the item's place (same id, name and
    /// settings), or the item itself when the source plays.</summary>
    public static MediaItem Played(MediaItem item, BlockFormats formats, Func<string, bool> present)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (v, _) = Pick(item, formats, present);
        return v is null ? item : item with { ContentHash = v.ContentHash, SizeBytes = v.SizeBytes, Info = v.Info, Location = "" };
    }

    /// <summary>Why this node cannot play a version (its codec's blocks, its file), or null.</summary>
    public static string? Unplayable(MediaVersion v, BlockFormats formats, Func<string, bool> present)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(present);
        if (!OptimiseTargets.TryCodec(v.To, out var codec))
            return $"{v.Id} is {v.To}, which this node does not play as a version";
        if (HapCodecs.Missing(codec, formats) is { Length: > 0 } missing)
            return $"{v.Id} is {HapCodecs.Words(codec)}; this GPU's GL has no {missing}";
        if (!present(v.ContentHash))
            return $"{v.Id}'s file is not on this node";
        return null;
    }

    public static string Words(MediaVersion v) => OptimiseTargets.TryCodec(v.To, out var c) ? HapCodecs.Words(c) : v.To;
}
