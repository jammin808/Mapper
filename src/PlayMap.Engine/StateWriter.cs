using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Engine.Playback;

namespace PlayMap.Engine;

/// <summary>STATE: the whole node as one compact JSON object, keys in docs/WIRE.md's order (new keys go at the
/// end of their object — some readers match raw substrings). The "video" row is Patterns' own, for the
/// focused playlist, so Stagewatch's VT link and countdown work unchanged.</summary>
public static class StateWriter
{
    public const string Version = "0.1.0";

    public static string Write(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return EngineCore.JsonOut(w => WriteState(w, engine));
    }

    private static void WriteState(Utf8JsonWriter w, EngineCore e)
    {
        var s = e.Services;
        var project = e.Store.Current;
        var now = e.LeaderNowNs;
        w.WriteStartObject();
        w.WriteNumber("v", 1);
        w.WriteNumber("seq", e.StateKey());
        w.WriteString("id", s.Settings.NodeId);
        w.WriteString("t", ShowBusConstants.KindPlayMap);
        w.WriteString("name", s.Settings.Name);
        w.WriteString("version", Version);
        w.WriteNumber("at", s.Wall.UnixMs);
        var ctx = ContextBuilder.Build(e);
        w.WriteString("light", ShowContext.LightWord(ctx.Light));
        w.WriteString("headline", ctx.Headline);

        var air = e.Air;
        w.WriteStartObject("air");
        w.WriteBoolean("blackout", air.EffectiveBlackout);
        w.WriteBoolean("frozen", air.Frozen);
        if (air.PatternKind is null)
            w.WriteNull("pattern");
        else
        {
            w.WriteStartObject("pattern");
            w.WriteString("kind", air.PatternKind);
            w.WriteStartArray("outputs");
            foreach (var o in air.PatternOutputs)
                w.WriteStringValue(o);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteBoolean("identify", air.IdentifyUntilLeaderNs != 0);
        w.WriteBoolean("patternsBlackout", air.PatternsBlackout);
        w.WriteEndObject();

        WriteVideo(w, e, project.Project, now);

        w.WriteStartArray("playlists");
        foreach (var l in project.Project.Playlists)
            WritePlaylist(w, e, l);
        w.WriteEndArray();

        w.WriteStartArray("outputs");
        foreach (var o in project.Project.Outputs)
            WriteOutput(w, e, o);
        w.WriteEndArray();

        var media = project.Project.Media;
        var missing = media.Count(m => m.IsFile && s.Media?.Readiness(m) is not { Present: true });
        w.WriteStartObject("media");
        w.WriteNumber("items", media.Length);
        w.WriteNumber("missing", missing);
        w.WriteBoolean("ready", missing == 0);
        w.WriteStartArray("transfers");
        w.WriteEndArray();
        w.WriteEndObject();

        var stage = project.Project.Stage;
        w.WriteStartObject("stage");
        w.WriteNumber("surfaces", stage.Surfaces.Length);
        w.WriteNumber("meshes", stage.Meshes.Length);
        w.WriteStartArray("projectors");
        foreach (var p in stage.Projectors)
        {
            w.WriteStartObject();
            w.WriteString("id", p.Id);
            w.WriteString("name", p.Name);
            w.WriteBoolean("solved", p.SolveResidualPx > 0);
            w.WriteNumber("residualPx", Math.Round(p.SolveResidualPx, 2));
            w.WriteNumber("pins", p.Pins.Length);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteNumber("scans", stage.Scans.Length);
        w.WriteEndObject();

        w.WritePropertyName("scan");
        e.WriteScanState(w);

        w.WritePropertyName("sync");
        WriteSync(w, e);

        w.WriteStartObject("links");
        WriteLink(w, "patterns", s.Network.Patterns(), null);
        WriteLink(w, "stagewatch", s.Network.Stagewatch(), s.Network.StagewatchTimerJson());
        w.WriteEndObject();

        w.WriteStartArray("peers");
        foreach (var p in s.Network.Peers())
            WritePeer(w, p);
        w.WriteEndArray();

        w.WriteStartObject("machine");
        w.WriteString("renderer", s.Outputs.RendererName);
        w.WriteNumber("memMb", Environment.WorkingSet / (1024 * 1024));
        w.WriteNumber("gcPauseMsTotal", Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1));
        w.WriteEndObject();

        w.WriteStartObject("project");
        w.WriteString("id", project.Project.Id);
        w.WriteString("name", project.Project.Name);
        w.WriteNumber("revision", project.Revision);
        if (e.ProjectPath is null)
            w.WriteNull("path");
        else
            w.WriteString("path", e.ProjectPath);
        w.WriteBoolean("dirty", e.Dirty);
        w.WriteEndObject();

        e.WriteOpsState(w);

        w.WriteEndObject();
    }

    /// <summary>Patterns' "video" row (round 86 shape) for the focused playlist's current item.</summary>
    private static void WriteVideo(Utf8JsonWriter w, EngineCore e, Project p, long now)
    {
        var lane = e.Playback.Snapshot.Focused;
        var list = p.FindPlaylist(lane?.PlaylistId);
        if (lane is null || list is null || lane.IsIdle)
        {
            w.WriteNull("video");
            return;
        }
        var (source, clip, itemId) = lane.At(now);
        var item = list.Items.Find(itemId);
        var file = source.Kind == SourceKind.Media ? (p.FindMedia(source.Id)?.Name ?? source.Id) : item?.Label ?? source.ToString();
        var pos = clip.PositionAt(now) - clip.InNs;
        var len = clip.HasLength ? clip.LengthNs : (long?)null;
        var rem = clip.RemainingAt(now);
        var playing = clip.State == TransportState.Playing;
        var ended = clip.State == TransportState.Ended;
        w.WriteStartObject("video");
        w.WriteString("file", file);
        w.WriteString("role", "playlist");
        w.WriteString("tag", list.Name);
        w.WriteNumber("position", pos / 1_000_000_000);
        if (len is { } l)
            w.WriteNumber("length", l / 1_000_000_000);
        else
            w.WriteNull("length");
        if (rem is { } r)
            w.WriteNumber("remaining", r / 1_000_000_000);
        else
            w.WriteNull("remaining");
        w.WriteString("positionText", ClipTransport.Format(pos));
        w.WriteString("lengthText", len is { } l2 ? ClipTransport.Format(l2) : "");
        w.WriteString("remainingText", rem is { } r2 ? ClipTransport.Format(r2) : "");
        var text = $"{list.Name} {file} · {ClipTransport.Format(pos)}" + (len is { } l3 ? " / " + ClipTransport.Format(l3) : "") + (rem is { } r3 ? $" · {ClipTransport.Format(r3)} left" : "");
        w.WriteString("text", text);
        w.WriteString("chip", rem is { } r4 ? $"{list.Name} {ClipTransport.Format(r4)}" : list.Name);
        w.WriteBoolean("playing", playing);
        w.WriteBoolean("ended", ended);
        w.WriteBoolean("loops", clip.Loop);
        w.WriteBoolean("out", rem is { } r5 && r5 <= 10_000_000_000 && playing);
        w.WriteString("call", "");
        w.WriteString("key", source.Id);
        w.WriteNumber("positionMs", pos / 1_000_000);
        if (len is { } l4)
            w.WriteNumber("lengthMs", l4 / 1_000_000);
        else
            w.WriteNull("lengthMs");
        if (rem is { } r6)
            w.WriteNumber("remainingMs", r6 / 1_000_000);
        else
            w.WriteNull("remainingMs");
        w.WriteBoolean("paused", clip.State is TransportState.Paused or TransportState.Cued);
        w.WriteNumber("rate", Math.Round(clip.Anchor.Rate, 3));
        w.WriteBoolean("seekable", true);
        w.WriteNumber("seq", clip.Seq);
        w.WriteEndObject();
    }

    public static void WritePlaylist(Utf8JsonWriter w, EngineCore e, Playlist list)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(list);
        var now = e.LeaderNowNs;
        var lane = e.Snapshot.Playback.Playlists.GetValueOrDefault(list.Id);
        w.WriteStartObject();
        w.WriteString("id", list.Id);
        w.WriteString("name", list.Name);
        w.WriteBoolean("focused", string.Equals(e.Snapshot.Playback.FocusedId, list.Id, StringComparison.Ordinal));
        if (lane is null || lane.IsIdle)
        {
            w.WriteString("state", "idle");
            w.WriteNumber("index", -1);
            w.WriteNull("item");
            w.WriteNull("positionMs");
            w.WriteNull("lengthMs");
            w.WriteNull("remainingMs");
            w.WriteNumber("rate", 1);
        }
        else
        {
            var (source, clip, itemId) = lane.At(now);
            var index = list.Items.IndexOfId(itemId);
            w.WriteString("state", clip.State.ToString().ToLowerInvariant());
            w.WriteNumber("index", index);
            w.WriteStartObject("item");
            w.WriteString("id", itemId);
            w.WriteString("label", index >= 0 ? list.Items[index].Label : "");
            w.WriteString("source", source.ToString());
            w.WriteEndObject();
            w.WriteNumber("positionMs", (clip.PositionAt(now) - clip.InNs) / 1_000_000);
            if (clip.HasLength)
                w.WriteNumber("lengthMs", clip.LengthNs / 1_000_000);
            else
                w.WriteNull("lengthMs");
            if (clip.RemainingAt(now) is { } rem)
                w.WriteNumber("remainingMs", rem / 1_000_000);
            else
                w.WriteNull("remainingMs");
            w.WriteNumber("rate", Math.Round(clip.Anchor.Rate, 3));
        }
        w.WriteBoolean("loop", list.Loop);
        if (list.SyncGroup is null)
            w.WriteNull("syncGroup");
        else
            w.WriteString("syncGroup", list.SyncGroup);
        w.WriteNumber("seq", lane?.Clip.Seq ?? 0);
        var nextIndex = lane is null || lane.IsIdle ? (list.Items.Length > 0 ? 0 : -1) : PlaylistCursor.NextIndex(list, list.Items.IndexOfId(lane.At(now).ItemId));
        if (nextIndex >= 0 && nextIndex < list.Items.Length)
        {
            w.WriteStartObject("next");
            w.WriteString("id", list.Items[nextIndex].Id);
            w.WriteString("label", list.Items[nextIndex].Label);
            w.WriteEndObject();
        }
        else
            w.WriteNull("next");
        w.WriteNumber("items", list.Items.Length);
        w.WriteEndObject();
    }

    public static void WriteOutput(Utf8JsonWriter w, EngineCore e, Output o)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(o);
        var health = e.Services.Outputs.Health().FirstOrDefault(h => h.OutputId == o.Id);
        w.WriteStartObject();
        w.WriteString("id", o.Id);
        w.WriteString("name", o.Name);
        w.WriteString("mode", o.Mode.ToString().ToLowerInvariant());
        w.WriteString("kind", o.Kind.ToString().ToLowerInvariant());
        w.WriteString("display", health?.Display ?? o.DisplayKey);
        w.WriteNumber("width", o.Width);
        w.WriteNumber("height", o.Height);
        w.WriteNumber("refreshHz", o.RefreshHz);
        w.WriteBoolean("enabled", o.Enabled);
        w.WriteBoolean("open", health?.Open ?? false);
        w.WriteNumber("fps", Math.Round(health?.Fps ?? 0, 1));
        w.WriteNumber("dropped", health?.Dropped ?? 0);
        w.WriteNumber("frameMsP95", Math.Round(health?.FrameMsP95 ?? 0, 2));
        if (e.Air.PatternCovers(o.Id))
            w.WriteString("pattern", e.Air.PatternKind);
        else
            w.WriteNull("pattern");
        if (o.ProjectorId is null)
            w.WriteNull("projector");
        else
            w.WriteString("projector", o.ProjectorId);
        var light = !o.Enabled ? "grey" : o.Kind == OutputKind.Virtual ? "grey" : health is null ? "grey" : !health.Open ? "red" : health.Dropped > 0 ? "amber" : "green";
        w.WriteString("light", light);
        w.WriteEndObject();
    }

    public static void WriteScan(Utf8JsonWriter w, ScanProgress? scan)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (scan is null)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        w.WriteString("state", scan.State);
        if (scan.ProjectorId is null)
            w.WriteNull("projector");
        else
            w.WriteString("projector", scan.ProjectorId);
        w.WriteNumber("pattern", scan.Pattern);
        w.WriteNumber("of", scan.Of);
        w.WriteNumber("quality", Math.Round(scan.Quality, 1));
        w.WriteString("words", scan.Words);
        w.WriteEndObject();
    }

    public static void WriteSync(Utf8JsonWriter w, EngineCore e)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(e);
        var sync = e.Services.Network.Sync();
        w.WriteStartObject();
        w.WriteString("role", sync.Role);
        if (sync.Leader is null)
            w.WriteNull("leader");
        else
            w.WriteString("leader", sync.Leader);
        w.WriteNumber("offsetMs", Math.Round(sync.OffsetMs, 3));
        w.WriteNumber("rttMs", Math.Round(sync.RttMs, 3));
        w.WriteNumber("driftPpm", Math.Round(sync.DriftPpm, 2));
        w.WriteStartArray("groups");
        foreach (var g in e.Store.Project.Playlists.Select(l => l.SyncGroup).Where(g => g is not null).Distinct(StringComparer.Ordinal))
            w.WriteStringValue(g);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteLink(Utf8JsonWriter w, string name, LinkView? link, string? timerJson)
    {
        if (link is null)
        {
            w.WriteStartObject(name);
            w.WriteString("state", "off");
            w.WriteEndObject();
            return;
        }
        w.WriteStartObject(name);
        w.WriteString("state", link.State);
        w.WriteString("host", link.Host);
        w.WriteBoolean("paired", link.Paired);
        w.WriteString("detail", link.Detail);
        if (timerJson is not null)
        {
            w.WritePropertyName("timer");
            w.WriteRawValue(timerJson);
        }
        w.WriteEndObject();
    }

    public static void WritePeer(Utf8JsonWriter w, PeerView p)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(p);
        w.WriteStartObject();
        w.WriteString("t", p.Kind);
        w.WriteString("id", p.Id);
        w.WriteString("name", p.Name);
        w.WriteString("light", p.Light);
        w.WriteString("headline", p.Headline);
        w.WriteBoolean("live", p.Live);
        w.WriteEndObject();
    }

    internal static string Seconds(double s) => s.ToString("0.#", CultureInfo.InvariantCulture);
}
