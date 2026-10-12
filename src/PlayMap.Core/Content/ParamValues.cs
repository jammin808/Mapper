using PlayMap.Core.Model;

namespace PlayMap.Core.Content;

/// <summary>Typed reads of a generator's parameters against its schema: an operator's value, clamped to the
/// parameter's range, or the catalogue default. Shared by every generator so one rule reads them all.</summary>
public static class ParamValues
{
    /// <summary>The operator's numeric value for a parameter, clamped to its range; false when unset or not a number.</summary>
    public static bool TryNumber(ParamSet set, GeneratorKind kind, string name, out double value)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(kind);
        value = 0;
        if (set.Get(name) is not { } text
            || !double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            || !double.IsFinite(d))
            return false;
        value = kind.Param(name) is { } p && p.Max > p.Min ? Math.Clamp(d, p.Min, p.Max) : d;
        return true;
    }

    /// <summary>The catalogue default of a numeric parameter (0 when the kind has no such parameter).</summary>
    public static double DefaultNumber(GeneratorKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind.Param(name) is { } p
            && double.TryParse(p.Default, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : 0;
    }

    /// <summary>A number: the operator's (clamped) or the default.</summary>
    public static double Number(ParamSet set, GeneratorKind kind, string name) =>
        TryNumber(set, kind, name, out var v) ? v : DefaultNumber(kind, name);

    /// <summary>A whole number: the operator's (clamped, rounded) or the default.</summary>
    public static int Integer(ParamSet set, GeneratorKind kind, string name) => (int)Math.Round(Number(set, kind, name));

    /// <summary>A switch: the operator's ("true"/"false", "on"/"off", "1"/"0") or the default.</summary>
    public static bool Flag(ParamSet set, GeneratorKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(kind);
        var text = set.Get(name) ?? kind.Param(name)?.Default ?? "false";
        return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("on", StringComparison.OrdinalIgnoreCase) || text == "1";
    }

    /// <summary>A choice (case-folded to the schema's spelling) or the default.</summary>
    public static string Choice(ParamSet set, GeneratorKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(kind);
        var spec = kind.Param(name);
        var text = set.Get(name);
        if (spec is not null && text is not null)
            foreach (var c in spec.Choices)
                if (c.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
                    return c;
        return spec?.Default ?? "";
    }

    /// <summary>Text (a colour, a colour list, a label) or the default.</summary>
    public static string Text(ParamSet set, GeneratorKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(kind);
        return set.Get(name) ?? kind.Param(name)?.Default ?? "";
    }
}
