using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;
using PlayMap.Engine.Led;

namespace PlayMap.Engine;

/// <summary>wp2-led's hooks into the engine (EngineCore.Modules.cs): the LED verbs, STATE.led, one context item per node, the check
/// of an LED entity an edit adds, the live air in the recovery record, and the INDEX section. Every mutating verb refuses a change
/// that changes nothing, with the reason; a node's state in STATE is what the sender observed, never assumed.</summary>
public sealed partial class EngineCore
{
    private const long LocateDefaultNs = 10_000_000_000;

    private LedBoard? _ledBoard;

    /// <summary>The engine's side of LED output.</summary>
    internal LedBoard Led => _ledBoard ??= new LedBoard(Services);

    partial void RouteLed(VerbRoutes routes)
    {
        routes.Lane("LED LIST", _ => LedList());
        routes.Lane("LED NEW", c => LedNew(c.Args));
        routes.Lane("LED PATCH", c => LedPatchVerb(c.Args));
        routes.Lane("LED AUTOPATCH", c => LedAutoPatch(c.Args));
        routes.Lane("LED OUTPUT", c => LedOutputVerb(c.Args));
        routes.Lane("LED TEST", c => LedTest(c.Args));
        routes.Lane("LED LOCATE", c => LedLocate(c.Args));
        routes.Lane("LED LEVEL", c => LedLevel(c.Args));
        routes.Lane("LED MONITOR", c => LedMonitor(c.Args));
        routes.OffLane("LED IMPORT", LedImportAsync);
        routes.Index(new LedIndex(this));
        // The plan follows the published project: rebuilt on the lane when the rig or what it samples changed, so the render
        // thread and the sender have it before the next frame.
        Store.Changed += snap => Led.Publish(snap.Project);
        Led.Publish(Store.Project);
    }

    // ---- lookups ----

    private static LedFixture? FindLedFixture(Project p, string key) =>
        p.Leds.Fixtures.FirstOrDefault(f => string.Equals(f.Id, key, StringComparison.Ordinal))
        ?? p.Leds.Fixtures.FirstOrDefault(f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static LedNode? FindLedNode(Project p, string key) =>
        p.Leds.Nodes.FirstOrDefault(n => string.Equals(n.Id, key, StringComparison.Ordinal))
        ?? p.Leds.Nodes.FirstOrDefault(n => n.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fixtures named by a word: ALL, #tag, a node (its fixtures), or a fixture by id or name; null with the reason.</summary>
    private static List<LedFixture>? LedScope(Project p, string word, out string why)
    {
        why = "";
        if (word.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return [.. p.Leds.Fixtures];
        if (word.StartsWith('#'))
        {
            var tag = word[1..];
            var tagged = p.Leds.Fixtures.Where(f => f.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
            if (tagged.Count == 0)
                why = $"no fixture is tagged {tag}";
            return tagged.Count == 0 ? null : tagged;
        }
        if (FindLedNode(p, word) is { } node)
        {
            var on = p.Leds.Fixtures.Where(f => f.Patch?.NodeId == node.Id).ToList();
            if (on.Count == 0)
                why = $"nothing is patched to {node.Name}";
            return on.Count == 0 ? null : on;
        }
        if (FindLedFixture(p, word) is { } one)
            return [one];
        why = $"no LED fixture, node or #tag {word} — LED LIST lists them";
        return null;
    }

    private static bool TryAddress(string word, out int universe, out int address)
    {
        universe = 0;
        address = 1;
        var dot = word.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
            return int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out universe) && universe >= 0;
        return int.TryParse(word.AsSpan(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out universe) && universe >= 0
            && int.TryParse(word.AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out address) && address >= 1;
    }

    private static string Num(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);

    // ---- LED LIST ----

    private CommandReply LedList()
    {
        var p = Store.Project;
        var plan = Led.PlanFor(p);
        var view = Led.Output?.View();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("output", view?.Output == true);
            w.WriteStartArray("nodes");
            foreach (var n in LedNodeRows(p, plan, view))
            {
                w.WriteStartObject();
                w.WriteString("id", n.Node.Id);
                w.WriteString("name", n.Node.Name);
                w.WriteString("protocol", ProtocolWord(n.Node.Protocol));
                w.WriteString("host", n.Node.Host);
                w.WriteString("state", n.State);
                w.WriteNumber("universes", n.Universes);
                w.WriteNumber("fixtures", p.Leds.Fixtures.Count(f => f.Patch?.NodeId == n.Node.Id));
                w.WriteString("why", n.Why);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("fixtures");
            foreach (var f in p.Leds.Fixtures)
            {
                var fp = plan.Fixture(f.Id);
                w.WriteStartObject();
                w.WriteString("id", f.Id);
                w.WriteString("name", f.Name);
                w.WriteString("type", f.TypeId);
                w.WriteString("space", f.Space.ToString().ToLowerInvariant() + (f.SpaceId.Length > 0 ? ":" + f.SpaceId : ""));
                w.WriteNumber("pixels", fp?.Pixels ?? LedLayouts.Local(f.Layout).Length);
                w.WriteString("patch", fp?.Range ?? (f.Enabled ? "not sent" : "off"));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("pixels", plan.Pixels);
            w.WriteNumber("universes", plan.Universes);
            WriteConflicts(w, plan);
            w.WriteNumber("mbps", plan.Mbps);
            w.WriteString("refused", plan.Refused);
            w.WriteEndObject();
        }));
    }

    // ---- LED NEW ----

    private CommandReply LedNew(List<string> args)
    {
        const string usage = "LED NEW LINE|GRID|RING <count>[x<rows>] [ON canvas:<id>|output:<id>|surface:<id>|source:<ref>] [TYPE <type>] [<name>]";
        if (args.Count < 2 || !Enum.TryParse<LedShape>(args[0], ignoreCase: true, out var shape) || shape is LedShape.Points
            || int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return CommandReply.Fail(usage);
        var countWord = args[1];
        var x = countWord.IndexOfAny(['x', 'X']);
        var rows = 1;
        if (!int.TryParse(x < 0 ? countWord : countWord[..x], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            || (x >= 0 && !int.TryParse(countWord[(x + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out rows)))
            return CommandReply.Fail($"'{countWord}' is not a count — a number, or columns x rows for a grid");
        if (shape != LedShape.Grid && x >= 0)
            return CommandReply.Fail("only a GRID takes columns x rows");
        if (count < 1 || rows < 1 || (long)count * rows > LedLayouts.MaxPixels)
            return CommandReply.Fail($"a fixture has 1 to {LedLayouts.MaxPixels} pixels");
        var p = Edits.Project;
        var rest = args.Skip(2).ToList();
        string? spaceWord = null;
        string typeId = "builtin:rgb";
        var nameWords = new List<string>();
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i].Equals("ON", StringComparison.OrdinalIgnoreCase) && i + 1 < rest.Count && spaceWord is null)
                spaceWord = rest[++i];
            else if (rest[i].Equals("TYPE", StringComparison.OrdinalIgnoreCase) && i + 1 < rest.Count)
                typeId = rest[++i];
            else
                nameWords.Add(rest[i]);
        }
        var type = LedBuiltinTypes.Resolve(p.Leds, typeId);
        if (type is null)
            return CommandReply.Fail($"no fixture type {typeId} — built in: {string.Join(", ", LedBuiltinTypes.All.Select(t => t.Id[8..]))}, or a type the show has");
        if (!TryLedSpace(p, spaceWord, out var space, out var spaceId, out var w0, out var h0, out var why))
            return CommandReply.Fail(why);
        var unit = space is LedSpace.Surface or LedSpace.Source;
        var (wSpan, hSpan) = unit ? (1.0, 1.0) : (w0 - 1.0, h0 - 1.0);
        var (layout, place) = shape switch
        {
            LedShape.Line => (new LedLayout(LedShape.Line, count, 1, new Vec3d(wSpan, 0, 0), LedStart.TopLeft, false, false, []),
                new Vec3d(0, unit ? 0.5 : Math.Floor(h0 / 2.0), 0)),
            LedShape.Grid => (new LedLayout(LedShape.Grid, count, rows, new Vec3d(wSpan, hSpan, 0), LedStart.TopLeft, false, false, []), Vec3d.Zero),
            _ => (new LedLayout(LedShape.Ring, count, 1, new Vec3d(Math.Min(wSpan, hSpan), 0, 0), LedStart.TopLeft, false, false, []),
                new Vec3d(unit ? 0.5 : Math.Floor(w0 / 2.0), unit ? 0.5 : Math.Floor(h0 / 2.0), 0)),
        };
        var name = nameWords.Count > 0 ? string.Join(' ', nameWords) : $"{shape} {p.Leds.Fixtures.Length + 1}";
        var id = "";
        var r = Edits.Apply($"add LED fixture {name}", pp =>
        {
            id = pp.NewId(Ids.LedFixture);
            var f = new LedFixture(id, name, type.Id, space, spaceId, Transform3.Identity with { Position = place }, layout, null, LedSampling.Point, 0,
                LedColor.Neutral, true, false, false, "#FF8800", []);
            return pp with { Leds = pp.Leds with { Fixtures = pp.Leds.Fixtures.Add(f) } };
        });
        if (!r.Changed)
            return FromEdit(r);
        var pixels = count * rows;
        return CommandReply.Success(id, $"added fixture {id} {name} ({pixels} px on {space.ToString().ToLowerInvariant()} {spaceId}, unpatched)");
    }

    /// <summary>A fixture's space from its ON word (none: the first canvas, else the first output) and its size in its units.</summary>
    private static bool TryLedSpace(Project p, string? word, out LedSpace space, out string spaceId, out int width, out int height, out string why)
    {
        space = LedSpace.Canvas;
        spaceId = "";
        width = height = 1;
        why = "";
        if (word is null)
        {
            if (p.Canvases.FirstOrDefault() is { } c0)
            {
                (spaceId, width, height) = (c0.Id, c0.Width, c0.Height);
                return true;
            }
            if (p.Outputs.FirstOrDefault() is { } o0)
            {
                (space, spaceId, width, height) = (LedSpace.Output, o0.Id, o0.Width, o0.Height);
                return true;
            }
            why = "the show has no canvas or output to lay the fixture on — name a space with ON";
            return false;
        }
        var colon = word.IndexOf(':', StringComparison.Ordinal);
        var kind = colon < 0 ? word : word[..colon];
        var key = colon < 0 ? "" : word[(colon + 1)..];
        switch (kind.ToLowerInvariant())
        {
            case "canvas" when p.Canvases.FirstOrDefault(c => c.Id == key || c.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } c:
                (spaceId, width, height) = (c.Id, c.Width, c.Height);
                return true;
            case "output" when p.Outputs.FirstOrDefault(o => o.Id == key || o.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } o:
                (space, spaceId, width, height) = (LedSpace.Output, o.Id, o.Width, o.Height);
                return true;
            case "surface" when p.Stage.Surfaces.FirstOrDefault(s => s.Id == key || s.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } s:
                (space, spaceId) = (LedSpace.Surface, s.Id);
                return true;
            case "source" when SourceRef.TryParse(key, out var src) && !src.IsNone:
                (space, spaceId) = (LedSpace.Source, src.ToString());
                return true;
            case "canvas" or "output" or "surface":
                why = $"the show has no {kind.ToLowerInvariant()} {key}";
                return false;
            case "source":
                why = $"'{key}' is not a source (media:<id>, generator:<id>, playlist:<id>, canvas:<id> or color:#RRGGBB)";
                return false;
            default:
                why = $"ON takes canvas:<id>, output:<id>, surface:<id> or source:<ref> — not {word}";
                return false;
        }
    }

    // ---- LED IMPORT ----

    /// <summary>A pixel list from this machine's disk, one points fixture per fixture name, added as one undo step; patched when the
    /// file gives addresses and a node is named (or the rig has one). Read off the lane; the edit goes through it.</summary>
    private async Task<CommandReply> LedImportAsync(VerbCall call)
    {
        const string usage = "LED IMPORT <path> [AS CSV] [ON <space>] [SCALE <x>] [NODE <node>]";
        var args = call.Args;
        if (args.Count < 1)
            return CommandReply.Fail(usage);
        var path = args[0];
        string? spaceWord = null, nodeWord = null;
        var scale = 1.0;
        for (var i = 1; i < args.Count; i++)
        {
            var word = args[i].ToUpperInvariant();
            if (i + 1 >= args.Count)
                return CommandReply.Fail(usage);
            var value = args[++i];
            switch (word)
            {
                case "AS" when value.Equals("CSV", StringComparison.OrdinalIgnoreCase):
                    break;
                case "AS":
                    return CommandReply.Fail($"this node imports CSV pixel lists — not {value} (MVR and GDTF are next round)");
                case "ON":
                    spaceWord = value;
                    break;
                case "SCALE" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) && s > 0:
                    scale = s;
                    break;
                case "SCALE":
                    return CommandReply.Fail("SCALE is a number above 0");
                case "NODE":
                    nodeWord = value;
                    break;
                default:
                    return CommandReply.Fail(usage);
            }
        }
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return CommandReply.Fail($"no file {path} on this machine");
            if (info.Length > 64L * 1024 * 1024)
                return CommandReply.Fail($"{path} is larger than 64 MB");
            text = await File.ReadAllTextAsync(path, call.Ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CommandReply.Fail($"{path} could not be read: {ex.Message}");
        }
        if (!LedCsvReader.TryRead(text, scale, out var read, out var why))
            return CommandReply.Fail(why);
        var warnings = new List<string>();
        var ids = new List<string>();
        var r = await EditAsync(call.Session, $"import {read.Length} LED fixture{(read.Length == 1 ? "" : "s")} from {Path.GetFileName(path)}", p =>
        {
            ids.Clear();
            warnings.Clear();
            if (!TryLedSpace(p, spaceWord, out var space, out var spaceId, out _, out _, out var spaceWhy))
                throw new EditRefusedException(spaceWhy);
            LedNode? node = null;
            if (nodeWord is not null)
                node = FindLedNode(p, nodeWord) ?? throw new EditRefusedException($"no LED node {nodeWord}");
            else if (p.Leds.Nodes.Length == 1)
                node = p.Leds.Nodes[0];
            var fixtures = p.Leds.Fixtures;
            foreach (var f in read)
            {
                var type = f.Type.Length > 0 ? LedBuiltinTypes.Resolve(p.Leds, f.Type) ?? LedBuiltinTypes.Resolve(p.Leds, f.Type.ToLowerInvariant()) : null;
                if (f.Type.Length > 0 && type is null)
                    warnings.Add($"{f.Name}: no type {f.Type} — imported as RGB");
                LedPatch? patch = null;
                if (f.Address > 0 && node is not null)
                    patch = new LedPatch(node.Id, f.Universe, f.Address);
                else if (f.Address > 0)
                    warnings.Add($"{f.Name}: the file gives {f.Universe}.{f.Address} but no node is named — unpatched (NODE <node>)");
                var id = p.NewId(Ids.LedFixture);
                ids.Add(id);
                fixtures = fixtures.Add(new LedFixture(id, f.Name, type?.Id ?? "builtin:rgb", space, spaceId, Transform3.Identity,
                    new LedLayout(LedShape.Points, f.Points.Length, 1, Vec3d.Zero, LedStart.TopLeft, false, false, f.Points), patch, LedSampling.Point, 0,
                    LedColor.Neutral, true, false, false, "#FF8800", []));
                p = p with { Leds = p.Leds with { Fixtures = fixtures } };
            }
            return p;
        }).ConfigureAwait(false);
        if (!r.Changed)
            return FromEdit(r);
        var plan = Led.PlanFor(Store.Project);
        foreach (var c in plan.Conflicts.Where(c => c.Fixtures.Any(ids.Contains)))
            warnings.Add($"{string.Join(" and ", c.Fixtures)} overlap on {c.Universe}.{c.From}–{c.Universe}.{c.To}");
        var pixels = read.Sum(f => f.Points.Length);
        var json = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("fixtures", read.Length);
            w.WriteNumber("types", 0);
            w.WriteNumber("nodes", 0);
            w.WriteNumber("pixels", pixels);
            w.WriteStartArray("ids");
            foreach (var id in ids)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteStartArray("warnings");
            foreach (var x in warnings)
                w.WriteStringValue(x);
            w.WriteEndArray();
            w.WriteEndObject();
        });
        return CommandReply.Success(json, $"imported {read.Length} LED fixture{(read.Length == 1 ? "" : "s")}, {pixels} px, from {Path.GetFileName(path)}");
    }

    // ---- patching ----

    private CommandReply LedPatchVerb(List<string> args)
    {
        if (args.Count < 3)
            return CommandReply.Fail("LED PATCH <fixture> <node> <universe>[.<address>] [FORCE]");
        var force = args[^1].Equals("FORCE", StringComparison.OrdinalIgnoreCase);
        var p = Edits.Project;
        if (FindLedFixture(p, args[0]) is not { } f)
            return CommandReply.Fail($"no LED fixture {args[0]} — LED LIST lists them");
        if (FindLedNode(p, args[1]) is not { } node)
            return CommandReply.Fail($"no LED node {args[1]} — ADD lednode first");
        if (!TryAddress(args[2], out var universe, out var address))
            return CommandReply.Fail($"'{args[2]}' is not <universe>[.<address>]");
        if (node.Protocol == LedProtocol.Ddp && universe != 0)
            return CommandReply.Fail("a DDP device has no universes: patch it at 0.<byte>");
        var type = LedBuiltinTypes.Resolve(p.Leds, f.TypeId);
        if (type is null)
            return CommandReply.Fail($"{f.Name} names a type the show does not have ({f.TypeId})");
        if (LedPatcher.DdpEightBitOnly(node) && LedBuiltinTypes.Has16Bit(type))
            return CommandReply.Fail(LedPatcher.Wled16Words(node, type));
        var patch = new LedPatch(node.Id, universe, address);
        if (f.Patch == patch)
            return CommandReply.Fail($"{f.Name} is already patched so");
        if (!LedPatcher.TryPlace(node, type, LedLayouts.Local(f.Layout).Length, universe, address, null, out _, out _, out var why))
            return CommandReply.Fail($"{f.Name} cannot start at {LedPatcher.Words(node.Protocol, universe, address)} on {node.Name}: {why}");
        var next = p with { Leds = p.Leds with { Fixtures = p.Leds.Fixtures.Update(f.Id, x => x with { Patch = patch }) } };
        if (!force && LedPatchRefusal(p, next, [f.Id]) is { } refused)
            return CommandReply.Fail(refused + " — add FORCE to patch it anyway");
        var r = Edits.Apply($"patch {f.Name} to {node.Name} {LedPatcher.Words(node.Protocol, universe, address)}",
            pp => pp with { Leds = pp.Leds with { Fixtures = pp.Leds.Fixtures.Update(f.Id, x => x with { Patch = patch }) } });
        if (!r.Changed)
            return FromEdit(r);
        var range = Led.PlanFor(Store.Project).Fixture(f.Id)?.Range ?? LedPatcher.Words(node.Protocol, universe, address);
        return CommandReply.Success(range, $"patched {f.Name} to {range}");
    }

    /// <summary>Why a patch change is refused: a new overlap with another fixture (named with its slots), or the bandwidth guard
    /// (with its arithmetic) where the current patch was within it; null when it is fine.</summary>
    private string? LedPatchRefusal(Project before, Project after, IReadOnlyCollection<string> changed)
    {
        var was = Led.PlanFor(before);
        var plan = Led.Trial(after);
        foreach (var c in plan.Conflicts)
        {
            if (!c.Fixtures.Any(changed.Contains))
                continue;
            if (was.Conflicts.Any(o => o.NodeId == c.NodeId && o.Universe == c.Universe && o.From == c.From && o.Fixtures.SequenceEqual(c.Fixtures)))
                continue;
            var node = after.Leds.Nodes.FirstOrDefault(n => n.Id == c.NodeId);
            var names = c.Fixtures.Select(id => $"{id} ({after.Leds.Fixtures.FirstOrDefault(x => x.Id == id)?.Name})");
            return node?.Protocol == LedProtocol.Ddp
                ? $"it would overlap on {node.Name} bytes {c.Universe * 512 + c.From}–{c.Universe * 512 + c.To}: {string.Join(" and ", names)}"
                : $"it would overlap on {node?.Name ?? c.NodeId} {c.Universe}.{c.From}–{c.Universe}.{c.To}: {string.Join(" and ", names)}";
        }
        if (plan.Refused.Length > 0 && was.Refused.Length == 0)
            return plan.Refused;
        return null;
    }

    private CommandReply LedAutoPatch(List<string> args)
    {
        var p = Edits.Project;
        var scopeWord = "ALL";
        var nodeWords = new List<string>();
        int? fromU = null;
        var fromA = 1;
        var each = false;
        var i = 0;
        if (args.Count > 0 && !IsAutoKeyword(args[0]))
            scopeWord = args[i++];
        while (i < args.Count)
        {
            var word = args[i++].ToUpperInvariant();
            switch (word)
            {
                case "NODE":
                    while (i < args.Count && !IsAutoKeyword(args[i]))
                        nodeWords.Add(args[i++]);
                    if (nodeWords.Count == 0)
                        return CommandReply.Fail("NODE names one node or more");
                    break;
                case "FROM" when i < args.Count:
                    if (!TryAddress(args[i++], out var u, out fromA))
                        return CommandReply.Fail("FROM takes <universe>[.<address>]");
                    fromU = u;
                    break;
                case "EACH" when i < args.Count && args[i].ToUpperInvariant() is "ON" or "OFF":
                    each = args[i++].Equals("ON", StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    return CommandReply.Fail("LED AUTOPATCH [<fixture>|#<tag>|ALL] [NODE <node>…] [FROM <universe>[.<address>]] [EACH ON|OFF]");
            }
        }
        if (LedScope(p, scopeWord, out var scopeWhy) is not { } scope)
            return CommandReply.Fail(scopeWhy);
        scope = [.. scope.Where(f => f.Enabled)];
        if (scope.Count == 0)
            return CommandReply.Fail("every fixture in scope is off");
        var nodes = new List<LedNode>();
        foreach (var nw in nodeWords)
        {
            if (FindLedNode(p, nw) is not { } n)
                return CommandReply.Fail($"no LED node {nw}");
            nodes.Add(n);
        }
        if (nodes.Count == 0)
            nodes = [.. p.Leds.Nodes.Where(n => n.Enabled)];
        if (nodes.Count == 0)
            return CommandReply.Fail(p.Leds.Nodes.IsEmpty ? "there is no LED node to patch to — ADD lednode first" : "every LED node is off — name one with NODE");
        var first = nodes[0];
        if (fromU is null)
        {
            // From the start of the first node, or — for part of the rig — after what the rest of the rig uses on it.
            fromU = LedPatcher.MinUniverse(first.Protocol);
            fromA = 1;
            var others = p with { Leds = p.Leds with { Fixtures = [.. p.Leds.Fixtures.Where(f => !scope.Any(s => s.Id == f.Id))] } };
            var rows = Led.Trial(others).Rows.Where(r => r.NodeId == first.Id && r.Used > 0).ToList();
            if (rows.Count > 0)
            {
                var last = rows[^1];
                if (first.Protocol == LedProtocol.Ddp)
                    (fromU, fromA) = (0, last.Universe * 512 + last.Used + 1);
                else if (last.Used >= LedPatcher.Capacity(first))
                    (fromU, fromA) = (last.Universe + 1, 1);
                else
                    (fromU, fromA) = (last.Universe, last.Used + 1);
            }
        }
        if (!LedPatcher.TryAutoPatch(p.Leds, scope, nodes, fromU.Value, fromA, each, out var patches, out var why))
            return CommandReply.Fail(why);
        if (patches.All(x => scope.First(f => f.Id == x.FixtureId).Patch == x.Patch))
            return CommandReply.Fail("already patched so");
        var map = patches.ToDictionary(x => x.FixtureId, x => x.Patch, StringComparer.Ordinal);
        Project Apply(Project pp) => pp with { Leds = pp.Leds with { Fixtures = [.. pp.Leds.Fixtures.Select(f => map.TryGetValue(f.Id, out var np) ? f with { Patch = np } : f)] } };
        var next = Apply(p);
        if (LedPatchRefusal(p, next, map.Keys) is { } refused)
            return CommandReply.Fail(refused);
        var r = Edits.Apply($"auto-patch {scope.Count} LED fixture{(scope.Count == 1 ? "" : "s")}", Apply);
        if (!r.Changed)
            return FromEdit(r);
        var plan = Led.PlanFor(Store.Project);
        var used = plan.Rows.Where(row => patches.Any(x => x.Patch.NodeId == row.NodeId)).ToList();
        var range = used.Count == 0 ? "" : $"{used.Min(u => u.Universe)}–{used.Max(u => u.Universe)}";
        var pixels = scope.Sum(f => plan.Fixture(f.Id)?.Pixels ?? 0);
        var json = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("universes", range);
            w.WriteNumber("pixels", pixels);
            w.WriteNumber("fixtures", scope.Count);
            w.WriteNumber("conflicts", plan.Conflicts.Length);
            w.WriteEndObject();
        });
        return CommandReply.Success(json, $"auto-patched {scope.Count} fixture{(scope.Count == 1 ? "" : "s")}, {pixels} px, universes {range}");
    }

    private static bool IsAutoKeyword(string word) => word.ToUpperInvariant() is "NODE" or "FROM" or "EACH";

    // ---- the air ----

    private CommandReply LedOutputVerb(List<string> args)
    {
        if (args.Count < 1 || args[0].ToUpperInvariant() is not ("ON" or "OFF"))
            return CommandReply.Fail("LED OUTPUT ON|OFF [<node>|ALL] [HOLD]");
        var on = args[0].Equals("ON", StringComparison.OrdinalIgnoreCase);
        var hold = args.Count > 1 && args[^1].Equals("HOLD", StringComparison.OrdinalIgnoreCase);
        var target = args.Skip(1).Where(a => !a.Equals("HOLD", StringComparison.OrdinalIgnoreCase)).ToList();
        if (hold && on)
            return CommandReply.Fail("HOLD goes with OFF: it stops without the black frame");
        if (target.Count == 0 || (target.Count == 1 && target[0].Equals("ALL", StringComparison.OrdinalIgnoreCase)))
        {
            if (on && !Led.HasRenderer)
                return CommandReply.Fail("no renderer on this node — LED output needs the GPU");
            if (on && Led.Output is null)
                return CommandReply.Fail("this node has no LED output");
            if (Led.Master == on)
                return CommandReply.Fail(on ? "this machine already sends LED data" : "LED output is already off on this machine");
            Led.SetMaster(on, hold);
            FactsChanged();
            var saved = Led.SettingsProblem.Length > 0 ? $" (not saved: {Led.SettingsProblem})" : "";
            if (on && Led.Current.Refused.Length > 0)
                saved += " — but nothing is sent: " + Led.Current.Refused;
            return CommandReply.Success(on ? "on" : "off",
                on ? "this machine sends LED data" + saved : (hold ? "LED output off, held" : "LED output off — one black frame sent") + saved);
        }
        var p = Edits.Project;
        if (FindLedNode(p, string.Join(' ', target)) is not { } node)
            return CommandReply.Fail($"no LED node {string.Join(' ', target)}");
        if (node.Enabled == on)
            return CommandReply.Fail($"{node.Name} is already {(on ? "on" : "off")}");
        if (!on)
            Led.Output?.StopNode(node.Id, blackFirst: !hold);
        var r = Edits.Apply($"{node.Name} {(on ? "on" : "off")}", pp => pp with { Leds = pp.Leds with { Nodes = pp.Leds.Nodes.Update(node.Id, n => n with { Enabled = on }) } });
        if (!r.Changed)
            return FromEdit(r);
        return CommandReply.Success(on ? "on" : "off", on ? $"{node.Name} on" : hold ? $"{node.Name} off, held" : $"{node.Name} off — one black frame sent");
    }

    private CommandReply LedTest(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("LED TEST CHASE|ORDER|UNIVERSE|FIXTURE|GRADIENT|WALK|WHITE|FULL|BLACK|OFF [ON <node>|<fixture>|#<tag>|ALL]");
        var live = Led.Live;
        if (args[0].Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            if (live.Test == LedTestKind.None)
                return CommandReply.Fail("no LED test is up");
            Led.SetLive(l => l with { Test = LedTestKind.None, TestScope = null, TestWords = "" });
            FactsChanged();
            return CommandReply.Success("off", "LED test off — the show is back on the LEDs");
        }
        if (!Enum.TryParse<LedTestKind>(args[0], ignoreCase: true, out var kind) || kind == LedTestKind.None
            || int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return CommandReply.Fail($"no test pattern {args[0]} — chase, order, universe, fixture, gradient, walk, white, full or black");
        ImmutableHashSet<string>? scope = null;
        var scopeWords = "ALL";
        if (args.Count > 1)
        {
            if (!args[1].Equals("ON", StringComparison.OrdinalIgnoreCase) || args.Count < 3)
                return CommandReply.Fail("LED TEST <pattern> [ON <node>|<fixture>|#<tag>|ALL]");
            scopeWords = string.Join(' ', args.Skip(2));
            if (!scopeWords.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                if (LedScope(Store.Project, scopeWords, out var why) is not { } fixtures)
                    return CommandReply.Fail(why);
                scope = [.. fixtures.Select(f => f.Id)];
            }
            else
            {
                scopeWords = "ALL";
            }
        }
        if (live.Test == kind && string.Equals(live.TestWords, scopeWords, StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail($"the {LedTestPatterns.Word(kind)} test is already up on {scopeWords}");
        Led.SetLive(l => l with { Test = kind, TestScope = scope, TestWords = scopeWords });
        FactsChanged();
        var sending = Led.Master && Led.Output is not null ? "" : " (this machine is not sending LED data — LED OUTPUT ON)";
        return CommandReply.Success(LedTestPatterns.Word(kind), $"LED test {LedTestPatterns.Word(kind)} on {scopeWords}{sending}");
    }

    private CommandReply LedLocate(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("LED LOCATE <fixture> [<seconds 1-60>]");
        var p = Store.Project;
        var secondsGiven = args.Count > 1 && double.TryParse(args[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        var key = string.Join(' ', secondsGiven ? args.Take(args.Count - 1) : args);
        if (FindLedFixture(p, key) is not { } f)
            return CommandReply.Fail($"no LED fixture {key}");
        var ns = LocateDefaultNs;
        if (secondsGiven)
        {
            var s = double.Parse(args[^1], NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(s) || s < 1 || s > 60)
                return CommandReply.Fail("a locate lasts 1 to 60 seconds");
            ns = (long)(s * 1e9);
        }
        var until = LeaderNowNs + ns;
        Led.SetLive(l => l with { Locate = f.Id, LocateUntilNs = until });
        FactsChanged();
        return CommandReply.Success(f.Id, $"blinking {f.Name} for {Num(ns / 1e9, "0.#")} s");
    }

    private CommandReply LedLevel(List<string> args)
    {
        const string usage = "LED LEVEL <0-100> [FADE <ms>], or LED LEVEL BY <delta> [FADE <ms>]";
        if (args.Count < 1)
            return CommandReply.Fail(usage);
        var by = args[0].Equals("BY", StringComparison.OrdinalIgnoreCase);
        var i = by ? 1 : 0;
        if (i >= args.Count || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            return CommandReply.Fail(usage);
        long fadeMs = 0;
        if (args.Count > i + 1)
        {
            if (args.Count != i + 3 || !args[i + 1].Equals("FADE", StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(args[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out fadeMs) || fadeMs < 0 || fadeMs > 600_000)
                return CommandReply.Fail("FADE takes 0 to 600000 ms");
        }
        var now = LeaderNowNs;
        var live = Led.Live;
        var current = live.LevelAt(now);
        var target = Math.Round(Math.Clamp(by ? current * 100 + value : value, 0, 100), 3) / 100;
        if (!by && (value < 0 || value > 100))
            return CommandReply.Fail("the LED level is 0 to 100");
        if (Math.Abs(target - live.LevelTo) < 1e-9 && live.LevelAt(now) == live.LevelTo)
            return CommandReply.Fail($"the LED level is already {Num(target * 100)}");
        Led.SetLive(l => l with { LevelFrom = current, LevelTo = target, FadeStartNs = now, FadeLengthNs = fadeMs * 1_000_000 });
        FactsChanged();
        return CommandReply.Success(Num(target * 100), fadeMs > 0 ? $"LED level to {Num(target * 100)} over {fadeMs} ms" : $"LED level {Num(target * 100)}");
    }

    private CommandReply LedMonitor(List<string> args)
    {
        if (args.Count < 2 || !int.TryParse(args[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var universe))
            return CommandReply.Fail("LED MONITOR <node> <universe>");
        var p = Store.Project;
        var key = string.Join(' ', args.Take(args.Count - 1));
        if (FindLedNode(p, key) is not { } node)
            return CommandReply.Fail($"no LED node {key}");
        if (Led.Output is not { } output)
            return CommandReply.Fail("this node has no LED output");
        var slots = new byte[LedPlan.SlotsPerRow];
        if (!output.TryMonitor(node.Id, universe, slots, out var leaderNs))
            return CommandReply.Fail($"nothing has been sent to {node.Name} universe {universe}");
        var plan = Led.PlanFor(p);
        var fixtures = new List<(string Id, int From, int To)>();
        var row = plan.RowOf(node.Id, universe);
        if (row >= 0)
        {
            for (var s = 0; s < LedPlan.SlotsPerRow; s++)
            {
                var op = plan.Patch[(row * LedPlan.SlotsPerRow + s) * 4 + 1];
                if (op < 0)
                    continue;
                var fid = plan.Fixtures[(int)plan.Patch[(row * LedPlan.SlotsPerRow + s) * 4 + 2]].Id;
                if (fixtures.Count > 0 && fixtures[^1].Id == fid && fixtures[^1].To == s)
                    fixtures[^1] = (fid, fixtures[^1].From, s + 1);
                else
                    fixtures.Add((fid, s + 1, s + 1));
            }
        }
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("node", node.Id);
            w.WriteNumber("universe", universe);
            w.WriteNumber("leaderMs", leaderNs / 1_000_000);
            w.WriteString("slots", Convert.ToHexString(slots));
            w.WriteStartArray("fixtures");
            foreach (var (fid, from, to) in fixtures)
            {
                w.WriteStartObject();
                w.WriteString("id", fid);
                w.WriteNumber("from", from);
                w.WriteNumber("to", to);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private long _ledFadePublishedNs;

    partial void TickLed(long leaderNs)
    {
        var live = Led.Live;
        // A level fade moves STATE without an edit: published ten times a second while it runs, and once when it lands.
        if (live.FadeLengthNs > 0 && _ledFadePublishedNs <= live.FadeStartNs + live.FadeLengthNs
            && (leaderNs - _ledFadePublishedNs >= 100_000_000 || leaderNs >= live.FadeStartNs + live.FadeLengthNs))
        {
            _ledFadePublishedNs = leaderNs;
            FactsChanged();
        }
        if (live.Locate is not null && leaderNs >= live.LocateUntilNs)
        {
            Led.SetLive(l => l with { Locate = null, LocateUntilNs = 0 });
            FactsChanged();
        }
    }

    // ---- STATE, context, validation, recovery ----

    /// <summary>A node as STATE, LED LIST, INDEX and the context tell it: its state word from the machine master, the plan and what
    /// the sender observed.</summary>
    internal sealed record LedNodeRow(LedNode Node, string State, string Why, int Universes, LedNodeView? Sent);

    internal List<LedNodeRow> LedNodeRows(Project p, LedPlan plan, LedOutputView? view)
    {
        var rows = new List<LedNodeRow>();
        foreach (var n in p.Leds.Nodes)
        {
            var np = plan.Node(n.Id);
            var sent = view?.Nodes.FirstOrDefault(v => v.Id == n.Id);
            var universes = np?.Rows ?? 0;
            var (state, why) = !n.Enabled ? ("disabled", "this node is off in the show — LED OUTPUT ON " + n.Id)
                : Led.Output is null ? ("off", Led.HasRenderer ? "this node has no LED output" : "no renderer on this node — LED output needs the GPU")
                : view?.Output != true ? ("off", "this machine does not send LED data — LED OUTPUT ON")
                : plan.Refused.Length > 0 ? ("refused", plan.Refused)
                : universes == 0 ? ("idle", np?.Why is { Length: > 0 } w ? w : "nothing is patched to it")
                : sent is not null ? (sent.State, sent.Why)
                : ("idle", "waiting for the sender");
            rows.Add(new LedNodeRow(n, state, why, universes, sent));
        }
        return rows;
    }

    private static string ProtocolWord(LedProtocol p) => p switch
    {
        LedProtocol.ArtNet => "artnet",
        LedProtocol.Sacn => "sacn",
        _ => "ddp",
    };

    private static void WriteConflicts(Utf8JsonWriter w, LedPlan plan)
    {
        w.WriteStartArray("conflicts");
        foreach (var c in plan.Conflicts)
        {
            w.WriteStartObject();
            w.WriteString("node", c.NodeId);
            w.WriteNumber("universe", c.Universe);
            w.WriteNumber("from", c.From);
            w.WriteNumber("to", c.To);
            w.WriteStartArray("fixtures");
            foreach (var f in c.Fixtures)
                w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    partial void StateLed(Utf8JsonWriter w)
    {
        var p = Store.Project;
        var plan = Led.PlanFor(p);
        var view = Led.Output?.View();
        var live = Led.Live;
        var now = LeaderNowNs;
        var rows = LedNodeRows(p, plan, view);
        w.WriteStartObject("led");
        w.WriteBoolean("output", view?.Output == true);
        w.WriteNumber("level", Math.Round(live.LevelAt(now), 4));
        w.WriteNumber("fixtures", plan.Fixtures.Length);
        w.WriteNumber("pixels", plan.Pixels);
        w.WriteNumber("universes", plan.Universes);
        w.WriteNumber("mbps", plan.Mbps);
        if (live.Test == LedTestKind.None)
        {
            w.WriteNull("test");
        }
        else
        {
            w.WriteStartObject("test");
            w.WriteString("pattern", LedTestPatterns.Word(live.Test));
            w.WriteString("on", live.TestWords);
            w.WriteEndObject();
        }
        if (live.Locating(now))
        {
            w.WriteStartObject("locate");
            w.WriteString("fixture", live.Locate);
            w.WriteNumber("leftMs", (live.LocateUntilNs - now) / 1_000_000);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("locate");
        }
        w.WriteNumber("lagMs", Math.Round(rows.Where(r => r.Sent is not null).Select(r => r.Sent!.LateMs).DefaultIfEmpty(0).Max(), 3));
        w.WriteNumber("lateCollects", Led.Pass?.LateCollects ?? 0);
        w.WriteStartArray("nodes");
        foreach (var r in rows)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Node.Id);
            w.WriteString("name", r.Node.Name);
            w.WriteString("protocol", ProtocolWord(r.Node.Protocol));
            w.WriteString("host", r.Node.Host);
            w.WriteString("state", r.State);
            w.WriteNumber("fps", Math.Round(r.Sent?.Fps ?? 0, 2));
            w.WriteNumber("universes", r.Universes);
            w.WriteNumber("lateMs", Math.Round(r.Sent?.LateMs ?? 0, 3));
            if (r.Sent?.Seen is { } seen)
            {
                w.WriteStartObject("seen");
                w.WriteString("name", seen.Name);
                w.WriteNumber("refreshHz", seen.RefreshHz);
                w.WriteNumber("ageS", Math.Round(Math.Max(0, Services.Mono.NowNs - seen.SeenMonoNs) / 1e9, 1));
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("seen");
            }
            w.WriteString("why", r.Why);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        WriteConflicts(w, plan);
        w.WriteStartArray("warnings");
        foreach (var x in plan.Warnings)
            w.WriteStringValue(x);
        if (Led.Pass?.Problem is { } problem)
            w.WriteStringValue(problem);
        if (plan.Refused.Length > 0)
            w.WriteStringValue(plan.Refused);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    partial void ContextLed(List<ContextItem> items, List<ContextLink> links)
    {
        var p = Store.Project;
        if (p.Leds.Nodes.IsEmpty)
            return;
        var plan = Led.PlanFor(p);
        var live = Led.Live;
        var now = LeaderNowNs;
        foreach (var r in LedNodeRows(p, plan, Led.Output?.View()))
        {
            var air = live.Test != LedTestKind.None ? $"test {LedTestPatterns.Word(live.Test)}" : live.Locating(now) ? "locate up" : "";
            var light = r.State switch
            {
                "unreachable" or "refused" => Led.Master ? Light.Red : Light.Grey,
                "late" => Light.Amber,
                "sending" => air.Length > 0 ? Light.Amber : Light.Green,
                _ => Light.Grey,
            };
            var words = r.State == "sending"
                ? $"sending {Num(r.Sent?.Fps ?? 0, "0.#")} fps · {r.Universes} universe{(r.Universes == 1 ? "" : "s")}{(air.Length > 0 ? " · " + air : "")}"
                : r.Why.Length > 0 ? $"{r.State}: {r.Why}" : r.State;
            items.Add(new ContextItem("led:" + r.Node.Id, "led", r.Node.Name, light, words));
            foreach (var o in plan.Fixtures.Where(f => f.NodeId == r.Node.Id && f.AirOutput.Length > 0).Select(f => f.AirOutput).Distinct())
                links.Add(new ContextLink("output:" + o, "led:" + r.Node.Id, ContextLinkKinds.Feeds, light));
        }
    }

    partial void ValidateLed(Project p, object entity, ref string? why)
    {
        if (why is not null)
            return;
        switch (entity)
        {
            case LedNode n when n.UniverseChannels is < 0 or > 512:
                why = "a node's universe channels are 1 to 512 (0 = 510)";
                break;
            case LedNode n when n.RateHz is < 0 or > 1000 || !double.IsFinite(n.RateHz):
                why = "a node's rate is 0 (the protocol's default) to 1000 Hz";
                break;
            case LedNode n when n.Host.Length > 0 && !System.Net.IPAddress.TryParse(n.Host, out _):
                why = $"{n.Host} is not an address — give the node's IP address";
                break;
            case LedFixture f when LedBuiltinTypes.Resolve(p.Leds, f.TypeId) is null:
                why = $"no fixture type {f.TypeId}";
                break;
            case LedFixture f when LedLayouts.Local(f.Layout).Length == 0:
                why = "a fixture has at least one pixel";
                break;
            case LedFixtureType t when LedBuiltinTypes.Problem(t) is { Length: > 0 } problem:
                why = problem;
                break;
        }
    }

    partial void RecoverySaveLed(IDictionary<string, string> into)
    {
        var live = Led.Live;
        into["led.level"] = Num(live.LevelTo, "R");
        if (live.Test != LedTestKind.None)
        {
            into["led.test"] = LedTestPatterns.Word(live.Test);
            into["led.testOn"] = live.TestWords;
            if (live.TestScope is { } scope)
                into["led.testScope"] = string.Join(',', scope.Order(StringComparer.Ordinal));
        }
    }

    partial void RecoveryLoadLed(IReadOnlyDictionary<string, string> from)
    {
        var level = from.TryGetValue("led.level", out var l) && double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? Math.Clamp(v, 0, 1) : 1;
        var test = from.TryGetValue("led.test", out var t) && Enum.TryParse<LedTestKind>(t, ignoreCase: true, out var k) ? k : LedTestKind.None;
        ImmutableHashSet<string>? scope = from.TryGetValue("led.testScope", out var s) && s.Length > 0 ? [.. s.Split(',')] : null;
        var words = from.TryGetValue("led.testOn", out var on) ? on : "ALL";
        Led.SetLive(x => x with { LevelFrom = level, LevelTo = level, FadeLengthNs = 0, Test = test, TestScope = test == LedTestKind.None ? null : scope,
            TestWords = test == LedTestKind.None ? "" : words });
    }
}
