using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Engine.Playback;

namespace PlayMap.Engine;

/// <summary>The Show Bus §6 context: lights and a headline computed from facts by rules — no model — so the
/// Eye, Stagewatch's show network and every assistant read the same thing with or without AI. Unknown stays
/// grey. What reaches another node is said as §6's P2 and P3 say it: each link this node holds to Patterns or Stagewatch is its
/// <c>link</c> item and one row to <c>@&lt;that node's id&gt;</c>, and an output that draws Stagewatch's timer is fed by
/// <c>@&lt;its id&gt;/timer</c>; both are this node's claims, and neither is made while no hello names the far end.</summary>
public static class ContextBuilder
{
    /// <summary>The journal's last rows the context carries as its "recent": the only window of the journal a reader outside the node
    /// takes (MCP's get_context, Patterns, Stagewatch and the assistant).</summary>
    public const int RecentRows = 20;

    /// <summary>The id of the item a Stagewatch's own context gives its timer: the far end of a P3 row for a timer this node draws.</summary>
    public const string StagewatchTimerItem = "timer";

    /// <summary>How many canvases compose inside one another, and how many playlists play one another, before the render draws black
    /// there: the walk of what an output draws stops where the render does.</summary>
    internal const int CanvasDepth = 4, PlaylistDepth = 8;

    /// <summary>§14 <c>context.links</c>: the most links a context lists. Past it the last grey rows are left out first, then the last
    /// of the others, and the item <see cref="LinksLeftOutId"/> says how many.</summary>
    public const int MaxLinks = 400;

    /// <summary>The id of the one grey item a context carries while it lists fewer links than this node has to say.</summary>
    public const string LinksLeftOutId = "context:links";

    public static ShowContext Build(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var s = engine.Services;
        var p = engine.Store.Project;
        var now = engine.LeaderNowNs;
        var air = engine.Air;
        var items = new List<ContextItem>();
        var links = new List<ContextLink>();
        // The rows with an end on another node (§6 P2, P3): they lead the links, so a reader that keeps the first of a long list, and
        // a post trimmed from its end, keep the edges between nodes longest.
        var claims = new List<ContextLink>();
        var heard = s.Network.Peers();

        if (air.EffectiveBlackout)
            items.Add(new ContextItem("air", "air", "Air", Light.Amber, air.PatternsBlackout && air.FollowPatternsBlackout && !air.Blackout ? "blacked out by Patterns" : "blacked out"));
        else if (air.PatternKind is not null)
            items.Add(new ContextItem("air", "air", "Air", Light.Amber, $"test pattern {Core.Content.GeneratorCatalog.Find(air.PatternKind)?.Name ?? air.PatternKind} up"));

        string? playing = null;
        var playback = engine.Snapshot.Playback;
        var listLights = new Dictionary<string, Light>(StringComparer.Ordinal);
        foreach (var list in p.Playlists)
        {
            var lane = playback.Playlists.GetValueOrDefault(list.Id);
            if (lane is null || lane.IsIdle)
            {
                items.Add(new ContextItem("playlist:" + list.Id, "playlist", list.Name, Light.Grey, "idle"));
                listLights[list.Id] = Light.Grey;
                continue;
            }
            var (_, clip, itemId) = lane.At(now);
            var label = list.Items.Find(itemId)?.Label ?? "item";
            var rem = clip.RemainingAt(now);
            var state = clip.State switch
            {
                TransportState.Playing => rem is { } r ? $"playing {label}, {ClipTransport.Format(r)} left" : $"playing {label}",
                TransportState.Paused => $"paused on {label}",
                TransportState.Cued => $"cued {label}",
                TransportState.Ended => $"{label} ended",
                _ => "idle",
            };
            var light = s.Media is not null && lane.Source.Kind == SourceKind.Media && p.FindMedia(lane.Source.Id) is { IsFile: true } m
                && s.Media.Readiness(m) is { Present: false }
                ? Light.Red : Light.Green;
            items.Add(new ContextItem("playlist:" + list.Id, "playlist", list.Name, light, light == Light.Red ? state + " — its file is missing" : state));
            listLights[list.Id] = light;
            if (clip.State == TransportState.Playing && playing is null)
                playing = $"{list.Name} · {state}";
        }

        var health = s.Outputs.Health();
        var green = 0;
        var timerOn = new List<string>();
        foreach (var o in p.Outputs.Where(o => o.Enabled && o.Kind != OutputKind.Virtual))
        {
            var h = health.FirstOrDefault(x => x.OutputId == o.Id);
            var light = h is null ? Light.Grey : !h.Open ? Light.Red : h.Dropped > 0 ? Light.Amber : Light.Green;
            if (light == Light.Green)
                green++;
            var words = h is null ? "not measured yet" : !h.Open ? "not open" : $"{h.Fps:0} fps{(h.Dropped > 0 ? $", {h.Dropped} dropped" : "")}";
            // What the output shows now, after its own words; nothing is added while the showing words are empty.
            var showing = engine.ShowingWords(o);
            items.Add(new ContextItem("output:" + o.Id, "output", o.Name, light, showing.Length == 0 ? words : $"{words} · {showing}"));
            // Which playlist is on which output, in the project's order, each row in its playlist's light (an idle one's grey).
            var (lists, timer) = Drawn(p, o, playback, now);
            foreach (var list in p.Playlists.Where(l => lists.Contains(l.Id)))
                links.Add(new ContextLink("playlist:" + list.Id, "output:" + o.Id, ContextLinkKinds.Feeds, listLights[list.Id]));
            if (timer)
                timerOn.Add(o.Id);
        }

        foreach (var pj in p.Stage.Projectors)
        {
            var light = pj.SolveResidualPx <= 0 ? Light.Grey : pj.SolveResidualPx < 1.5 ? Light.Green : pj.SolveResidualPx < 4 ? Light.Amber : Light.Red;
            items.Add(new ContextItem("projector:" + pj.Id, "projector", pj.Name, light,
                pj.SolveResidualPx <= 0 ? "not calibrated" : $"calibrated, {pj.SolveResidualPx:0.0} px"));
            foreach (var o in p.Outputs.Where(o => o.ProjectorId == pj.Id))
                links.Add(new ContextLink("output:" + o.Id, "projector:" + pj.Id, "drives", light));
        }

        var media = p.Media.Where(m => m.IsFile).ToList();
        if (media.Count > 0 && s.Media is not null)
        {
            var missing = media.Count(m => s.Media.Readiness(m) is not { Present: true });
            items.Add(new ContextItem("media", "media", "Media", missing > 0 ? Light.Amber : Light.Green,
                missing > 0 ? $"{missing} of {media.Count} files missing" : $"{media.Count} files ready"));
        }

        if (s.Network.Patterns() is { } pl)
        {
            var light = pl.State == "live" ? (pl.Paired ? Light.Green : Light.Amber) : pl.State == "stale" ? Light.Amber : Light.Red;
            var desk = PeerEnd(pl, heard, s.Settings.NodeId);
            items.Add(new ContextItem("link:patterns", "link", "Patterns", light, (pl.State == "live" && !pl.Paired ? "not paired" : pl.State) + NotProved(desk)));
            links.Add(new ContextLink("link:patterns", "air", "controls", light));
            // P2: this node reads the desk's state on the link (its blackout, its clip), so the link follows the desk.
            if (desk is not null)
                claims.Add(new ContextLink("link:patterns", desk, ContextLinkKinds.Follows, light));
        }
        if (s.Network.Stagewatch() is { } sw)
        {
            var light = sw.State == "live" ? (sw.Paired ? Light.Green : Light.Amber) : sw.State == "stale" ? Light.Amber : Light.Red;
            var stagewatch = PeerEnd(sw, heard, s.Settings.NodeId);
            items.Add(new ContextItem("link:stagewatch", "link", "Stagewatch", light, sw.State + NotProved(stagewatch) + (sw.Detail.Length > 0 ? ", " + sw.Detail : "")));
            if (stagewatch is not null)
            {
                // P2: this node reads Stagewatch's frames, so the link follows it. P3: each output that draws the timer is fed by
                // Stagewatch's own timer item, in the link's light — the time on it is only as good as the link.
                claims.Add(new ContextLink("link:stagewatch", stagewatch, ContextLinkKinds.Follows, light));
                foreach (var id in timerOn)
                    claims.Add(new ContextLink(stagewatch + "/" + StagewatchTimerItem, "output:" + id, ContextLinkKinds.Feeds, light));
            }
        }
        var sync = s.Network.Sync();
        if (sync.Role == "follower")
            items.Add(new ContextItem("sync", "clock", "Sync", sync.OffsetMs > 2000 ? Light.Red : sync.RttMs > 5 ? Light.Amber : Light.Green,
                $"following {sync.Leader}, rtt {sync.RttMs:0.0} ms"));

        if (s.Scan.Current is { } scan && scan.State is not ("done" or "failed" or "cancelled"))
            items.Add(new ContextItem("scan", "scan", "Scan", Light.Amber, scan.Words));

        foreach (var f in s.Faults?.Faults() ?? [])
            items.Add(new ContextItem("fault:" + f.Part, "fault", f.Part, Light.Red, f.Words));

        links.InsertRange(0, claims);
        engine.AddOpsContext(items, links);
        engine.AddModuleContext(items, links);
        // §6's size for links, kept as its trimming orders them: grey rows go first, then the others, each from the end of the list.
        // The rows between nodes lead it and are never grey (a link item's light is green, amber or red), so they are the last to go.
        var leftOut = links.Count - MaxLinks;
        if (leftOut > 0)
        {
            for (var i = links.Count - 1; i >= 0 && links.Count > MaxLinks; i--)
            {
                if (links[i].Light == Light.Grey)
                    links.RemoveAt(i);
            }
            if (links.Count > MaxLinks)
                links.RemoveRange(MaxLinks, links.Count - MaxLinks);
            items.Add(new ContextItem(LinksLeftOutId, "health", "Context", Light.Grey,
                $"left out: {leftOut} link{(leftOut == 1 ? "" : "s")} (a context lists at most {MaxLinks})"));
        }

        var outputsWords = green > 0 ? $"{green} output{(green == 1 ? "" : "s")} green" : !p.Outputs.Any(o => o.Enabled) ? "no outputs on" : "outputs not measured";
        var headline = air.EffectiveBlackout ? "BLACKOUT · " + outputsWords
            : playing is not null ? $"{playing} · {outputsWords}"
            : $"{p.Name} · nothing playing · {outputsWords}";
        if (sync.Role == "follower")
            headline += $" · synced to {sync.Leader}";
        headline = engine.ModuleHeadline(headline);

        var recent = engine.Journal.Recent(RecentRows).Select(r => new ContextRecent(r.AtUnixMs, r.Ok ? r.Text : "refused: " + r.Text)).ToList();
        // §6: an id in conflict (§2), this node's own among them, adds no peer row, and a context lists at most context.peers rows;
        // another process saying this node's id is an amber item of its own instead.
        if (heard.FirstOrDefault(x => x.Conflict && x.Id == s.Settings.NodeId) is { } clash)
            items.Add(new ContextItem("bus:hello", "bus", "Show Bus hello", Light.Amber, clash.Headline));
        var peers = heard.Where(x => !x.Conflict && x.Id != s.Settings.NodeId).Take(ShowBusConstants.ContextPeers)
            .Select(x => new ContextPeer(x.Kind, x.Id, x.Name, ShowContext.ParseLight(x.Light), x.Headline)).ToList();
        return new ShowContext(ShowBusConstants.KindPlayMap, s.Settings.NodeId, s.Settings.Name, s.Wall.UnixMs, headline,
            items, links, recent, ShowContext.CommandsFromCatalog(engine), peers);
    }

    /// <summary>§6's end for the node a link reaches, <c>@&lt;id&gt;</c>, or null when the link names none a reader could place: no hello
    /// gave an id, the id is no §2 id, it is this node's own, or two machines say it (§2's mark: §6 gives that id no peers row, so a
    /// row to it would point at whichever of the two a reader shows).</summary>
    private static string? PeerEnd(LinkView link, IReadOnlyList<PeerView> heard, string self) =>
        PeerLink.IsId(link.PeerId) && !string.Equals(link.PeerId, self, StringComparison.Ordinal) && !heard.Any(x => x.Conflict && x.Id == link.PeerId)
            ? "@" + link.PeerId : null;

    /// <summary>§6 P2's words for a link that names its far end before LINK proves it (this node's links are level 0): the id is the
    /// one a hello gave, so the item that carries the row says so.</summary>
    private static string NotProved(string? end) => end is null ? "" : " · not proved";

    /// <summary>What an output's picture comes from at a leader instant, as the render reads the project: a source output its source,
    /// a canvas output its canvas's layers, a projector output the stage's surfaces — only what is drawn (a visible layer or surface
    /// with some opacity; a surface on a mesh the project holds, or pixel-mapped on this output's projector; a canvas with a size,
    /// and a canvas or a playlist no deeper than the render nests them and never inside itself). A canvas is composed once a frame,
    /// where the output first reaches it: reached again it is that same picture, whatever was too deep to draw in it the first
    /// time; one reached too deep is black there and composed where it is next reached. A playlist on the way is one the output
    /// shows, idle or not; past it only what its item on the air draws through it is followed (a canvas, another playlist; both
    /// items while one mixes into the next). Whether a Stagewatch timer is among what is drawn is the second answer: a timer that
    /// is a playlist's own item is not, since a playlist draws only its items' media, live inputs and drawn pictures. The air's
    /// blackout, a test pattern and a panic's safe look stand over all of it and are said by their own items.</summary>
    private static (HashSet<string> Playlists, bool Timer) Drawn(Project p, Output o, PlaybackSnapshot playback, long leaderNs)
    {
        var lists = new HashSet<string>(StringComparer.Ordinal);
        // The playlists being played on the way down, and the canvases composed so far (the one being composed among them).
        var playing = new HashSet<string>(StringComparer.Ordinal);
        var composed = new HashSet<string>(StringComparer.Ordinal);
        var timer = false;
        switch (o.Mode)
        {
            case OutputMode.Projector when p.FindProjector(o.ProjectorId) is not null:
                foreach (var sf in p.Stage.Surfaces)
                {
                    // A pixel-mapped surface is drawn as its quad on its own projector; any other needs its mesh.
                    var placed = sf.Pixel is { } quad ? string.Equals(quad.ProjectorId, o.ProjectorId, StringComparison.Ordinal) : p.FindMesh(sf.MeshId) is not null;
                    if (sf.Visible && sf.Opacity > 0 && placed)
                        Walk(sf.Content.Source, 0);
                }
                break;
            case OutputMode.Canvas:
                Walk(SourceRef.Canvas(o.Source.Id), 0);
                break;
            case OutputMode.Source:
                Walk(o.Source, 0);
                break;
        }
        return (lists, timer);

        void Walk(SourceRef source, int canvases)
        {
            switch (source.Kind)
            {
                case SourceKind.Timer:
                    timer = true;
                    break;
                case SourceKind.Canvas:
                    if (p.FindCanvas(source.Id) is not { Width: >= 1, Height: >= 1 } canvas || canvases >= CanvasDepth || !composed.Add(canvas.Id))
                        break;
                    foreach (var layer in canvas.Layers)
                    {
                        if (layer.Visible && layer.Opacity > 0 && !layer.Rect.IsEmpty)
                            Walk(layer.Source, canvases + 1);
                    }
                    break;
                case SourceKind.Playlist:
                    if (p.FindPlaylist(source.Id) is null || playing.Count >= PlaylistDepth || !playing.Add(source.Id))
                        break;
                    lists.Add(source.Id);
                    if (playback.Playlists.GetValueOrDefault(source.Id) is { IsIdle: false } lane)
                    {
                        Through(lane.At(leaderNs).Source, canvases);
                        if (lane.MixAt(leaderNs) is not null)
                            Through(lane.NextSource, canvases);
                    }
                    playing.Remove(source.Id);
                    break;
            }
        }

        // An item's picture is followed only where the render routes the playlist to it: a canvas or another playlist.
        void Through(SourceRef item, int canvases)
        {
            if (item.Kind is SourceKind.Canvas or SourceKind.Playlist)
                Walk(item, canvases);
        }
    }
}
