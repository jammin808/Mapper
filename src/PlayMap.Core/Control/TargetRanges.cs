using System.Globalization;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.Core.Control;

/// <summary>A target's numeric range: where a control's 0–1 lands, whether it is read in log space, whether it steps by whole
/// numbers, and whether the range is known (an unknown field reads 0–1 and says so).</summary>
public readonly record struct TargetRange(double Min, double Max, bool Log, bool Integer, bool Known)
{
    public static readonly TargetRange Unit = new(0, 1, false, false, true);
    public static readonly TargetRange Unknown = new(0, 1, false, false, false);
}

/// <summary>The ranges of the targets controls hit most (a pure table): opacity 0–1, an output's colour trim, a blend's gamma, a
/// layer's rectangle in its canvas, a generator's parameter from its catalogue, and a verb's own "&lt;lo-hi&gt;" syntax word.</summary>
public static class TargetRanges
{
    public static TargetRange Of(TargetLine target, Project? project)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Field is { } field)
        {
            if (target.Spec.Name == "GENERATOR SET")
                return OfGenerator(project, field.Id, field.Path);
            return OfField(project, field.Id, field.Path);
        }
        if (target.PlaceholderAt >= 0 && SyntaxRange(target, target.PlaceholderAt) is { } r)
            return r;
        return target.Placeholder == Placeholder.Percent ? new TargetRange(0, 1, false, false, true) : TargetRange.Unknown;
    }

    /// <summary>The range of an entity's numeric field by its path.</summary>
    public static TargetRange OfField(Project? project, string id, string path)
    {
        var p = path.ToLowerInvariant();
        switch (p)
        {
            case "opacity" or "master" or "volume":
                return TargetRange.Unit;
            case "color.brightness" or "color.contrast" or "color.gainr" or "color.gaing" or "color.gainb":
                return new TargetRange(0, 2, false, false, true);
            case "color.gamma":
                return new TargetRange(0.2, 5, true, false, true);
            case "color.lift":
                return new TargetRange(-0.5, 0.5, false, false, true);
            case "blend.gamma":
                return new TargetRange(0.5, 4, false, false, true);
            case "blend.blacklevel":
                return new TargetRange(0, 0.25, false, false, true);
            case "rate":
                return new TargetRange(0.05, 20, true, false, true);
        }
        if (p.StartsWith("rect.", StringComparison.Ordinal) && project is not null)
        {
            foreach (var canvas in project.Canvases)
            {
                if (canvas.Layers.Find(id) is null)
                    continue;
                var size = p is "rect.x" or "rect.w" ? canvas.Width : canvas.Height;
                return new TargetRange(0, size, false, false, true);
            }
        }
        if (p.StartsWith("params.values.", StringComparison.Ordinal) && project?.FindGenerator(id) is { } g)
            return OfGenerator(project, g.Id, path["params.values.".Length..]);
        return TargetRange.Unknown;
    }

    private static TargetRange OfGenerator(Project? project, string id, string param)
    {
        var generator = project?.FindGenerator(id) ?? project?.Generators.FirstOrDefault(x => x.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        var spec = GeneratorCatalog.Find(generator?.Kind)?.Params.FirstOrDefault(x => x.Name.Equals(param, StringComparison.OrdinalIgnoreCase));
        if (spec is null || spec.Type is not (ParamType.Number or ParamType.Integer))
            return TargetRange.Unknown;
        return new TargetRange(spec.Min, spec.Max, false, spec.Type == ParamType.Integer, true);
    }

    /// <summary>The verb's own range word at a position ("VIDEO RATE &lt;0.05-20&gt;", "MASTER &lt;0-100&gt;"); a rate reads in log space.</summary>
    private static TargetRange? SyntaxRange(TargetLine target, int wordAt)
    {
        var syntax = target.Spec.Syntax.Split(" | ", StringSplitOptions.TrimEntries)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wordAt >= syntax.Length)
            return null;
        var w = syntax[wordAt].Trim('<', '>', '[', ']');
        var space = w.LastIndexOf(' ');
        if (space >= 0)
            w = w[(space + 1)..];
        var dash = w.IndexOf('-', 1);
        if (dash <= 0)
            return null;
        if (!double.TryParse(w.AsSpan(0, dash), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)
            || !double.TryParse(w.AsSpan(dash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var hi) || hi <= lo)
            return null;
        var log = target.Spec.Name.EndsWith(" RATE", StringComparison.Ordinal) && lo > 0;
        var integer = Math.Abs(lo - Math.Round(lo)) < 1e-9 && Math.Abs(hi - Math.Round(hi)) < 1e-9 && hi - lo >= 2;
        return new TargetRange(lo, hi, log, integer, true);
    }
}
