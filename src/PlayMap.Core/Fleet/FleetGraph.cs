using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;

namespace PlayMap.Core.Fleet;

/// <summary>A lens over the board: everything, one kind of thing with its machines, the machines in a sync group or a backup pair,
/// or the problems and what they touch.</summary>
public enum FleetLens
{
    All,
    Outputs,
    Projectors,
    Sync,
    Backup,
    Problems,
}

/// <summary>One machine on the board: its id and name, how recently it was heard, its last health (null before the first), and the
/// words for the link to it. This node is a machine too (heard live, always).</summary>
public sealed record FleetMachine(string Id, string Name, FleetHeard Heard, HealthRecord? Health, string Words, bool Self = false)
{
    /// <summary>Its certificate was refused: red with the words, whatever its last record said.</summary>
    public bool Refused { get; init; }

    /// <summary>Seconds since its last record (0 for this node).</summary>
    public double SilentS { get; init; }
}

/// <summary>A thing on the board, in Patterns' Eye vocabulary: id, kind, plane, tier, label, the state in a line, the light and the facts
/// as words.</summary>
public sealed record FleetNode(string Id, string Kind, string Plane, int Tier, string Label, string Sub, Light Light, ImmutableArray<string> Words,
    string Machine)
{
    public bool IsProblem => Light is Light.Red or Light.Amber;
}

/// <summary>A link of the board, one of the Eye's eleven verbs, with its light and words.</summary>
public sealed record FleetEdge(string From, string To, string Kind, Light Light, string Words = "");

/// <summary>Where a thing sits on the deterministic grid.</summary>
public readonly record struct FleetPlace(double X, double Y, double W, double H);

/// <summary>The fleet as one graph (Patterns' Eye method, pure): a machine per node this one hears (and itself), each output it shows,
/// each projector an output drives with its device's facts folded in; backups mirror their primary and followers follow their
/// leader. Lights by evidence — a machine not heard for 6 s is red "gone quiet" and never leaves silently; its things go grey. The
/// same facts give the same ids, the same order and the same places. Headline, counts and the problems queue (reds, then ambers;
/// pictures first, then by tier and label) are computed once.</summary>
public sealed class FleetGraph
{
    public const double CellW = 180, CellH = 64, NodeW = 160, NodeH = 48;

    private readonly Dictionary<string, FleetNode> _byId;
    private readonly Dictionary<string, List<string>> _adjacent;
    private readonly Dictionary<string, FleetPlace> _places;

    private FleetGraph(List<FleetNode> nodes, List<FleetEdge> edges, Dictionary<string, FleetPlace> places, List<FleetMachine> machines)
    {
        Nodes = nodes;
        Edges = edges;
        Machines = machines;
        _places = places;
        _byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _adjacent = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (!_byId.ContainsKey(e.From) || !_byId.ContainsKey(e.To))
                continue;
            _adjacent[e.From].Add(e.To);
            _adjacent[e.To].Add(e.From);
        }
        Red = nodes.Count(n => n.Light == Light.Red);
        Amber = nodes.Count(n => n.Light == Light.Amber);
        Green = nodes.Count(n => n.Light == Light.Green);
        Grey = nodes.Count - Red - Amber - Green;
        Problems = nodes.Where(n => n.Light == Light.Red).OrderBy(PlaneOrder).ThenBy(n => n.Tier).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Concat(nodes.Where(n => n.Light == Light.Amber).OrderBy(PlaneOrder).ThenBy(n => n.Tier).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Id, StringComparer.Ordinal))
            .Select(n => n.Id).ToImmutableArray();
        Worst = Problems.Length > 0 ? _byId[Problems[0]] : null;
        Headline = Words();
    }

    public IReadOnlyList<FleetNode> Nodes { get; }
    public IReadOnlyList<FleetEdge> Edges { get; }
    public IReadOnlyList<FleetMachine> Machines { get; }
    public int Red { get; }
    public int Amber { get; }
    public int Green { get; }
    public int Grey { get; }

    /// <summary>The reds, then the ambers — what NEXT and PREV walk.</summary>
    public ImmutableArray<string> Problems { get; }

    public FleetNode? Worst { get; }

    public Light WorstLight => Red > 0 ? Light.Red : Amber > 0 ? Light.Amber : Green > 0 ? Light.Green : Light.Grey;

    public string WorstWords => Worst is { } w ? $"{w.Label}: {w.Sub}" : "";

    /// <summary>"3 machines · 5 outputs · 1 red — FOH-B: gone quiet", or "3 machines · 5 outputs · all green".</summary>
    public string Headline { get; }

    public FleetNode? Find(string id) => _byId.GetValueOrDefault(id);

    public FleetPlace PlaceOf(string id) => _places.GetValueOrDefault(id);

    public IReadOnlyList<string> Neighbours(string id) => _adjacent.TryGetValue(id, out var l) ? l : [];

    /// <summary>The graph of a fleet. Machines are drawn in (role, name, id) order, this node among them by its own role.</summary>
    public static FleetGraph Build(IReadOnlyList<FleetMachine> machines)
    {
        ArgumentNullException.ThrowIfNull(machines);
        var ordered = machines.GroupBy(m => m.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(m => RoleOrder(m.Health?.Facts.Role)).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        var nodes = new List<FleetNode>();
        var edges = new List<FleetEdge>();
        var places = new Dictionary<string, FleetPlace>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var row = 0;
        foreach (var m in ordered)
        {
            var machineId = MachineId(m.Id);
            var (light, sub) = MachineLight(m);
            var f = m.Health?.Facts;
            var words = ImmutableArray.CreateBuilder<string>();
            if (m.Words.Length > 0)
                words.Add(m.Words);
            if (f is not null)
            {
                words.Add(Say($"{RoleWord(f.Role)}{(f.Epoch > 0 ? $" · epoch {f.Epoch}" : "")} · build {f.Build}"));
                foreach (var c in m.Health!.Categories.Where(c => c.Light is Light.Red or Light.Amber))
                    words.Add($"{c.Name}: {c.Words}");
            }
            if (ids.Add(machineId))
            {
                nodes.Add(new FleetNode(machineId, "machine", "video", 1, m.Name, sub, light, words.ToImmutable(), m.Id));
                places[machineId] = Cell(0, row, 0);
            }
            if (f is null)
            {
                row++;
                continue;
            }
            var live = m.Heard == FleetHeard.Live && !m.Refused;
            var col = 1;
            var outputIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var o in f.Outputs)
            {
                var id = $"output:{m.Id}:{o.Id}";
                if (!ids.Add(id))
                    continue;
                var ol = live ? m.Health!.LightOf(o) : Light.Grey;
                var osub = live ? OutputWords(o, ol) : Say($"not heard for {m.SilentS:0} s");
                nodes.Add(new FleetNode(id, "output", "video", 2, o.Name.Length > 0 ? o.Name : o.Id, osub, ol,
                    [Say($"{m.Name}: output {o.Id}"), o.Path.Length > 0 ? "path " + o.Path : "path unknown"], m.Id));
                places[id] = Cell(col, row, 0);
                outputIds[o.Id] = id;
                edges.Add(new FleetEdge(machineId, id, "shows", ol));
                col++;
            }
            var spare = col;   // a projector no output drives sits after the outputs' columns
            foreach (var p in f.Projectors)
            {
                var id = $"projector:{m.Id}:{p.Id}";
                if (!ids.Add(id))
                    continue;
                var pl = live ? p.Light : Light.Grey;
                nodes.Add(new FleetNode(id, "projector", "video", 3, p.Name.Length > 0 ? p.Name : p.Id, live ? p.Words : Say($"not heard for {m.SilentS:0} s"),
                    pl, [Say($"{m.Name}: projector {p.Id}")], m.Id));
                var above = outputIds.TryGetValue(p.Output, out var driver) ? places[driver] : Cell(spare++, row, 0);
                places[id] = new FleetPlace(above.X, above.Y + NodeH + 8, NodeW, NodeH / 2);   // on the projectors' line, inside the row
                if (driver is not null)
                    edges.Add(new FleetEdge(driver, id, "drives", pl));
            }
            row++;
        }
        // Between machines: a backup mirrors its primary; a follower follows its leader.
        foreach (var m in ordered)
        {
            var f = m.Health?.Facts;
            if (f is null)
                continue;
            var (light, _) = MachineLight(m);
            if (f.Of.Length > 0 && ids.Contains(MachineId(f.Of)))
                edges.Add(new FleetEdge(MachineId(m.Id), MachineId(f.Of), "mirrors", light, "backs up"));
            if (f.SyncRole == "follower" && f.SyncLeader.Length > 0 && ids.Contains(MachineId(f.SyncLeader)))
                edges.Add(new FleetEdge(MachineId(m.Id), MachineId(f.SyncLeader), "follows", light, "takes its clock"));
        }
        return new FleetGraph(nodes, edges, places, ordered);
    }

    public static string MachineId(string nodeId) => "machine:" + nodeId;

    /// <summary>The word that names this node's own machine on its board (a key that is the same on every node).</summary>
    public const string SelfWord = "self";

    /// <summary>A machine's own light: refused red, gone red "gone quiet", stale amber "quiet", no record yet grey, else its health's.</summary>
    public static (Light Light, string Sub) MachineLight(FleetMachine m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.Refused)
            return (Light.Red, m.Words);
        return m.Heard switch
        {
            FleetHeard.Gone => (Light.Red, m.Health is null ? "gone quiet before its health was heard" : Say($"gone quiet {m.SilentS:0} s")),
            FleetHeard.Stale => (Light.Amber, Say($"quiet {m.SilentS:0} s")),
            _ when m.Health is null => (Light.Grey, m.Words.Length > 0 ? m.Words : "not heard yet"),
            _ => (m.Health.Light, m.Health.Headline),
        };
    }

    /// <summary>Whether a thing is on a lens: everything; one kind with its machines; the machines in a sync group or a backup pair; the
    /// problems and what they touch.</summary>
    public bool Visible(FleetNode n, FleetLens lens)
    {
        ArgumentNullException.ThrowIfNull(n);
        return lens switch
        {
            FleetLens.Outputs => n.Kind is "machine" or "output",
            FleetLens.Projectors => n.Kind is "machine" or "projector",
            FleetLens.Sync => n.Kind == "machine" && Edges.Any(e => e.Kind == "follows" && (e.From == n.Id || e.To == n.Id)),
            FleetLens.Backup => n.Kind == "machine" && Edges.Any(e => e.Kind == "mirrors" && (e.From == n.Id || e.To == n.Id)),
            FleetLens.Problems => n.IsProblem || Neighbours(n.Id).Any(id => _byId.TryGetValue(id, out var o) && o.IsProblem),
            _ => true,
        };
    }

    public static FleetLens? ParseLens(string word) => (word ?? "").Trim().ToUpperInvariant() switch
    {
        "ALL" => FleetLens.All,
        "OUTPUTS" => FleetLens.Outputs,
        "PROJECTORS" => FleetLens.Projectors,
        "SYNC" => FleetLens.Sync,
        "BACKUP" => FleetLens.Backup,
        "PROBLEMS" => FleetLens.Problems,
        _ => null,
    };

    public static string LensWord(FleetLens lens) => lens.ToString().ToLowerInvariant();

    /// <summary>How far every thing is from the focus, in hops (a thing not reached is absent).</summary>
    public IReadOnlyDictionary<string, int> Hops(string focusId)
    {
        var hops = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!_byId.ContainsKey(focusId))
            return hops;
        var queue = new Queue<string>();
        hops[focusId] = 0;
        queue.Enqueue(focusId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var n in _adjacent[id])
            {
                if (hops.ContainsKey(n))
                    continue;
                hops[n] = hops[id] + 1;
                queue.Enqueue(n);
            }
        }
        return hops;
    }

    /// <summary>Full for the focus and what it links to, 0.55 two hops out, 0.25 for the rest (present, never hidden); 1 with no focus.</summary>
    public static double Emphasis(string id, IReadOnlyDictionary<string, int>? hops) =>
        hops is null ? 1.0 : !hops.TryGetValue(id, out var h) ? 0.25 : h <= 1 ? 1.0 : h == 2 ? 0.55 : 0.25;

    /// <summary>The thing some words name: an id as it is, "self" for this node's machine, a machine's node id or name, an output's id,
    /// a label whole, then a label containing the words — problems first. Null when nothing answers.</summary>
    public string? Resolve(string words)
    {
        var w = (words ?? "").Trim();
        if (w.Length == 0)
            return null;
        if (w.Equals(SelfWord, StringComparison.OrdinalIgnoreCase))
            return Machines.FirstOrDefault(m => m.Self) is { } self ? MachineId(self.Id) : null;
        if (Nodes.FirstOrDefault(n => n.Id.Equals(w, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact.Id;
        if (_byId.ContainsKey(MachineId(w)))
            return MachineId(w);
        var ordered = Nodes.OrderBy(n => n.IsProblem ? 0 : 1).ThenBy(n => n.Tier).ToList();
        if (ordered.FirstOrDefault(n => n.Kind == "output" && n.Id.EndsWith(":" + w, StringComparison.OrdinalIgnoreCase)) is { } output)
            return output.Id;
        if (ordered.FirstOrDefault(n => n.Label.Equals(w, StringComparison.OrdinalIgnoreCase)) is { } whole)
            return whole.Id;
        return ordered.FirstOrDefault(n => n.Label.Contains(w, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>The problem after the current one (the first when none, or the current is not a problem); null when there are none.</summary>
    public string? Next(string? currentId)
    {
        if (Problems.Length == 0)
            return null;
        var i = currentId is null ? -1 : Problems.IndexOf(currentId);
        return Problems[(i + 1) % Problems.Length];
    }

    public string? Prev(string? currentId)
    {
        if (Problems.Length == 0)
            return null;
        var i = currentId is null ? 0 : Math.Max(0, Problems.IndexOf(currentId));
        return Problems[(i - 1 + Problems.Length) % Problems.Length];
    }

    private static FleetPlace Cell(int col, int row, double dy) => new(col * CellW, row * CellH * 2 + dy, NodeW, NodeH);

    private static int PlaneOrder(FleetNode n) => n.Plane == "video" ? 0 : 1;

    private static int RoleOrder(string? role) => role switch
    {
        "primary" or "leader" => 0,
        "solo" or null or "" => 1,
        "follower" => 2,
        "backup" => 3,
        _ => 4,
    };

    private static string RoleWord(string role) => role switch
    {
        "primary" => "primary",
        "backup" => "backup",
        "fenced" => "fenced",
        _ => "solo",
    };

    private static string OutputWords(HealthOutput o, Light light) => light switch
    {
        Light.Red => "not open",
        Light.Amber => Say($"{o.Fps:0} fps · {o.Dropped} dropped · {o.Missed60s} missed in a minute"),
        Light.Grey => "not measured yet",
        _ => Say($"{o.Fps:0} fps"),
    };

    private string Words()
    {
        var machines = Nodes.Count(n => n.Kind == "machine");
        var outputs = Nodes.Count(n => n.Kind == "output");
        var lead = Say($"{machines} machine{(machines == 1 ? "" : "s")} · {outputs} output{(outputs == 1 ? "" : "s")}");
        if (Worst is null)
            return Grey > 0 && Green == 0 ? lead + " · nothing measured yet" : lead + " · all green";
        var counts = new List<string>();
        if (Red > 0)
            counts.Add(Say($"{Red} red"));
        if (Amber > 0)
            counts.Add(Say($"{Amber} amber"));
        var owner = Worst.Kind == "machine" ? "" : (Machines.FirstOrDefault(m => m.Id == Worst.Machine)?.Name is { Length: > 0 } name ? name + ": " : "");
        return $"{lead} · {string.Join(" · ", counts)} — {owner}{Worst.Label}: {Worst.Sub}";
    }

    private static string Say(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The board on the wire: FLEET / EYE answer it in Patterns' <c>EyeJson</c> field names (headline, worst, worstLight,
/// worstWords, counts, focus, lens, problems, nodes with their places and the line that focuses each, edges) plus <c>machines</c>, each
/// machine's health in short; STATE carries the <c>eye</c> row.</summary>
public static class FleetJson
{
    public static string Board(FleetGraph g, string focus, FleetLens lens)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
            WriteBoard(w, g, focus, lens);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteBoard(Utf8JsonWriter w, FleetGraph g, string focus, FleetLens lens)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(g);
        var hops = focus.Length > 0 && g.Find(focus) is not null ? g.Hops(focus) : null;
        w.WriteStartObject();
        WriteSummary(w, g, focus, lens);
        w.WriteStartArray("nodes");
        foreach (var n in g.Nodes)
        {
            var p = g.PlaceOf(n.Id);
            w.WriteStartObject();
            w.WriteString("id", n.Id);
            w.WriteString("kind", n.Kind);
            w.WriteString("plane", n.Plane);
            w.WriteNumber("tier", n.Tier);
            w.WriteString("label", n.Label);
            w.WriteString("sub", n.Sub);
            w.WriteString("light", ShowContext.LightWord(n.Light));
            w.WriteNumber("x", Math.Round(p.X));
            w.WriteNumber("y", Math.Round(p.Y));
            w.WriteNumber("w", Math.Round(p.W));
            w.WriteNumber("h", Math.Round(p.H));
            w.WriteStartArray("words");
            foreach (var s in n.Words)
                w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteString("page", "fleet");
            w.WriteString("item", n.Machine);
            w.WriteString("wire", "EYE FOCUS " + n.Id);
            w.WriteBoolean("visible", g.Visible(n, lens));
            w.WriteNumber("emphasis", FleetGraph.Emphasis(n.Id, hops));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("edges");
        foreach (var e in g.Edges)
        {
            w.WriteStartObject();
            w.WriteString("from", e.From);
            w.WriteString("to", e.To);
            w.WriteString("kind", e.Kind);
            w.WriteString("light", ShowContext.LightWord(e.Light));
            w.WriteString("words", e.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("replay", false);
        w.WriteString("replayAt", "");
        w.WriteString("moment", "");
        w.WriteStartArray("machines");
        foreach (var m in g.Machines)
        {
            var (light, sub) = FleetGraph.MachineLight(m);
            var f = m.Health?.Facts;
            w.WriteStartObject();
            w.WriteString("id", m.Id);
            w.WriteString("name", m.Name);
            w.WriteString("role", f?.Role ?? "solo");
            w.WriteString("light", ShowContext.LightWord(light));
            w.WriteString("headline", sub);
            w.WriteString("heard", FleetPeer.HeardWord(m.Heard));
            w.WriteBoolean("self", m.Self);
            w.WriteStartArray("outputs");
            foreach (var o in f?.Outputs ?? [])
                w.WriteStringValue($"output:{m.Id}:{o.Id}");
            w.WriteEndArray();
            w.WriteStartArray("projectors");
            foreach (var p in f?.Projectors ?? [])
                w.WriteStringValue($"projector:{m.Id}:{p.Id}");
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>STATE.eye: the board's summary row (headline, worst, counts, problems, focus, lens).</summary>
    public static void WriteEye(Utf8JsonWriter w, FleetGraph g, string focus, FleetLens lens)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(g);
        w.WriteStartObject();
        WriteSummary(w, g, focus, lens);
        w.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter w, FleetGraph g, string focus, FleetLens lens)
    {
        w.WriteString("headline", g.Headline);
        w.WriteString("worst", g.Worst?.Id ?? "");
        w.WriteString("worstLight", ShowContext.LightWord(g.WorstLight));
        w.WriteString("worstWords", g.WorstWords);
        w.WriteStartObject("counts");
        w.WriteNumber("green", g.Green);
        w.WriteNumber("amber", g.Amber);
        w.WriteNumber("red", g.Red);
        w.WriteNumber("grey", g.Grey);
        w.WriteNumber("things", g.Nodes.Count);
        w.WriteNumber("links", g.Edges.Count);
        w.WriteEndObject();
        w.WriteString("focus", focus);
        w.WriteString("lens", FleetGraph.LensWord(lens));
        w.WriteStartArray("problems");
        foreach (var p in g.Problems)
            w.WriteStringValue(p);
        w.WriteEndArray();
    }
}
