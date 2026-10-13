using System.Globalization;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant;

/// <summary>A recipe's plan: <see cref="Before"/> lines make what the plan needs and does not have yet (a
/// generator); when there are any, run them and plan again, because the lines that use a new entity need the
/// id its reply answers. <see cref="Lines"/> are the job itself. <see cref="Notes"/> say what was skipped and
/// why. Nothing here runs anything: the lines go through the gate like any other.</summary>
public sealed record RecipePlan(string Recipe, IReadOnlyList<string> Before, IReadOnlyList<string> Lines, IReadOnlyList<string> Notes)
{
    public bool IsEmpty => Before.Count == 0 && Lines.Count == 0;
}

/// <summary>Auto-mapping intents as pure functions of the project: the same project gives the same lines.
/// The desk calls them from a right-click; the assistant through plan_recipe. A recipe never writes a line
/// that would change nothing (the node refuses those) and never names an id the project does not hold.</summary>
public static class MappingRecipes
{
    public const string FillTaggedName = "fill_tagged";
    public const string BlendOutputsName = "blend_outputs";
    public const string TestPatternName = "test_pattern";
    public const string PatternsOffName = "patterns_off";

    public static IReadOnlyList<string> Names { get; } = [BlendOutputsName, FillTaggedName, PatternsOffName, TestPatternName];

    /// <summary>Every surface tagged <paramref name="tag"/> shows <paramref name="generator"/> — an existing
    /// generator's id or name, or a kind ("particles.field", "julia"). A kind with no instance yet plans its
    /// GENERATOR NEW as a before line. A tag matches case-blind and singular or plural ("windows" finds
    /// "window"). Hidden surfaces are shown.</summary>
    public static RecipePlan FillTagged(Project project, string tag, string generator)
    {
        ArgumentNullException.ThrowIfNull(project);
        var notes = new List<string>();
        var surfaces = project.Stage.Surfaces.Where(s => s.Tags.Any(t => TagMatches(t, tag))).ToList();
        if (surfaces.Count == 0)
        {
            var tags = project.Stage.Surfaces.SelectMany(s => s.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            notes.Add(tags.Count == 0
                ? $"No surface is tagged '{tag}' — no surface in this project has a tag yet."
                : $"No surface is tagged '{tag}'. The tags in this project: {string.Join(", ", tags)}.");
            return new RecipePlan(FillTaggedName, [], [], notes);
        }
        var instance = FindGeneratorInstance(project, generator, out var kind);
        if (instance is null)
        {
            if (kind is null)
            {
                notes.Add($"No generator or generator kind is called '{generator}'. GENERATOR KINDS lists the kinds; get_project the generators.");
                return new RecipePlan(FillTaggedName, [], [], notes);
            }
            notes.Add($"There is no {kind.Name} generator yet: run the before line, then plan again with the id it answers.");
            return new RecipePlan(FillTaggedName, [$"GENERATOR NEW {kind.Id} {WireGrammar.Quote(kind.Name)}"], [], notes);
        }
        var source = SourceRef.Generator(instance.Id).ToString();
        var lines = new List<string>();
        foreach (var s in surfaces)
        {
            if (s.Content.Source.Kind == SourceKind.Generator && string.Equals(s.Content.Source.Id, instance.Id, StringComparison.Ordinal))
                notes.Add($"{s.Name} ({s.Id}) already shows {instance.Name}.");
            else
                lines.Add($"SURFACE SOURCE {s.Id} {source}");
            if (!s.Visible)
                lines.Add($"SURFACE SHOW {s.Id} ON");
        }
        return new RecipePlan(FillTaggedName, [], lines, notes);
    }

    /// <summary>Solves the 3D blend between projector outputs: those named in <paramref name="outputs"/> (an
    /// output's id, its name, a projector's id or name, or a number — "2" is the output or projector whose
    /// name ends in 2), or every enabled projector output when none are named. An output still on manual edges
    /// is switched to the automatic blend first. A blend needs two outputs or more.</summary>
    public static RecipePlan BlendOutputs(Project project, IReadOnlyList<string>? outputs)
    {
        ArgumentNullException.ThrowIfNull(project);
        var notes = new List<string>();
        var projectorOutputs = project.Outputs.Where(o => o.Enabled && o.Mode == OutputMode.Projector && o.ProjectorId is not null).ToList();
        List<Output> targets;
        if (outputs is null || outputs.Count == 0)
            targets = projectorOutputs;
        else
        {
            targets = [];
            foreach (var r in outputs)
            {
                var found = ResolveOutputs(project, r);
                if (found.Count == 0)
                    notes.Add($"Nothing in the project answers to '{r}'.");
                foreach (var o in found)
                {
                    if (o.Mode != OutputMode.Projector || o.ProjectorId is null)
                        notes.Add($"{o.Name} ({o.Id}) is not a projector output: its blend is its manual edges, not a solve.");
                    else if (!o.Enabled)
                        notes.Add($"{o.Name} ({o.Id}) is disabled.");
                    else if (!targets.Contains(o))
                        targets.Add(o);
                }
            }
        }
        if (targets.Count < 2)
        {
            notes.Add(targets.Count == 0
                ? "No projector output to blend."
                : $"A blend needs two projector outputs or more that overlap; only {targets[0].Name} ({targets[0].Id}) qualifies.");
            return new RecipePlan(BlendOutputsName, [], [], notes);
        }
        var lines = new List<string>();
        foreach (var o in targets.Where(o => !o.Blend.Auto3D))
            lines.Add($"SET {o.Id} blend.auto3D true");
        // One solve weighs every output that blends automatically together (and one runs at a time): one line.
        lines.Add("BLEND SOLVE ALL");
        return new RecipePlan(BlendOutputsName, [], lines, notes);
    }

    /// <summary>A test pattern (a pattern-family kind: "grid", "pattern.blend", "identify"…) over every enabled
    /// projector output — ALL when those are all the enabled outputs, each by id otherwise.</summary>
    public static RecipePlan TestPatternOnProjectors(Project project, string pattern = "pattern.grid")
    {
        ArgumentNullException.ThrowIfNull(project);
        var kind = GeneratorCatalog.FindLoose(pattern);
        if (kind is null || kind.Family != "pattern")
        {
            var names = GeneratorCatalog.All.Where(k => k.Family == "pattern").Select(k => k.Id).Order(StringComparer.Ordinal);
            return new RecipePlan(TestPatternName, [], [], [$"'{pattern}' is not a test pattern. The patterns: {string.Join(", ", names)}."]);
        }
        var enabled = project.Outputs.Where(o => o.Enabled).ToList();
        var projectors = enabled.Where(o => o.Mode == OutputMode.Projector).ToList();
        if (projectors.Count == 0)
            return new RecipePlan(TestPatternName, [], [], ["No enabled output is a projector output."]);
        IReadOnlyList<string> lines = projectors.Count == enabled.Count
            ? [$"PATTERN {kind.Id} ON ALL"]
            : projectors.Select(o => $"PATTERN {kind.Id} ON {o.Id}").ToList();
        return new RecipePlan(TestPatternName, [], lines, []);
    }

    /// <summary>Every output back to the show from its test pattern.</summary>
    public static RecipePlan PatternsOff() => new(PatternsOffName, [], ["PATTERN OFF ON ALL"], []);

    /// <summary>The outputs a word names: an output's id or name; a projector's id or name (the outputs that
    /// drive it); a number n — the outputs whose name ends in the word n, else the outputs of the projectors
    /// whose name does. Empty when nothing answers.</summary>
    public static IReadOnlyList<Output> ResolveOutputs(Project project, string? reference)
    {
        ArgumentNullException.ThrowIfNull(project);
        var r = (reference ?? "").Trim();
        if (r.Length == 0)
            return [];
        if (project.FindOutput(r) is { } byId)
            return [byId];
        var byName = project.Outputs.Where(o => o.Name.Equals(r, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 0)
            return byName;
        var projector = project.FindProjector(r) ?? project.Stage.Projectors.FirstOrDefault(p => p.Name.Equals(r, StringComparison.OrdinalIgnoreCase));
        if (projector is not null)
            return project.Outputs.Where(o => string.Equals(o.ProjectorId, projector.Id, StringComparison.Ordinal)).ToList();
        if (!int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return [];
        var numbered = project.Outputs.Where(o => EndsWithNumber(o.Name, n)).ToList();
        if (numbered.Count > 0)
            return numbered;
        var projectorIds = project.Stage.Projectors.Where(p => EndsWithNumber(p.Name, n)).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return project.Outputs.Where(o => o.ProjectorId is not null && projectorIds.Contains(o.ProjectorId)).ToList();
    }

    /// <summary>A generator instance named by id or name, or the first instance of a kind; null when there is
    /// none (<paramref name="kind"/> then says whether the word was a kind at all).</summary>
    private static Generator? FindGeneratorInstance(Project project, string? word, out GeneratorKind? kind)
    {
        var w = (word ?? "").Trim();
        if (project.FindGenerator(w) is { } byId)
        {
            kind = GeneratorCatalog.Find(byId.Kind);
            return byId;
        }
        if (project.Generators.FirstOrDefault(g => g.Name.Equals(w, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            kind = GeneratorCatalog.Find(byName.Kind);
            return byName;
        }
        kind = GeneratorCatalog.FindLoose(w);
        if (kind is null)
            return null;
        var id = kind.Id;
        return project.Generators.FirstOrDefault(g => g.Kind.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"window" matches "windows" and "Window"; nothing else is stretched.</summary>
    internal static bool TagMatches(string? tag, string? wanted)
    {
        var a = (tag ?? "").Trim();
        var b = (wanted ?? "").Trim();
        if (a.Length == 0 || b.Length == 0)
            return false;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || (a + "s").Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.Equals(b + "s", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndsWithNumber(string name, int n)
    {
        var words = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var last) && last == n;
    }
}
