using System.Globalization;
using System.Text.Json;

namespace PlayMap.Assistant;

/// <summary>STATE in plain words, with no model: the desk's answer to "what is going on?" even offline, in the
/// Show Bus headline's spirit — facts only, read from the fields the node published. A field that is not there
/// is not guessed at: unknown stays unknown.</summary>
public static class StateExplainer
{
    /// <summary>The explanation as lines (one fact or group of facts each); empty for no STATE.</summary>
    public static IReadOnlyList<string> ExplainLines(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
            return [];
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stateJson);
        }
        catch (JsonException)
        {
            return ["STATE could not be read, so nothing is known about the node."];
        }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return ["STATE could not be read, so nothing is known about the node."];
            var lines = new List<string>();
            Identity(r, lines);
            Air(r, lines);
            Video(r, lines);
            Playlists(r, lines);
            Outputs(r, lines);
            Media(r, lines);
            Stage(r, lines);
            Scan(r, lines);
            Sync(r, lines);
            Links(r, lines);
            Peers(r, lines);
            Project(r, lines);
            return lines;
        }
    }

    /// <summary>The explanation as one block of text, a line per fact.</summary>
    public static string Explain(string? stateJson) => string.Join("\n", ExplainLines(stateJson));

    private static void Identity(JsonElement r, List<string> lines)
    {
        var name = Str(r, "name") is { Length: > 0 } n ? n : "This node";
        var light = Str(r, "light");
        var headline = Str(r, "headline");
        var lightWords = light is "red" or "amber" or "green" ? light : "grey (not known)";
        lines.Add(headline is { Length: > 0 } h ? $"{name} is {lightWords}: {h}." : $"{name} is {lightWords}.");
    }

    private static void Air(JsonElement r, List<string> lines)
    {
        if (Obj(r, "air") is not { } air)
            return;
        if (Bool(air, "blackout") == true)
            lines.Add("Every output is blacked out.");
        if (Bool(air, "frozen") == true)
            lines.Add("Every output is frozen on its frame.");
        if (Str(air, "pattern") is { Length: > 0 } pattern)
            lines.Add($"The test pattern {pattern} is over the outputs.");
        if (Bool(air, "identify") == true)
            lines.Add("The outputs are showing their names and numbers.");
    }

    private static void Video(JsonElement r, List<string> lines)
    {
        if (!r.TryGetProperty("video", out var v))
            return;
        if (v.ValueKind != JsonValueKind.Object)
        {
            lines.Add("Nothing is loaded on the focused playlist.");
            return;
        }
        var tag = Str(v, "tag");
        var file = Str(v, "file") ?? "a clip";
        var who = tag is { Length: > 0 } t ? $"The focused playlist {t}" : "The focused playlist";
        var state = Bool(v, "playing") == true ? "is playing"
            : Bool(v, "ended") == true ? "has ended on"
            : Bool(v, "paused") == true ? "is paused on"
            : "is holding";
        var position = Str(v, "positionText") ?? Clock(Num(v, "positionMs"));
        var length = Str(v, "lengthText") ?? Clock(Num(v, "lengthMs"));
        var remaining = Str(v, "remainingText") ?? Clock(Num(v, "remainingMs"));
        var where = position is not null && length is not null ? $" at {position} of {length}" : "";
        var left = remaining is not null && Bool(v, "ended") != true ? $", {remaining} left" : "";
        var rate = Num(v, "rate") is { } rt && Math.Abs(rt - 1) > 1e-9 ? string.Create(CultureInfo.InvariantCulture, $" at {rt:0.##}× speed") : "";
        var loops = Bool(v, "loops") == true ? " (it loops)" : "";
        lines.Add($"{who} {state} {file}{where}{left}{rate}{loops}.");
    }

    private static void Playlists(JsonElement r, List<string> lines)
    {
        var lists = Arr(r, "playlists");
        if (lists.Count == 0)
            return;
        var idle = 0;
        foreach (var p in lists)
        {
            if (Bool(p, "focused") == true)
                continue;
            var name = Str(p, "name") ?? Str(p, "id") ?? "a playlist";
            var state = Str(p, "state");
            if (state is "playing" or "paused")
            {
                var item = Obj(p, "item") is { } it ? Str(it, "label") ?? Str(it, "id") : null;
                var remaining = Clock(Num(p, "remainingMs"));
                var what = item is not null ? $" {item}" : "";
                var left = remaining is not null ? $", {remaining} left" : "";
                lines.Add($"Playlist {name} is {state}{what}{left}.");
            }
            else
                idle++;
        }
        if (idle > 0)
            lines.Add(idle == 1 ? "One other playlist is not playing." : string.Create(CultureInfo.InvariantCulture, $"{idle} other playlists are not playing."));
    }

    private static void Outputs(JsonElement r, List<string> lines)
    {
        if (!r.TryGetProperty("outputs", out _))
            return;
        var outputs = Arr(r, "outputs");
        if (outputs.Count == 0)
        {
            lines.Add("There are no outputs.");
            return;
        }
        var problems = new List<string>();
        foreach (var o in outputs)
        {
            var name = Str(o, "name") ?? Str(o, "id") ?? "An output";
            if (Bool(o, "open") == false)
            {
                problems.Add($"{name} is closed");
                continue;
            }
            var light = Str(o, "light");
            var dropped = Num(o, "dropped");
            var details = new List<string>();
            if (dropped is > 0)
                details.Add(string.Create(CultureInfo.InvariantCulture, $"{dropped:0} frames dropped"));
            if (Str(o, "pattern") is { Length: > 0 } pattern)
                details.Add($"showing the pattern {pattern}");
            if (light is "red" or "amber")
                problems.Add($"{name} is {light}" + (details.Count > 0 ? " (" + string.Join(", ", details) + ")" : ""));
            else if (light is not "green")
                problems.Add($"{name}'s health is not known" + (details.Count > 0 ? " (" + string.Join(", ", details) + ")" : ""));
            else if (details.Count > 0)
                problems.Add($"{name} is green but {string.Join(", ", details)}");
        }
        var count = outputs.Count == 1 ? "The one output" : string.Create(CultureInfo.InvariantCulture, $"All {outputs.Count} outputs");
        if (problems.Count == 0)
            lines.Add($"{count} {(outputs.Count == 1 ? "is" : "are")} open and green.");
        else
            lines.Add(string.Join("; ", problems) + ".");
    }

    private static void Media(JsonElement r, List<string> lines)
    {
        if (Obj(r, "media") is not { } m)
            return;
        if (Num(m, "missing") is > 0 and var missing)
            lines.Add(missing == 1 ? "One media file is missing on this node." : string.Create(CultureInfo.InvariantCulture, $"{missing:0} media files are missing on this node."));
        else if (Bool(m, "ready") == false)
            lines.Add("The media is not ready yet.");
        // A row on its way is "queued" or "pulling"; one whose state says how it failed ("failed: …") runs no more.
        var rows = Arr(m, "transfers");
        var running = rows.Count(t => Str(t, "state") is "queued" or "pulling");
        if (running > 0)
            lines.Add(running == 1 ? "One media transfer is running." : string.Create(CultureInfo.InvariantCulture, $"{running} media transfers are running."));
        var failed = rows.Where(t => Str(t, "state")?.StartsWith("failed", StringComparison.Ordinal) == true).ToList();
        if (failed.Count == 1)
            lines.Add($"The media transfer of {Str(failed[0], "name") ?? "a file"} {Str(failed[0], "state")}; MEDIA FETCH asks for it again.");
        else if (failed.Count > 1)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{failed.Count} media transfers failed (each row's state says why); MEDIA FETCH asks for them again."));
    }

    private static void Stage(JsonElement r, List<string> lines)
    {
        if (Obj(r, "stage") is not { } s)
            return;
        var parts = new List<string>();
        if (Num(s, "surfaces") is { } surfaces)
            parts.Add(Count(surfaces, "surface", "surfaces"));
        if (Num(s, "meshes") is { } meshes)
            parts.Add("on " + Count(meshes, "mesh", "meshes"));
        var projectors = Arr(s, "projectors");
        var solved = new List<string>();
        var unsolved = new List<string>();
        foreach (var p in projectors)
        {
            var name = Str(p, "name") ?? Str(p, "id") ?? "a projector";
            if (Bool(p, "solved") == true)
                solved.Add(Num(p, "residualPx") is { } px ? string.Create(CultureInfo.InvariantCulture, $"{name} ({px:0.0#} px)") : name);
            else if (Bool(p, "solved") == false)
                unsolved.Add(name);
        }
        var sentence = parts.Count > 0 ? "The stage has " + string.Join(" ", parts) : "The stage";
        if (projectors.Count == 0)
            sentence += parts.Count > 0 ? " and no projectors" : " has no projectors";
        else
        {
            var bits = new List<string>();
            if (solved.Count > 0)
                bits.Add("solved: " + string.Join(", ", solved));
            if (unsolved.Count > 0)
                bits.Add("not solved: " + string.Join(", ", unsolved));
            sentence += (parts.Count > 0 ? "; " : " has ") + Count(projectors.Count, "projector", "projectors") + (bits.Count > 0 ? " (" + string.Join("; ", bits) + ")" : "");
        }
        lines.Add(sentence + ".");
    }

    private static void Scan(JsonElement r, List<string> lines)
    {
        if (!r.TryGetProperty("scan", out var scan) || scan.ValueKind == JsonValueKind.Null)
            return;
        // The scan row's shape belongs to the scan runner; only its state word is read here.
        var state = scan.ValueKind == JsonValueKind.Object ? Str(scan, "state") : null;
        lines.Add(state is { Length: > 0 } st ? $"A scan is {st}." : "A scan is in progress.");
    }

    private static void Sync(JsonElement r, List<string> lines)
    {
        if (Obj(r, "sync") is not { } s)
            return;
        var role = Str(s, "role");
        var groups = Arr(s, "groups").Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()).OfType<string>().ToList();
        if (role == "follower")
        {
            var leader = Str(s, "leader") ?? "its leader";
            var offset = Num(s, "offsetMs") is { } o ? string.Create(CultureInfo.InvariantCulture, $": offset {o:0.0#} ms") : "";
            var rtt = Num(s, "rttMs") is { } t ? string.Create(CultureInfo.InvariantCulture, $", round trip {t:0.0#} ms") : "";
            lines.Add($"It follows {leader} for sync{offset}{rtt}.");
        }
        else if (role == "leader")
            lines.Add(groups.Count > 0 ? "It leads the sync group " + string.Join(", ", groups) + "." : "It leads its sync group.");
    }

    private static void Links(JsonElement r, List<string> lines)
    {
        if (Obj(r, "links") is not { } links)
            return;
        if (Obj(links, "patterns") is { } p)
        {
            var host = Str(p, "host") is { Length: > 0 } h ? " at " + h : "";
            var clip = Str(p, "clip") is { Length: > 0 } c ? ", clip " + c : "";
            lines.Add(Str(p, "state") switch
            {
                "live" => $"The Patterns link{host} is live{clip}.",
                "down" => $"The Patterns link{host} is down.",
                { Length: > 0 } st => $"The Patterns link{host} is {st}.",
                _ => $"The Patterns link{host} is in a state not known.",
            });
        }
        if (Obj(links, "stagewatch") is { } w)
        {
            var host = Str(w, "host") is { Length: > 0 } h ? " at " + h : "";
            var timer = Obj(w, "timer") is { } t
                ? (Str(t, "title") is { Length: > 0 } title ? title + " " : "") + (Str(t, "text") ?? "") + (Str(t, "state") is { Length: > 0 } ts ? " " + ts : "")
                : "";
            var timerWords = timer.Trim().Length > 0 ? ", timer " + timer.Trim() : "";
            lines.Add(Str(w, "state") switch
            {
                "live" => $"The Stagewatch link{host} is live{timerWords}.",
                "down" => $"The Stagewatch link{host} is down.",
                { Length: > 0 } st => $"The Stagewatch link{host} is {st}.",
                _ => $"The Stagewatch link{host} is in a state not known.",
            });
        }
    }

    private static void Peers(JsonElement r, List<string> lines)
    {
        var peers = Arr(r, "peers");
        if (peers.Count == 0)
            return;
        var trouble = peers
            .Where(p => Str(p, "light") is "red" or "amber")
            .Select(p => $"{Str(p, "name") ?? Str(p, "id") ?? "a peer"} is {Str(p, "light")}")
            .ToList();
        var heard = peers.Count == 1 ? "It hears one peer" : string.Create(CultureInfo.InvariantCulture, $"It hears {peers.Count} peers");
        lines.Add(trouble.Count > 0 ? $"{heard}; {string.Join(", ", trouble)}." : $"{heard}, none in trouble.");
    }

    private static void Project(JsonElement r, List<string> lines)
    {
        if (Obj(r, "project") is not { } p)
            return;
        var name = Str(p, "name") is { Length: > 0 } n ? "The project " + n : "The project";
        lines.Add(Bool(p, "dirty") switch
        {
            true => $"{name} has unsaved changes.",
            false => $"{name} is saved.",
            _ => $"{name} is open.",
        });
    }

    private static string Count(double n, string one, string many) =>
        Math.Abs(n - 1) < 1e-9 ? "1 " + one : string.Create(CultureInfo.InvariantCulture, $"{n:0} {many}");

    /// <summary>Milliseconds as m:ss or h:mm:ss; null when not known.</summary>
    internal static string? Clock(double? ms)
    {
        if (ms is not { } v || !double.IsFinite(v) || v < 0)
            return null;
        var t = TimeSpan.FromMilliseconds(Math.Round(v / 1000) * 1000);
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : null;

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

    private static List<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().ToList()
            : [];
}
