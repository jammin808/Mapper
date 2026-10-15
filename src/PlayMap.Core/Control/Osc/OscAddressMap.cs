using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Control.Osc;

/// <summary>What an OSC message under /playmap asks: lines to run, a ping, a read of one field, or nothing it knows (with why).</summary>
public enum OscRouteKind { Lines, Ping, Query, Unknown }

/// <summary>An OSC message read as the node's words.</summary>
/// <param name="QueryId">For a query: the entity and the field path read back to the sender.</param>
public sealed record OscRoute(OscRouteKind Kind, IReadOnlyList<string> Lines, string? RequestId, string Why, string QueryId = "", string QueryPath = "")
{
    public static OscRoute Unknown(string why) => new(OscRouteKind.Unknown, [], null, why);
}

/// <summary>The node's OSC address space (pure): "/playmap/line ,s &lt;line&gt;" runs any line; "/playmap/&lt;verb words&gt;[/&lt;word&gt;…]
/// [args]" is that verb with the segments and arguments as words; "/playmap/set/&lt;id&gt;/&lt;path…&gt; &lt;value&gt;" is SET (a pattern in
/// the id sets every matching entity; "+"/"-" and a step is NUDGE; "?" reads the value back); "/playmap/surfaces|outputs|
/// generators|playlists/&lt;name&gt;/…" acts by name. Plural kinds never collide with verb words.</summary>
public static class OscAddressMap
{
    public const string Prefix = "/playmap/";

    public static OscRoute Map(OscMessage message, Project? project)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Address.StartsWith(Prefix, StringComparison.Ordinal))
            return OscRoute.Unknown($"{message.Address} is not under /playmap");
        var segments = message.Address[Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return OscRoute.Unknown("/playmap/ names no verb");
        var head = segments[0].ToLowerInvariant();
        switch (head)
        {
            case "line":
                if (message.Args.Length is < 1 or > 2 || message.Args[0].Tag is not ('s' or 'S'))
                    return OscRoute.Unknown("/playmap/line takes ,s \"<line>\" [,s \"<request id>\"]");
                return new OscRoute(OscRouteKind.Lines, [message.Args[0].Text ?? ""], message.Args.Length == 2 ? message.Args[1].Text : null, "");
            case "ping":
                return new OscRoute(OscRouteKind.Ping, [], null, "");
            case "set":
                return Set(segments, message, project);
            case "surfaces" or "outputs" or "generators" or "playlists" or "layers":
                return ByName(head, segments, message, project);
        }
        return Verb(segments, message);
    }

    private static OscRoute Verb(string[] segments, OscMessage message)
    {
        VerbSpec? spec = null;
        var used = 0;
        if (segments.Length >= 2 && VerbCatalog.Find(segments[0] + " " + segments[1]) is { } two)
            (spec, used) = (two, 2);
        else if (VerbCatalog.Find(segments[0]) is { } one)
            (spec, used) = (one, 1);
        if (spec is null)
            return OscRoute.Unknown($"no verb {string.Join(' ', segments.Take(2)).ToUpperInvariant()} — /playmap/line ,s \"<line>\" runs any line");
        var words = spec.Name.Split(' ').ToList();
        var verbWords = words.Count;
        words.AddRange(segments.Skip(used).Select(Uri.UnescapeDataString));
        var onOff = spec.Syntax.Contains("ON|OFF", StringComparison.Ordinal);
        var itemSlot = spec.Name is "PLAYLIST GO" or "PLAYLIST CUE";
        foreach (var a in message.Args)
        {
            var afterVerb = words.Count - verbWords;
            if (onOff && a.IsNumber && a.Tag is not ('T' or 'F'))
                words.Add(Math.Abs(a.Number) > 1e-9 ? "ON" : "OFF");
            else if (a.Tag is 'T' or 'F')
                words.Add(onOff ? (a.Tag == 'T' ? "ON" : "OFF") : (a.Tag == 'T' ? "true" : "false"));
            else if (itemSlot && afterVerb == 1 && a.Tag is 'i' or 'h')
                words.Add("#" + a.Int.ToString(CultureInfo.InvariantCulture));
            else
                words.Add(Word(a));
        }
        if (message.Args.Length == 0 && words.Count == verbWords + segments.Length - used && spec.Syntax.Contains("|TOGGLE", StringComparison.Ordinal)
            && !words.Skip(verbWords).Any(w => w.Equals("ON", StringComparison.OrdinalIgnoreCase) || w.Equals("OFF", StringComparison.OrdinalIgnoreCase)))
            words.Add("TOGGLE");
        return new OscRoute(OscRouteKind.Lines, [string.Join(' ', words.Select(WireGrammar.Quote))], null, "");
    }

    /// <summary>An argument as a line word: strings as given, integers and floats invariant (a float32 at its own precision).</summary>
    public static string Word(OscArg a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return a.Tag switch
        {
            'i' or 'h' or 'c' => a.Int.ToString(CultureInfo.InvariantCulture),
            'f' => ((float)a.Float).ToString("R", CultureInfo.InvariantCulture),
            'd' => a.Float.ToString("R", CultureInfo.InvariantCulture),
            's' or 'S' => a.Text ?? "",
            'T' => "true",
            'F' => "false",
            _ => "null",
        };
    }

    /// <summary>An argument as a JSON literal (SET's value).</summary>
    private static string Json(OscArg a) => a.Tag switch
    {
        's' or 'S' => JsonSerializer.Serialize(a.Text ?? ""),
        'T' => "true",
        'F' => "false",
        'N' or 'I' => "null",
        '[' => "[" + string.Join(",", a.Items.Select(Json)) + "]",
        _ => Word(a),
    };

    private static OscRoute Set(string[] segments, OscMessage message, Project? project)
    {
        if (segments.Length < 3)
            return OscRoute.Unknown("/playmap/set/<id>/<field>[/<path>…] <value>");
        var id = segments[1];
        var path = string.Join('.', segments.Skip(2));
        return SetOf(id, path, message, project);
    }

    private static OscRoute SetOf(string id, string path, OscMessage message, Project? project)
    {
        if (message.Args.Length == 0)
            return OscRoute.Unknown($"/playmap/set/{id}/… takes a value (or \"?\" to read it)");
        var first = message.Args[0];
        if (first.Tag is 's' or 'S' && first.Text == "?" && message.Args.Length == 1)
            return new OscRoute(OscRouteKind.Query, [], null, "", id, path);
        var ids = OscPattern.IsPattern(id)
            ? project?.AllIds().Where(x => OscPattern.Matches(id, x)).ToList() ?? []
            : [id];
        if (ids.Count == 0)
            return OscRoute.Unknown($"no id matches {id}");
        if (first.Tag is 's' or 'S' && first.Text is "+" or "-" && message.Args.Length == 2 && message.Args[1].IsNumber)
        {
            var step = message.Args[1].Number * (first.Text == "-" ? -1 : 1);
            var delta = (step >= 0 ? "+" : "") + step.ToString("R", CultureInfo.InvariantCulture);
            return new OscRoute(OscRouteKind.Lines, ids.Select(x => $"NUDGE {x} {path} {delta}").ToList(), null, "");
        }
        var value = message.Args.Length == 1 ? Json(first) : "[" + string.Join(",", message.Args.Select(Json)) + "]";
        return new OscRoute(OscRouteKind.Lines, ids.Select(x => $"SET {x} {path} {value}").ToList(), null, "");
    }

    private static OscRoute ByName(string kind, string[] segments, OscMessage message, Project? project)
    {
        if (project is null)
            return OscRoute.Unknown("no show is open");
        if (segments.Length < 3)
            return OscRoute.Unknown($"/playmap/{kind}/<name>/<field>");
        string? id = kind switch
        {
            "surfaces" => project.Stage.Surfaces.FirstOrDefault(s => Named(s.Id, s.Name, segments[1]))?.Id,
            "outputs" => project.Outputs.FirstOrDefault(o => Named(o.Id, o.Name, segments[1]))?.Id,
            "generators" => project.Generators.FirstOrDefault(g => Named(g.Id, g.Name, segments[1]))?.Id,
            "playlists" => project.Playlists.FirstOrDefault(p => Named(p.Id, p.Name, segments[1]))?.Id,
            _ => null,
        };
        if (kind == "layers")
        {
            if (segments.Length < 4)
                return OscRoute.Unknown("/playmap/layers/<canvas>/<layer>/<field>");
            var canvas = project.Canvases.FirstOrDefault(c => Named(c.Id, c.Name, segments[1]));
            var layer = canvas?.Layers.FirstOrDefault(l => Named(l.Id, l.Name, segments[2]));
            if (layer is null)
                return OscRoute.Unknown($"no layer {segments[2]} in {segments[1]}");
            return SetOf(layer.Id, string.Join('.', segments.Skip(3)), message, project);
        }
        if (id is null)
            return OscRoute.Unknown($"no {kind.TrimEnd('s')} named {segments[1]}");
        switch (kind)
        {
            case "generators":
                if (message.Args.Length != 1)
                    return OscRoute.Unknown("/playmap/generators/<name>/<param> <value>");
                return new OscRoute(OscRouteKind.Lines, [$"GENERATOR SET {id} {WireGrammar.Quote(segments[2])} {WireGrammar.Quote(Word(message.Args[0]))}"], null, "");
            case "playlists":
            {
                var verb = segments[2].ToLowerInvariant();
                var n = message.Args.Length > 0 && message.Args[0].IsNumber ? " #" + ((long)message.Args[0].Number).ToString(CultureInfo.InvariantCulture) : "";
                return verb switch
                {
                    "go" => new OscRoute(OscRouteKind.Lines, [$"PLAYLIST GO {id}{n}"], null, ""),
                    "cue" when n.Length > 0 => new OscRoute(OscRouteKind.Lines, [$"PLAYLIST CUE {id}{n}"], null, ""),
                    "next" => new OscRoute(OscRouteKind.Lines, [$"PLAYLIST NEXT {id}"], null, ""),
                    "prev" => new OscRoute(OscRouteKind.Lines, [$"PLAYLIST PREV {id}"], null, ""),
                    _ => OscRoute.Unknown($"/playmap/playlists/<name>/go|next|prev|cue <n> — not {segments[2]}"),
                };
            }
            default:
                return SetOf(id, string.Join('.', segments.Skip(2)), message, project);
        }
    }

    /// <summary>A name in an address: the id, the name, or the name with OSC's reserved characters as '_'.</summary>
    private static bool Named(string id, string name, string word) =>
        string.Equals(id, word, StringComparison.Ordinal) || name.Equals(word, StringComparison.OrdinalIgnoreCase)
        || Sanitise(name).Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>A name as an address part: space # * , / ? [ ] { } become '_'.</summary>
    public static string Sanitise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Create(name.Length, name, (span, n) =>
        {
            for (var i = 0; i < n.Length; i++)
                span[i] = n[i] is ' ' or '#' or '*' or ',' or '/' or '?' or '[' or ']' or '{' or '}' ? '_' : n[i];
        });
    }
}
