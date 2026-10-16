using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Playback;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>The demo engine's scan (simulated in time, with the shape a real scan reports) and its STATE,
/// written key for key in docs/WIRE.md's order.</summary>
public sealed partial class FakeEngineLink
{
    /// <summary>A scan in progress: which camera, which projectors, how long the simulation runs.</summary>
    private sealed record FakeScan(string Id, string CameraId, IReadOnlyList<Projector> Projectors, string Mode, long StartNs, long DurationNs)
    {
        public string State { get; set; } = "running";

        public bool Recorded { get; set; }

        public bool Applied { get; set; }

        public int PatternsPerProjector => Mode switch { "fast" => 22, "difficult" => 72, _ => 44 };

        public double Quality => Mode switch { "fast" => 0.81, "difficult" => 0.95, _ => 0.92 };
    }

    private static readonly (string Id, string Label, double X, double Y, double W, double H, double Confidence)[] DemoRegions =
    [
        ("rg_wall", "wall", 0.06, 0.08, 0.88, 0.62, 0.97),
        ("rg_cube", "cube", 0.17, 0.52, 0.2, 0.3, 0.91),
        ("rg_column", "column", 0.68, 0.22, 0.08, 0.62, 0.88),
        ("rg_floor", "floor", 0.0, 0.8, 1.0, 0.2, 0.74),
    ];

    private WireReply ScanStart(WireLine line)
    {
        if (_scan is { State: "running" })
            return Err("a scan is running — SCAN CANCEL stops it");
        var cameraId = line.Word(2);
        var project = _store.Project;
        if (project.Stage.Cameras.Find(cameraId) is null)
            return Err($"no camera '{cameraId}' — pick one in step 1");
        var mode = "robust";
        var projectors = new List<Projector>();
        for (var i = 3; i < line.Count; i++)
        {
            if (line.WordUpper(i) == "MODE")
            {
                mode = line.Word(i + 1).ToLowerInvariant();
                break;
            }
            var pj = project.FindProjector(line.Word(i));
            if (pj is null)
                return Err($"no projector '{line.Word(i)}'");
            projectors.Add(pj);
        }
        if (mode is not ("fast" or "robust" or "difficult"))
            return Err("MODE is FAST, ROBUST or DIFFICULT");
        if (projectors.Count == 0)
            projectors.AddRange(project.Stage.Projectors);
        if (projectors.Count == 0)
            return Err("no projector to scan with — add one first");
        var seconds = mode switch { "fast" => 6, "difficult" => 20, _ => 12 };
        var id = project.NewId(Ids.Scan);
        _scan = new FakeScan(id, cameraId, projectors, mode, Now, seconds * DemoProject.Second * projectors.Count / 2 + DemoProject.Second);
        return Ok($"{{\"stage\":\"accepted\",\"effect\":\"scanning {projectors.Count} projector(s), {mode}\",\"scan\":\"{id}\"}}");
    }

    private WireReply ScanCancel()
    {
        if (_scan is not { State: "running" } s)
            return Err("no scan is running");
        s.State = "cancelled";
        return Ok("scan cancelled — outputs back to the show");
    }

    private WireReply ScanApply(WireLine line)
    {
        var id = line.Word(2);
        if (_scan is not { } s || s.Id != id)
            return _store.Project.Stage.Scans.Find(id) is null ? Err($"no scan '{id}'") : Err("that scan was applied when it was taken");
        if (s.State != "done")
            return Err("the scan has not finished");
        if (s.Applied)
            return Err("applied already");
        var ids = s.Projectors.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var r = Edit("apply scan", p => p.WithStage(st => st with
        {
            Projectors = st.Projectors.Select(j => ids.Contains(j.Id) ? j with { SolveResidualPx = 0.6 } : j).ToImmutableArrayFast(),
        }));
        if (r.Ok)
            s.Applied = true;
        return r.Ok ? Ok($"{s.Projectors.Count} projector(s) solved from the scan") : r;
    }

    private WireReply ScanSurface(WireLine line)
    {
        // SCAN SURFACE <scan> <x> <y> [<name>] — x, y in smart-scan units (0..1).
        var id = line.Word(2);
        if (_scan is not { State: "done" } s || s.Id != id)
            return Err($"no finished scan '{id}'");
        if (!double.TryParse(line.Word(3), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(line.Word(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return Err("SCAN SURFACE <scan> <x> <y> [<name>]");
        var hit = DemoRegions.Where(g => x >= g.X && y >= g.Y && x < g.X + g.W && y < g.Y + g.H).OrderBy(g => g.W * g.H).FirstOrDefault();
        if (hit.Id is null)
            return Err("nothing was found there — click inside a coloured region");
        var name = line.Count > 5 ? line.Word(5) : char.ToUpperInvariant(hit.Label[0]) + hit.Label[1..];
        var project = _store.Project;
        var meshId = project.NewId(Ids.Mesh);
        var surfaceId = project.NewId(Ids.Surface);
        var mesh = new MeshAsset(meshId, name + " (scan)", MeshOrigin.Primitive, "", "", "plane",
            ParamSet.Empty.With("width", Math.Round(hit.W * 4, 2)).With("height", Math.Round(hit.H * 2.5, 2)), 4, 2);
        var surface = new Surface(surfaceId, name, meshId,
            new Transform3(new Vec3d((hit.X + hit.W / 2 - 0.5) * 4, (1 - hit.Y - hit.H / 2) * 2.5, 0.02), Vec3d.Zero, new Vec3d(1, 1, 1)), [],
            SurfaceContent.Empty, 1, [], true, false, "#CE93D8", [hit.Label]);
        return Edit("surface from scan", p => p.WithStage(st => st with { Meshes = st.Meshes.Add(mesh), Surfaces = st.Surfaces.Add(surface) }), okText: surfaceId);
    }

    private bool TickScan()
    {
        if (_scan is not { State: "running" } s)
            return false;
        if (Now - s.StartNs >= s.DurationNs)
        {
            s.State = "done";
            if (!s.Recorded)
            {
                s.Recorded = true;
                var record = new ScanRecord(s.Id, "Scan " + _store.Project.Stage.Scans.Length.ToString(CultureInfo.InvariantCulture), ScanKind.StructuredLight,
                    1_760_000_000_000 + Now / 1_000_000, s.CameraId, [.. s.Projectors.Select(p => p.Id)], "scans/" + s.Id, null, 0.7,
                    120_000 * s.Projectors.Count, s.Mode + " mode");
                _store.Apply("scan " + s.Id, p => p.WithStage(st => st with { Scans = st.Scans.Add(record) }));
            }
        }
        return true;
    }

    private string BuildState()
    {
        var project = _store.Project;
        var now = Now;
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteNumber("seq", _stateSeq);
            w.WriteString("id", NodeId);
            w.WriteString("t", "playmap");
            w.WriteString("name", NodeName);
            w.WriteString("version", "0.1.0");
            w.WriteNumber("at", 1_760_000_000_000 + now / 1_000_000);
            var focused = _focused is null ? null : project.FindPlaylist(_focused);
            var fc = focused is null ? null : CursorOf(focused.Id);
            var outputsOpen = project.Outputs.Count(o => o.Enabled);
            w.WriteString("light", _blackout ? "amber" : project.Media.Any(m => Availability(m).State == "missing") ? "amber" : "green");
            w.WriteString("headline", Headline(focused, fc, outputsOpen, now));
            w.WriteStartObject("air");
            w.WriteBoolean("blackout", _blackout);
            w.WriteBoolean("frozen", _frozen);
            // The engine's shape: the kind and the outputs it covers (empty = all).
            if (_pattern is null)
                w.WriteNull("pattern");
            else
            {
                w.WriteStartObject("pattern");
                w.WriteString("kind", _pattern);
                w.WriteStartArray("outputs");
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteBoolean("identify", _identifyUntilNs != 0);
            w.WriteEndObject();
            WriteVideo(w, focused, fc, now);
            w.WriteStartArray("playlists");
            foreach (var pl in project.Playlists)
                WritePlaylist(w, pl, CursorOf(pl.Id), pl.Id == _focused, now);
            w.WriteEndArray();
            w.WriteStartArray("outputs");
            foreach (var o in project.Outputs)
            {
                w.WriteStartObject();
                w.WriteString("id", o.Id);
                w.WriteString("name", o.Name);
                w.WriteString("mode", VerbText.EnumWord(o.Mode));
                w.WriteString("display", o.DisplayKey);
                w.WriteNumber("width", o.Width);
                w.WriteNumber("height", o.Height);
                w.WriteNumber("refreshHz", o.RefreshHz);
                w.WriteBoolean("open", o.Enabled);
                w.WriteNumber("fps", o.Enabled ? o.RefreshHz : 0);
                w.WriteNumber("dropped", 0);
                w.WriteNumber("frameMsP95", o.Enabled ? 4.1 : 0);
                var pat = _outputPatterns.TryGetValue(o.Id, out var p1) ? p1 : _pattern;
                if (pat is null)
                    w.WriteNull("pattern");
                else
                    w.WriteString("pattern", pat);
                w.WriteString("light", !o.Enabled ? "grey" : o.DisplayKey.Length == 0 && o.Kind == OutputKind.Display ? "amber" : "green");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("media");
            w.WriteNumber("items", project.Media.Length);
            w.WriteNumber("missing", project.Media.Count(m => Availability(m).State == "missing"));
            w.WriteBoolean("ready", project.Media.All(m => Availability(m).State == "ready"));
            w.WriteStartArray("transfers");
            foreach (var t in _transfers.Values)
            {
                w.WriteStartObject();
                w.WriteString("media", t.MediaId);
                w.WriteString("name", t.Name);
                w.WriteNumber("progress", Math.Round(Math.Clamp((now - t.StartNs) / (8.0 * DemoProject.Second), 0, 1), 3));
                w.WriteString("from", t.From);
                w.WriteString("state", "copying");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("stage");
            w.WriteNumber("surfaces", project.Stage.Surfaces.Length);
            w.WriteNumber("meshes", project.Stage.Meshes.Length);
            w.WriteStartArray("projectors");
            foreach (var pj in project.Stage.Projectors)
            {
                w.WriteStartObject();
                w.WriteString("id", pj.Id);
                w.WriteString("name", pj.Name);
                w.WriteBoolean("solved", pj.SolveResidualPx > 0);
                w.WriteNumber("residualPx", pj.SolveResidualPx);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("scans", project.Stage.Scans.Length);
            w.WriteEndObject();
            WriteScan(w, now);
            w.WriteStartObject("sync");
            w.WriteString("role", _syncLead is null ? "follower" : "leader");
            if (_syncLead is null)
                w.WriteString("leader", "9f8e7d6c");
            else
                w.WriteNull("leader");
            w.WriteNumber("offsetMs", _syncLead is null ? 0.21 : 0);
            w.WriteNumber("rttMs", 0.35);
            w.WriteNumber("driftPpm", 3.2);
            w.WriteStartArray("groups");
            foreach (var g in project.Playlists.Select(p => p.SyncGroup).Append(_syncLead).Where(g => g is not null).Distinct(StringComparer.Ordinal))
                w.WriteStringValue(g);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("links");
            w.WriteStartObject("patterns");
            w.WriteString("state", _patternsHost.Length == 0 ? "off" : "live");
            w.WriteString("host", _patternsHost);
            w.WriteBoolean("paired", _patternsHost.Length > 0);
            w.WriteString("clip", _patternsHost.Length == 0 ? "" : "VT 2:28");
            w.WriteEndObject();
            w.WriteStartObject("stagewatch");
            w.WriteString("state", _stagewatchHost.Length == 0 ? "off" : "live");
            w.WriteString("host", _stagewatchHost);
            if (_stagewatchHost.Length > 0)
            {
                var left = Math.Max(0, 750 - now / DemoProject.Second % 750);
                w.WriteStartObject("timer");
                w.WriteString("state", "running");
                w.WriteString("text", VerbText.Clock(left * 1000));
                w.WriteNumber("seconds", left);
                w.WriteString("phase", left < 120 ? "warning" : "normal");
                w.WriteString("color", left < 120 ? "#F59E0B" : "#22C55E");
                w.WriteString("title", "Keynote");
                w.WriteString("message", "");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartArray("peers");
            WritePeer(w, "patterns", "f0e1d2c3", "FOH", "green", true, "Show running · cue 14");
            WritePeer(w, "main", "a9b8c7d6", "Stagewatch", "green", true, "Keynote 12:30 left");
            WritePeer(w, "playmap", "9f8e7d6c", "Stage right", "amber", true, "1 file missing · synced");
            w.WriteEndArray();
            w.WriteStartObject("machine");
            w.WriteString("renderer", "Demo (no renderer)");
            w.WriteNumber("cpuPct", 3);
            w.WriteNumber("memMb", 180);
            w.WriteNumber("gcPauseMsMax", 0.2);
            w.WriteEndObject();
            w.WriteStartObject("project");
            w.WriteString("id", project.Id);
            w.WriteString("name", project.Name);
            w.WriteNumber("revision", _store.Current.Revision);
            w.WriteString("path", "/shows/demo/" + project.Name + ".playmap");
            w.WriteBoolean("dirty", _store.Current.Revision != _savedRevision);
            w.WriteEndObject();
            WriteAi(w);
            w.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return _sections.Count == 0 ? json : MergeSections(json);
    }

    /// <summary>The fake's own STATE with the sections laid over it (a fixture's or a hook's), merged the way a node
    /// grows STATE: objects merge key by key, an array of objects with ids merges element by element (by id, new ones
    /// appended), anything else is replaced.</summary>
    private string MergeSections(string json)
    {
        var root = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var (key, node) in _sections)
            root[key] = Merge(root[key], node);
        return root.ToJsonString();
    }

    private static System.Text.Json.Nodes.JsonNode? Merge(System.Text.Json.Nodes.JsonNode? into, System.Text.Json.Nodes.JsonNode? over)
    {
        switch (into, over)
        {
            case (System.Text.Json.Nodes.JsonObject a, System.Text.Json.Nodes.JsonObject b):
                var merged = (System.Text.Json.Nodes.JsonObject)a.DeepClone();
                foreach (var (k, v) in b)
                    merged[k] = Merge(merged[k], v);
                return merged;
            case (System.Text.Json.Nodes.JsonArray a, System.Text.Json.Nodes.JsonArray b) when b.All(IdOf) && a.All(IdOf):
                var list = (System.Text.Json.Nodes.JsonArray)a.DeepClone();
                foreach (var e in b)
                {
                    var id = e!["id"]!.GetValue<string>();
                    var at = list.Select((x, i) => (x, i)).FirstOrDefault(t => t.x!["id"]!.GetValue<string>() == id, (null, -1)).Item2;
                    if (at < 0)
                        list.Add(e.DeepClone());
                    else
                        list[at] = Merge(list[at], e);
                }
                return list;
            default:
                return over?.DeepClone();
        }
    }

    private static bool IdOf(System.Text.Json.Nodes.JsonNode? e) =>
        e is System.Text.Json.Nodes.JsonObject o && o["id"] is System.Text.Json.Nodes.JsonValue v && v.GetValueKind() == JsonValueKind.String;

    private string Headline(Playlist? focused, PlaylistCursor? cursor, int outputsOpen, long now)
    {
        if (_blackout)
            return "Blacked out · " + outputsOpen.ToString(CultureInfo.InvariantCulture) + " outputs";
        var sb = new StringBuilder();
        if (focused is not null && cursor is { IsIdle: false } c && c.Index < focused.Items.Length)
        {
            sb.Append(focused.Name).Append(" · ").Append(focused.Items[c.Index].Label);
            if (c.Clip.RemainingAt(now) is { } left)
                sb.Append(' ').Append(VerbText.Clock(left / 1_000_000, countdown: true)).Append(" left");
        }
        else
        {
            sb.Append("Idle");
        }
        sb.Append(" · ").Append(outputsOpen.ToString(CultureInfo.InvariantCulture)).Append(" outputs open");
        return sb.ToString();
    }

    private void WriteVideo(Utf8JsonWriter w, Playlist? focused, PlaylistCursor? cursor, long now)
    {
        if (focused is null || cursor is null || cursor.IsIdle || cursor.Index >= focused.Items.Length)
        {
            w.WriteNull("video");
            return;
        }
        var item = focused.Items[cursor.Index];
        var clip = cursor.Clip;
        var pos = clip.PositionAt(now) - clip.InNs;
        var len = clip.LengthNs;
        var rem = Math.Max(0, len - pos);
        var file = item.Source.Kind == SourceKind.Media ? _store.Project.FindMedia(item.Source.Id)?.Name ?? item.Label : item.Label;
        w.WriteStartObject("video");
        w.WriteString("file", file);
        w.WriteString("role", "playlist");
        w.WriteString("tag", focused.Name);
        w.WriteNumber("position", pos / 1_000_000_000);
        w.WriteNumber("length", len / 1_000_000_000);
        w.WriteNumber("remaining", rem / 1_000_000_000);
        w.WriteString("positionText", VerbText.Clock(pos / 1_000_000));
        w.WriteString("lengthText", VerbText.Clock(len / 1_000_000));
        w.WriteString("remainingText", VerbText.Clock(rem / 1_000_000, countdown: true));
        w.WriteString("text", $"{focused.Name} {file} · {VerbText.Clock(pos / 1_000_000)} / {VerbText.Clock(len / 1_000_000)} · {VerbText.Clock(rem / 1_000_000, true)} left");
        w.WriteString("chip", focused.Name + " " + VerbText.Clock(rem / 1_000_000, countdown: true));
        w.WriteBoolean("playing", clip.State == TransportState.Playing);
        w.WriteBoolean("ended", clip.State == TransportState.Ended);
        w.WriteBoolean("loops", clip.Loop);
        w.WriteBoolean("out", false);
        w.WriteString("call", "");
        w.WriteString("key", item.Source.Id);
        w.WriteNumber("positionMs", pos / 1_000_000);
        w.WriteNumber("lengthMs", len / 1_000_000);
        w.WriteNumber("remainingMs", rem / 1_000_000);
        w.WriteBoolean("paused", clip.State is TransportState.Paused or TransportState.Cued);
        w.WriteNumber("rate", clip.Anchor.Rate);
        w.WriteBoolean("seekable", true);
        w.WriteNumber("seq", clip.Seq);
        w.WriteEndObject();
    }

    private static void WriteItemRef(Utf8JsonWriter w, string name, PlaylistItem? item)
    {
        if (item is null)
        {
            w.WriteNull(name);
            return;
        }
        w.WriteStartObject(name);
        w.WriteString("id", item.Id);
        w.WriteString("label", item.Label);
        w.WriteString("source", item.Source.ToString());
        w.WriteEndObject();
    }

    private void WritePlaylist(Utf8JsonWriter w, Playlist pl, PlaylistCursor cursor, bool focused, long now)
    {
        var item = !cursor.IsIdle && cursor.Index < pl.Items.Length ? pl.Items[cursor.Index] : null;
        var clip = cursor.Clip;
        var pos = item is null ? 0 : clip.PositionAt(now) - clip.InNs;
        var len = item is null ? 0 : clip.LengthNs;
        w.WriteStartObject();
        w.WriteString("id", pl.Id);
        w.WriteString("name", pl.Name);
        w.WriteBoolean("focused", focused);
        w.WriteString("state", item is null ? "empty" : clip.State.ToString().ToLowerInvariant());
        w.WriteNumber("index", item is null ? -1 : cursor.Index);
        WriteItemRef(w, "item", item);
        w.WriteNumber("positionMs", pos / 1_000_000);
        w.WriteNumber("lengthMs", len / 1_000_000);
        w.WriteNumber("remainingMs", Math.Max(0, len - pos) / 1_000_000);
        w.WriteNumber("rate", clip.Anchor.Rate);
        w.WriteBoolean("loop", pl.Loop);
        if (pl.SyncGroup is null)
            w.WriteNull("syncGroup");
        else
            w.WriteString("syncGroup", pl.SyncGroup);
        if (pl.SyncGroup is null)
            w.WriteNull("leader");
        else
            w.WriteString("leader", _syncLead == pl.SyncGroup ? NodeId : "9f8e7d6c");
        w.WriteNumber("seq", cursor.Seq);
        var nextIndex = item is null ? (pl.Items.Length > 0 ? 0 : -1) : PlaylistCursor.NextIndex(pl, cursor.Index);
        WriteItemRef(w, "next", nextIndex >= 0 && nextIndex != cursor.Index ? pl.Items[nextIndex] : null);
        w.WriteEndObject();
    }

    private void WriteScan(Utf8JsonWriter w, long now)
    {
        if (_scan is not { } s)
        {
            w.WriteNull("scan");
            return;
        }
        var progress = s.State == "done" ? 1 : Math.Clamp((now - s.StartNs) / (double)s.DurationNs, 0, 1);
        var total = s.PatternsPerProjector * s.Projectors.Count;
        w.WriteStartObject("scan");
        w.WriteString("id", s.Id);
        w.WriteString("state", s.State);
        w.WriteString("stage", s.State != "running" ? s.State : progress < 0.05 ? "measuring latency" : progress > 0.9 ? "decoding" : "projecting patterns");
        w.WriteNumber("pattern", (int)Math.Round(progress * total));
        w.WriteNumber("patterns", total);
        w.WriteNumber("progress", Math.Round(progress, 3));
        w.WriteNumber("quality", s.State == "done" ? s.Quality : 0);
        w.WriteString("qualityWords", s.State == "done"
            ? string.Create(CultureInfo.InvariantCulture, $"{(s.Quality > 0.9 ? "Good" : "Fair")} — {s.Quality * 100:0} % of the picture decoded, {DemoRegions.Length} regions found")
            : "");
        w.WriteString("message", s.State == "cancelled" ? "Cancelled — the outputs are back on the show" : "");
        w.WriteStartArray("projectors");
        for (var i = 0; i < s.Projectors.Count; i++)
        {
            var share = 1.0 / s.Projectors.Count;
            var p = Math.Clamp((progress - i * share) / share, 0, 1);
            w.WriteStartObject();
            w.WriteString("id", s.Projectors[i].Id);
            w.WriteString("name", s.Projectors[i].Name);
            w.WriteNumber("progress", Math.Round(p, 3));
            w.WriteString("state", p >= 1 ? "done" : p > 0 ? "scanning" : "waiting");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("regions");
        if (s.State == "done")
        {
            foreach (var g in DemoRegions)
            {
                w.WriteStartObject();
                w.WriteString("id", g.Id);
                w.WriteString("label", g.Label);
                w.WriteStartArray("rect");
                w.WriteNumberValue(g.X);
                w.WriteNumberValue(g.Y);
                w.WriteNumberValue(g.W);
                w.WriteNumberValue(g.H);
                w.WriteEndArray();
                w.WriteNumber("confidence", g.Confidence);
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WritePeer(Utf8JsonWriter w, string t, string id, string name, string light, bool live, string headline)
    {
        w.WriteStartObject();
        w.WriteString("t", t);
        w.WriteString("id", id);
        w.WriteString("name", name);
        w.WriteString("light", light);
        w.WriteBoolean("live", live);
        w.WriteString("headline", headline);
        w.WriteEndObject();
    }
}

internal static class ImmutableHelpers
{
    public static System.Collections.Immutable.ImmutableArray<T> ToImmutableArrayFast<T>(this IEnumerable<T> items) =>
        System.Collections.Immutable.ImmutableArray.CreateRange(items);
}
