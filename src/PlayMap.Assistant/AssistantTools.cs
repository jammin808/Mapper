using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant;

/// <summary>What became of one proposed line. <see cref="LineStatus.Ran"/> and <see cref="LineStatus.Refused"/>
/// carry the node's own reply; <see cref="LineStatus.Queued"/> has not happened; <see cref="LineStatus.Unknown"/>
/// means the line was handed over but no reply came back (whether it applied is not known).</summary>
public enum LineStatus
{
    Ran,
    Refused,
    Queued,
    NotRun,
    Unknown,
}

public sealed record LineOutcome(string Line, string Verb, VerbRisk Risk, LineStatus Status, string Reply, string? ProposalId = null)
{
    public string StatusWord => Status switch
    {
        LineStatus.Ran => "ran",
        LineStatus.Refused => "refused",
        LineStatus.Queued => "queued",
        LineStatus.NotRun => "not_run",
        _ => "unknown",
    };
}

/// <summary>The tools the model is given: a small, typed set. Reads (state, context, project, one entity, the
/// vocabulary), two pure helpers (a recipe's lines, a time as unix ms) and propose_commands — the only tool
/// that changes anything. Every schema is closed (no other properties) so a strict provider holds the model to
/// it, and the toolbox checks every call again, because a local model is not held to anything.</summary>
public static class AssistantTools
{
    public const string GetState = "get_state";
    public const string GetContext = "get_context";
    public const string GetProject = "get_project";
    public const string GetEntity = "get_entity";
    public const string ListVerbs = "list_verbs";
    public const string PlanRecipe = "plan_recipe";
    public const string ResolveTime = "resolve_time";
    public const string ProposeCommands = "propose_commands";

    /// <summary>The most lines one propose_commands call takes.</summary>
    public const int MaxLinesPerCall = 50;

    /// <summary>The project's sections get_project can return on their own.</summary>
    public static IReadOnlyList<string> ProjectSections { get; } =
        ["canvases", "cameras", "generators", "media", "meshes", "outputs", "playlists", "projectors", "scans", "settings", "stage", "surfaces", "viewpoints"];

    private static readonly string[] StageSections = ["surfaces", "meshes", "projectors", "cameras", "viewpoints", "scans"];

    /// <summary>The tool definitions, in a fixed order (they render before the system prompt in the cached
    /// prefix, so they must not move).</summary>
    public static IReadOnlyList<ToolSpec> Specs { get; } =
    [
        new(GetState,
            "The node's STATE right now as JSON: the air (blackout, freeze, pattern), the focused clip, every playlist's transport, outputs and their health, media readiness, the stage, scan, sync, links and peers, the project's name and revision. Use it to check facts before and after you act.",
            Schema("{}", "[]")),
        new(GetContext,
            "The node's Show Bus context as JSON: its light and headline, items, recent journal lines in plain words, the verbs it accepts with their risk, and the peers it hears with their headlines.",
            Schema("{}", "[]")),
        new(GetProject,
            "The project as JSON (the show's document): media, playlists and their items, generators, canvases, the stage (surfaces with their tags and content, meshes, projectors, cameras, viewpoints, scans) and outputs. Name a section to get only that part. Ids for every line you propose come from here or from STATE.",
            Schema("""{"section":{"type":"string","enum":[SECTIONS],"description":"Only this part of the project."}}""".Replace("SECTIONS", string.Join(",", ProjectSections.Select(s => "\"" + s + "\"")), StringComparison.Ordinal), "[]")),
        new(GetEntity,
            "One entity of the project by its id (a surface, output, projector, playlist, item, generator, media item, canvas, layer…) as JSON, with the path where it lives in the project.",
            Schema("""{"id":{"type":"string","description":"The entity's id, exactly as the project or STATE writes it."}}""", """["id"]""")),
        new(ListVerbs,
            "PlayMap's vocabulary as JSON: every verb's name, syntax, what it does, its risk (low, review, high) and whether it changes the show. Name a category to list only that part.",
            Schema("""{"category":{"type":"string","enum":[CATEGORIES],"description":"Only the verbs of this category."}}""".Replace("CATEGORIES", string.Join(",", Enum.GetNames<VerbCategory>().Select(c => "\"" + c.ToLowerInvariant() + "\"")), StringComparison.Ordinal), "[]")),
        new(PlanRecipe,
            "Works out the lines for a common mapping job from the project itself; nothing runs. fill_tagged: every surface with a tag shows a generator (tag, and generator: an existing generator's id or name, or a kind such as particles.field). blend_outputs: solve the 3D blend between projector outputs (outputs: ids, names or numbers; none means every projector output). test_pattern: a test pattern on every projector output (pattern: a pattern kind, default pattern.grid). patterns_off: every output back to the show. The answer has before lines (run them first, then plan again), lines and notes.",
            Schema("""{"recipe":{"type":"string","enum":[RECIPES]},"tag":{"type":"string","description":"fill_tagged: the surface tag, such as window."},"generator":{"type":"string","description":"fill_tagged: a generator id or name, or a generator kind."},"outputs":{"type":"array","items":{"type":"string"},"description":"blend_outputs: the outputs or projectors to blend."},"pattern":{"type":"string","description":"test_pattern: the pattern kind."}}""".Replace("RECIPES", string.Join(",", MappingRecipes.Names.Select(n => "\"" + n + "\"")), StringComparison.Ordinal), """["recipe"]""")),
        new(ResolveTime,
            "A time as the node's unix milliseconds, for a verb's AT: a time of day on the node's clock (\"20:00\", \"20:00:30\", \"8pm\", \"8:15 pm\" — the next one to come, so a time already past today is tomorrow) or an offset from now (\"+90s\", \"+5m\", \"+1h\").",
            Schema("""{"time":{"type":"string","description":"20:00, 8pm, 8:15pm, +90s, +5m or +1h."}}""", """["time"]""")),
        new(ProposeCommands,
            "The only way to change the show. Each line is one PlayMap wire verb, exactly as the vocabulary writes it, with real ids. Every line is checked first (a line that is not a valid verb refuses the whole call and nothing runs). Then, in order: queries and the changes the operator lets run on your word run now through the node's command path, and the result gives the node's own reply (OK or ERR); from the first change that needs a person, that change and every change after it wait on the approvals list, in order, and have not happened. A line that answers ERR stops the lines after it. High-risk lines always wait for a person.",
            Schema("""{"lines":{"type":"array","items":{"type":"string"},"description":"The wire lines, in the order they should run."},"reason":{"type":"string","description":"Why, in a sentence the operator sees beside each line waiting for approval."}}""", """["lines","reason"]""")),
    ];

    private static string Schema(string properties, string required) =>
        "{\"type\":\"object\",\"properties\":" + properties + ",\"required\":" + required + ",\"additionalProperties\":false}";

    internal static IEnumerable<string> StageSectionNames => StageSections;
}

/// <summary>What a tool call came to: the text the model reads back, whether it is an error, whether the call
/// itself was malformed (counts against the model's retries), and the lines that ran or queued.</summary>
internal sealed record ToolOutcome(string Content, bool IsError, bool Invalid, IReadOnlyList<LineOutcome> Lines, bool Cancelled = false)
{
    public static ToolOutcome Ok(string content) => new(content, false, false, []);

    public static ToolOutcome Error(string content) => new(content, true, false, []);

    public static ToolOutcome Malformed(string content) => new(content, true, true, []);
}

/// <summary>Runs the model's tool calls against the node: reads through <see cref="IWireEndpoint"/>, changes
/// only through the gate — validated by <see cref="CommandGate"/>, run through
/// <see cref="IWireEndpoint.HandleAsync"/> when the risk lets it, otherwise queued for a person.</summary>
internal sealed class AssistantToolbox
{
    /// <summary>A tool result longer than this is cut, with a note saying how to get less.</summary>
    public const int MaxResultChars = 64 * 1024;

    /// <summary>Compact, with words as they are (no \u escapes for quotes, dashes or accents): the model and the
    /// journal read these, never an HTML page.</summary>
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IWireEndpoint _endpoint;
    private readonly ProposalQueue _queue;
    private readonly AssistantSettings _settings;
    private readonly SessionInfo _session;
    private readonly string _from;
    private readonly TimeProvider _time;
    private int _batches;

    public AssistantToolbox(IWireEndpoint endpoint, ProposalQueue queue, AssistantSettings settings, SessionInfo session, string from, TimeProvider time)
    {
        _endpoint = endpoint;
        _queue = queue;
        _settings = settings;
        _session = session;
        _from = from;
        _time = time;
    }

    public async Task<ToolOutcome> RunAsync(ToolCallPart call, CancellationToken ct)
    {
        JsonDocument args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            return ToolOutcome.Malformed($"The arguments to {call.Name} are not JSON ({ex.Message}). Send a JSON object as the schema says.");
        }
        using (args)
        {
            var a = args.RootElement;
            if (a.ValueKind != JsonValueKind.Object)
                return ToolOutcome.Malformed($"The arguments to {call.Name} must be a JSON object.");
            return call.Name switch
            {
                AssistantTools.GetState => ToolOutcome.Ok(Cut(_endpoint.State().Json is { Length: > 0 } s ? s : "(no STATE yet)")),
                AssistantTools.GetContext => ToolOutcome.Ok(Cut(_endpoint.Context() is { Length: > 0 } c ? c : "(no context yet)")),
                AssistantTools.GetProject => GetProject(a),
                AssistantTools.GetEntity => GetEntity(a),
                AssistantTools.ListVerbs => ListVerbs(a),
                AssistantTools.PlanRecipe => PlanRecipe(a),
                AssistantTools.ResolveTime => ResolveTime(a),
                AssistantTools.ProposeCommands => await ProposeAsync(a, ct),
                _ => ToolOutcome.Malformed($"There is no tool '{call.Name}'. The tools: {string.Join(", ", AssistantTools.Specs.Select(t => t.Name))}."),
            };
        }
    }

    private ToolOutcome GetProject(JsonElement a)
    {
        var section = OptString(a, "section");
        if (section is not null && !AssistantTools.ProjectSections.Contains(section, StringComparer.OrdinalIgnoreCase))
            return ToolOutcome.Malformed($"'{section}' is not a section. The sections: {string.Join(", ", AssistantTools.ProjectSections)}.");
        if (ProjectNode() is not JsonObject root)
            return ToolOutcome.Error("The project could not be read.");
        if (section is null)
            return ToolOutcome.Ok(Cut(Shrink(root).ToJsonString(Compact)));
        var key = section.ToLowerInvariant();
        var node = AssistantTools.StageSectionNames.Contains(key, StringComparer.Ordinal)
            ? Child(Child(root, "stage"), key)
            : Child(root, key);
        return node is null
            ? ToolOutcome.Ok($"The project has no {key}.")
            : ToolOutcome.Ok(Cut(Shrink(node).ToJsonString(Compact)));
    }

    private ToolOutcome GetEntity(JsonElement a)
    {
        var id = OptString(a, "id");
        if (string.IsNullOrWhiteSpace(id))
            return ToolOutcome.Malformed("get_entity needs an id.");
        if (ProjectNode() is not JsonObject root)
            return ToolOutcome.Error("The project could not be read.");
        if (FindById(root, id.Trim(), "") is not { } found)
            return ToolOutcome.Error($"No entity '{id}' in the project. get_project lists what there is.");
        var answer = new JsonObject { ["path"] = found.Path, ["entity"] = Shrink(found.Node.DeepClone()) };
        return ToolOutcome.Ok(Cut(answer.ToJsonString(Compact)));
    }

    private static ToolOutcome ListVerbs(JsonElement a)
    {
        var category = OptString(a, "category");
        VerbCategory? only = null;
        if (category is not null)
        {
            if (!Enum.TryParse<VerbCategory>(category, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return ToolOutcome.Malformed($"'{category}' is not a category. The categories: {string.Join(", ", Enum.GetNames<VerbCategory>().Select(c => c.ToLowerInvariant()))}.");
            only = parsed;
        }
        var verbs = new JsonArray();
        foreach (var v in VerbCatalog.All.Where(v => only is null || v.Category == only))
        {
            verbs.Add(new JsonObject
            {
                ["name"] = v.Name,
                ["syntax"] = v.Syntax,
                ["words"] = v.Words,
                ["category"] = v.Category.ToString().ToLowerInvariant(),
                ["risk"] = ShowContext.RiskWord(v.Risk),
                ["mutates"] = v.Mutates,
                ["aliases"] = new JsonArray(v.Aliases.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            });
        }
        return ToolOutcome.Ok(verbs.ToJsonString(Compact));
    }

    private ToolOutcome PlanRecipe(JsonElement a)
    {
        var recipe = OptString(a, "recipe");
        if (recipe is null || !MappingRecipes.Names.Contains(recipe, StringComparer.Ordinal))
            return ToolOutcome.Malformed($"plan_recipe needs a recipe: {string.Join(", ", MappingRecipes.Names)}.");
        var project = LoadProject();
        if (project is null)
            return ToolOutcome.Error("The project could not be read.");
        RecipePlan plan;
        switch (recipe)
        {
            case MappingRecipes.FillTaggedName:
                var tag = OptString(a, "tag");
                var generator = OptString(a, "generator");
                if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(generator))
                    return ToolOutcome.Malformed("fill_tagged needs a tag and a generator.");
                plan = MappingRecipes.FillTagged(project, tag, generator);
                break;
            case MappingRecipes.BlendOutputsName:
                if (!TryStrings(a, "outputs", out var outputs, out var why))
                    return ToolOutcome.Malformed(why);
                plan = MappingRecipes.BlendOutputs(project, outputs);
                break;
            case MappingRecipes.TestPatternName:
                plan = MappingRecipes.TestPatternOnProjectors(project, OptString(a, "pattern") is { Length: > 0 } p ? p : "pattern.grid");
                break;
            default:
                plan = MappingRecipes.PatternsOff();
                break;
        }
        var next = plan.Before.Count > 0
            ? "Propose the before lines, read the id the reply answers, then plan again."
            : plan.Lines.Count > 0 ? "Propose these lines with propose_commands to do it." : "Nothing to do: see the notes.";
        var answer = new JsonObject
        {
            ["recipe"] = plan.Recipe,
            ["before"] = Strings(plan.Before),
            ["lines"] = Strings(plan.Lines),
            ["notes"] = Strings(plan.Notes),
            ["next"] = next,
        };
        return ToolOutcome.Ok(answer.ToJsonString(Compact));
    }

    private ToolOutcome ResolveTime(JsonElement a)
    {
        var text = OptString(a, "time");
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        if (!TimeWords.TryResolve(text, now, zone, out var at, out var why))
            return ToolOutcome.Malformed(why);
        var answer = new JsonObject
        {
            ["unixMs"] = at.ToUnixTimeMilliseconds(),
            ["local"] = TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            ["inSeconds"] = Math.Round((at - now).TotalSeconds),
        };
        return ToolOutcome.Ok(answer.ToJsonString(Compact));
    }

    /// <summary>The gate. Every line is checked before any runs: one bad line refuses the call and nothing
    /// runs. Then in order: a query, or a change the settings let run, runs through the endpoint; from the first
    /// change that needs a person, it and every later change queue (in order, one batch); an ERR stops the
    /// lines after it.</summary>
    private async Task<ToolOutcome> ProposeAsync(JsonElement a, CancellationToken ct)
    {
        if (!TryStrings(a, "lines", out var lines, out var why))
            return ToolOutcome.Malformed(why);
        if (lines.Count == 0)
            return ToolOutcome.Malformed("propose_commands needs at least one line.");
        if (lines.Count > AssistantTools.MaxLinesPerCall)
            return ToolOutcome.Malformed(string.Create(CultureInfo.InvariantCulture, $"At most {AssistantTools.MaxLinesPerCall} lines in one call; split the job."));
        var reason = OptString(a, "reason") is { Length: > 0 } r ? r.Trim() : "(no reason given)";
        var project = LoadProject();
        var checks = lines.Select(l => CommandGate.Check(l, project)).ToList();
        if (checks.Any(c => !c.Ok))
        {
            var refused = new JsonArray();
            for (var i = 0; i < checks.Count; i++)
            {
                if (!checks[i].Ok)
                    refused.Add(new JsonObject { ["line"] = lines[i], ["reason"] = checks[i].Reason });
            }
            var answer = new JsonObject
            {
                ["refused"] = refused,
                ["note"] = "Nothing ran and nothing was queued. Fix these lines (list_verbs has every verb's syntax; ids come from get_project) and propose again.",
            };
            return ToolOutcome.Malformed(answer.ToJsonString(Compact));
        }
        _batches++;
        var batch = string.Create(CultureInfo.InvariantCulture, $"{_session.SessionId}/b{_batches}");
        var outcomes = new List<LineOutcome>();
        var gated = false;
        var stopped = false;
        var cancelled = false;
        for (var i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            var spec = c.Spec!;
            if (!stopped && ct.IsCancellationRequested)
            {
                stopped = true;
                cancelled = true;
            }
            if (stopped)
            {
                outcomes.Add(new LineOutcome(c.Line, spec.Name, c.Risk, LineStatus.NotRun, cancelled ? "not run: the request was cancelled" : "not run: an earlier line did not succeed"));
                continue;
            }
            // A query changes nothing, so it runs wherever it stands; a change runs on the model's word only
            // while no earlier line of the call has had to wait for a person.
            if (!spec.Mutates || (!gated && _settings.RunsWithoutAsking(c.Risk)))
            {
                var outcome = await RunLineAsync(c, ct);
                outcomes.Add(outcome);
                if (outcome.Status != LineStatus.Ran)
                {
                    stopped = true;
                    cancelled = ct.IsCancellationRequested;
                }
                continue;
            }
            gated = true;
            var proposal = await _queue.EnqueueAsync(new ProposalDraft(c.Line, spec, reason, _from, _session.SessionId, batch, i), ct);
            outcomes.Add(new LineOutcome(c.Line, spec.Name, c.Risk, LineStatus.Queued, proposal.Summary, proposal.Id));
        }
        return new ToolOutcome(Describe(outcomes), false, false, outcomes, cancelled);
    }

    private async Task<LineOutcome> RunLineAsync(LineCheck c, CancellationToken ct)
    {
        var spec = c.Spec!;
        try
        {
            var reply = await _endpoint.HandleAsync(c.Parsed!, _session, ct);
            return new LineOutcome(c.Line, spec.Name, c.Risk, reply.Ok ? LineStatus.Ran : LineStatus.Refused, ProposalQueue.ReplyWords(reply));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new LineOutcome(c.Line, spec.Name, c.Risk, LineStatus.Unknown, "cancelled while running — whether it applied is not known; check STATE");
        }
        catch (Exception ex)
        {
            return new LineOutcome(c.Line, spec.Name, c.Risk, LineStatus.Unknown, "the node faulted running it (" + ex.Message + ") — whether it applied is not known; check STATE");
        }
    }

    /// <summary>The result the model reads: each line with its status and the node's reply, and one summary
    /// sentence that never calls a queued line done.</summary>
    internal static string Describe(IReadOnlyList<LineOutcome> outcomes)
    {
        var results = new JsonArray();
        foreach (var o in outcomes)
        {
            var row = new JsonObject
            {
                ["line"] = o.Line,
                ["verb"] = o.Verb,
                ["risk"] = ShowContext.RiskWord(o.Risk),
                ["status"] = o.StatusWord,
                ["reply"] = o.Reply,
            };
            if (o.ProposalId is not null)
                row["proposal"] = o.ProposalId;
            results.Add(row);
        }
        var ran = outcomes.Count(o => o.Status == LineStatus.Ran);
        var refused = outcomes.Count(o => o.Status == LineStatus.Refused);
        var queued = outcomes.Where(o => o.Status == LineStatus.Queued).Select(o => o.ProposalId).ToList();
        var notRun = outcomes.Count(o => o.Status == LineStatus.NotRun);
        var unknown = outcomes.Count(o => o.Status == LineStatus.Unknown);
        var parts = new List<string>();
        if (ran > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{ran} ran (the node answered OK)"));
        if (refused > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{refused} refused by the node (ERR — it did not happen)"));
        if (unknown > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{unknown} with no answer (whether it applied is not known)"));
        if (queued.Count > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{queued.Count} waiting for a person to approve ({string.Join(", ", queued)}) — not done yet"));
        if (notRun > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{notRun} not run"));
        var answer = new JsonObject { ["results"] = results, ["summary"] = string.Join("; ", parts) + "." };
        return answer.ToJsonString(Compact);
    }

    private Project? LoadProject()
    {
        try
        {
            var json = _endpoint.ProjectJson().Json;
            return string.IsNullOrWhiteSpace(json) ? null : ProjectJson.Deserialize(json);
        }
        catch (JsonException)
        {
            // An unreadable project leaves the id check to the node; nothing runs on the strength of it.
            return null;
        }
    }

    private JsonNode? ProjectNode()
    {
        try
        {
            var json = _endpoint.ProjectJson().Json;
            return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // The caller answers "could not be read".
            return null;
        }
    }

    private static JsonNode? Child(JsonNode? node, string key) =>
        node is JsonObject o ? o.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value : null;

    private static (JsonNode Node, string Path)? FindById(JsonNode node, string id, string path)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.TryGetPropertyValue("id", out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) && string.Equals(s, id, StringComparison.Ordinal))
                    return (o, path.Length == 0 ? "(the project)" : path);
                foreach (var (key, child) in o)
                {
                    if (child is not null && FindById(child, id, path.Length == 0 ? key : path + "." + key) is { } hit)
                        return hit;
                }
                return null;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is { } item && FindById(item, id, string.Create(CultureInfo.InvariantCulture, $"{path}[{i}]")) is { } hit)
                        return hit;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>The project as the model reads it: long arrays of numbers (a surface's face list, a lattice's
    /// offsets) become a count — noise to the model, and the node keeps them.</summary>
    internal static JsonNode Shrink(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (key, child) in o)
                    obj[key] = child is null ? null : Shrink(child);
                return obj;
            case JsonArray arr when arr.Count > 32 && arr.All(x => x is JsonValue or JsonObject { Count: <= 2 }):
                return JsonValue.Create(string.Create(CultureInfo.InvariantCulture, $"({arr.Count} values, not shown)"));
            case JsonArray arr:
                return new JsonArray(arr.Select(x => x is null ? null : Shrink(x)).ToArray());
            default:
                return node.DeepClone();
        }
    }

    private static string Cut(string text) =>
        text.Length <= MaxResultChars
            ? text
            : string.Create(CultureInfo.InvariantCulture, $"{text[..MaxResultChars]}… (cut at {MaxResultChars} characters: ask for one section or one entity)");

    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    private static string? OptString(JsonElement a, string name) =>
        a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool TryStrings(JsonElement a, string name, out List<string> values, out string why)
    {
        values = [];
        why = "";
        if (!a.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            return true;
        if (p.ValueKind == JsonValueKind.String)
        {
            // A local model often sends one line as a bare string: it is still one line.
            values.Add(p.GetString() ?? "");
            return true;
        }
        if (p.ValueKind != JsonValueKind.Array)
        {
            why = $"{name} must be a list of strings.";
            return false;
        }
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                why = $"{name} must be a list of strings.";
                return false;
            }
            values.Add(item.GetString() ?? "");
        }
        return true;
    }
}
