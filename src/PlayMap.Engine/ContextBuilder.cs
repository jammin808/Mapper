using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;

namespace PlayMap.Engine;

/// <summary>The Show Bus §6 context: lights and a headline computed from facts by rules — no model — so the
/// Eye, Stagewatch's show network and every assistant read the same thing with or without AI. Unknown stays
/// grey.</summary>
public static class ContextBuilder
{
    public static ShowContext Build(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var s = engine.Services;
        var p = engine.Store.Project;
        var now = engine.LeaderNowNs;
        var air = engine.Air;
        var items = new List<ContextItem>();
        var links = new List<ContextLink>();

        if (air.EffectiveBlackout)
            items.Add(new ContextItem("air", "air", "Air", Light.Amber, air.PatternsBlackout && air.FollowPatternsBlackout && !air.Blackout ? "blacked out by Patterns" : "blacked out"));
        else if (air.PatternKind is not null)
            items.Add(new ContextItem("air", "air", "Air", Light.Amber, $"test pattern {Core.Content.GeneratorCatalog.Find(air.PatternKind)?.Name ?? air.PatternKind} up"));

        string? playing = null;
        foreach (var list in p.Playlists)
        {
            var lane = engine.Snapshot.Playback.Playlists.GetValueOrDefault(list.Id);
            if (lane is null || lane.IsIdle)
            {
                items.Add(new ContextItem("playlist:" + list.Id, "playlist", list.Name, Light.Grey, "idle"));
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
            if (clip.State == TransportState.Playing && playing is null)
                playing = $"{list.Name} · {state}";
        }

        var health = s.Outputs.Health();
        var green = 0;
        foreach (var o in p.Outputs.Where(o => o.Enabled && o.Kind != OutputKind.Virtual))
        {
            var h = health.FirstOrDefault(x => x.OutputId == o.Id);
            var light = h is null ? Light.Grey : !h.Open ? Light.Red : h.Dropped > 0 ? Light.Amber : Light.Green;
            if (light == Light.Green)
                green++;
            items.Add(new ContextItem("output:" + o.Id, "output", o.Name, light,
                h is null ? "not measured yet" : !h.Open ? "not open" : $"{h.Fps:0} fps{(h.Dropped > 0 ? $", {h.Dropped} dropped" : "")}"));
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
            items.Add(new ContextItem("link:patterns", "link", "Patterns", light, pl.State == "live" && !pl.Paired ? "not paired" : pl.State));
            links.Add(new ContextLink("link:patterns", "air", "controls", light));
        }
        if (s.Network.Stagewatch() is { } sw)
        {
            var light = sw.State == "live" ? Light.Green : sw.State == "stale" ? Light.Amber : Light.Red;
            items.Add(new ContextItem("link:stagewatch", "link", "Stagewatch", light, sw.State + (sw.Detail.Length > 0 ? ", " + sw.Detail : "")));
        }
        var sync = s.Network.Sync();
        if (sync.Role == "follower")
            items.Add(new ContextItem("sync", "clock", "Sync", sync.OffsetMs > 2000 ? Light.Red : sync.RttMs > 5 ? Light.Amber : Light.Green,
                $"following {sync.Leader}, rtt {sync.RttMs:0.0} ms"));

        if (s.Scan.Current is { } scan && scan.State is not ("done" or "failed" or "cancelled"))
            items.Add(new ContextItem("scan", "scan", "Scan", Light.Amber, scan.Words));

        var outputsWords = green > 0 ? $"{green} output{(green == 1 ? "" : "s")} green" : !p.Outputs.Any(o => o.Enabled) ? "no outputs on" : "outputs not measured";
        var headline = air.EffectiveBlackout ? "BLACKOUT · " + outputsWords
            : playing is not null ? $"{playing} · {outputsWords}"
            : $"{p.Name} · nothing playing · {outputsWords}";
        if (sync.Role == "follower")
            headline += $" · synced to {sync.Leader}";

        var recent = engine.Journal.Recent(20).Select(r => new ContextRecent(r.AtUnixMs, r.Ok ? r.Text : "refused: " + r.Text)).ToList();
        var peers = s.Network.Peers().Select(x => new ContextPeer(x.Kind, x.Id, x.Name, ShowContext.ParseLight(x.Light), x.Headline)).ToList();
        return new ShowContext(ShowBusConstants.KindPlayMap, s.Settings.NodeId, s.Settings.Name, s.Wall.UnixMs, headline,
            items, links, recent, ShowContext.CommandsFromCatalog(), peers);
    }
}
