using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>Where one view saw a track: the camera position and how confidently it was decoded.</summary>
public sealed record TrackObservation(string ViewId, Vec2d Camera, float Confidence);

/// <summary>One surface point named by the projector pixel that lights it: the same projector pixel seen from two
/// views is the same 3D point, so no feature matching is needed and a white wall or curved plaster works.</summary>
public sealed record Track(string ProjectorId, Vec2d Projector, ImmutableArray<TrackObservation> Seen);

/// <summary>One view's decode of one projector, inverted onto the projector's raster.</summary>
public sealed record ViewDecode(string ViewId, string ProjectorId, InverseCorrespondence Inverse);

/// <summary>
/// The tracks of a multi-view scan: for every projector, every grid node any view placed, with the views that
/// placed it (in view id order). Ordered by projector id, then node row and column — the same tracks in the same
/// order for the same decodes, whatever order they arrive in.
/// </summary>
public sealed record TrackSet(ImmutableArray<Track> Tracks)
{
    public static readonly TrackSet Empty = new(ImmutableArray<Track>.Empty);

    /// <summary>Builds the tracks from each view's inverted decode at <paramref name="stride"/> (the options' stride when null).</summary>
    public static TrackSet Build(IEnumerable<ViewDecode> decodes, int? stride = null)
    {
        ArgumentNullException.ThrowIfNull(decodes);
        var nodes = new Dictionary<(string Projector, long Node), List<TrackObservation>>();
        var places = new Dictionary<(string Projector, long Node), Vec2d>();
        foreach (var d in decodes)
        {
            ArgumentNullException.ThrowIfNull(d);
            var s = stride ?? d.Inverse.Options.Stride;
            foreach (var sample in d.Inverse.Grid(s))
            {
                var key = (d.ProjectorId, Key(sample.Projector, s));
                if (!nodes.TryGetValue(key, out var list))
                {
                    nodes[key] = list = [];
                    places[key] = sample.Projector;
                }
                if (list.Any(o => o.ViewId == d.ViewId))
                    throw new ArgumentException($"View {d.ViewId} decoded projector {d.ProjectorId} twice.", nameof(decodes));
                list.Add(new TrackObservation(d.ViewId, sample.Camera, sample.Confidence));
            }
        }
        var tracks = nodes.Keys
            .OrderBy(k => k.Projector, StringComparer.Ordinal)
            .ThenBy(k => k.Node)
            .Select(k => new Track(k.Projector, places[k], [.. nodes[k].OrderBy(o => o.ViewId, StringComparer.Ordinal)]))
            .ToImmutableArray();
        return new TrackSet(tracks);
    }

    /// <summary>A node's row-major key on its grid.</summary>
    private static long Key(Vec2d node, int stride) => ((long)Math.Floor(node.Y / stride) << 32) | (uint)Math.Floor(node.X / stride);

    public int Count => Tracks.Length;

    /// <summary>Every observation of every track.</summary>
    public int Observations => Tracks.Sum(t => t.Seen.Length);

    /// <summary>The tracks seen by at least <paramref name="views"/> views.</summary>
    public int SeenBy(int views) => Tracks.Count(t => t.Seen.Length >= views);

    /// <summary>The tracks without one view's observations; a track no other view saw is gone.</summary>
    public TrackSet Without(string viewId) => new([.. Tracks
        .Select(t => t with { Seen = t.Seen.RemoveAll(o => o.ViewId == viewId) })
        .Where(t => t.Seen.Length > 0)]);
}
