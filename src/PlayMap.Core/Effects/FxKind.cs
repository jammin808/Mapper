using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.Core.Effects;

/// <summary>What a kind does that a show needs to know: it integrates from frame to frame (persistent buffers — not a pure
/// function of show time, so two nodes are not frame-exact), it reads sound, it reads the surface's geometry, it runs native
/// code, it may flash.</summary>
public readonly record struct FxTraits(bool HistoryDependent, bool ReadsAudio, bool GeometryAware, bool NativeCode, bool MayFlash);

/// <summary>An effect a slot can be: its id ("fx.blur", "isf:fa1b2c3d4e"), format, name, family and categories, role, its
/// parameters as the same <see cref="ParamSpec"/> generators use (so validation, HELP and the inspector are shared), the
/// extra image inputs it reads, and its traits. <see cref="Document"/> is the parsed ISF file it renders.</summary>
public sealed record FxKind(string Id, FxFormat Format, string Name, string Family, ImmutableArray<string> Categories, IsfRole Role,
    ImmutableArray<ParamSpec> Params, ImmutableArray<string> ImageInputs, FxTraits Traits, string Words, IsfDocument Document)
{
    public ParamSpec? Param(string name) => Params.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A parameter's value with the default filled in.</summary>
    public string Value(ParamSet set, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set.Get(name) ?? Param(name)?.Default ?? "";
    }

    /// <summary>True when every parameter is at its default (an absent value is).</summary>
    public bool AtDefaults(ParamSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        foreach (var p in Params)
            if (set.Get(p.Name) is { } v && !string.Equals(v, p.Default, StringComparison.Ordinal))
                return false;
        return true;
    }
}

/// <summary>Kinds made from ISF files, and an ISF input's uniform value from a slot's parameters.</summary>
public static class FxKinds
{
    /// <summary>The image inputs a chain feeds itself (the picture it is applied to; a transition's two sides).</summary>
    public static bool IsChainImage(string name) => name is "inputImage" or "startImage" or "endImage";

    public static FxKind FromIsf(string id, FxFormat format, string name, string family, string words, IsfDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var ps = ImmutableArray.CreateBuilder<ParamSpec>();
        var images = ImmutableArray.CreateBuilder<string>();
        foreach (var input in doc.Inputs)
        {
            var label = input.Label.Length > 0 ? input.Label : input.Name;
            switch (input.Type)
            {
                case IsfInputType.Float:
                    {
                        var (min, max, def) = Range(input, 0, 0, 1, 0);
                        ps.Add(ParamSpec.Num(input.Name, def, min, max, label));
                        break;
                    }
                case IsfInputType.Long when !input.Values.IsDefaultOrEmpty:
                    {
                        var choices = Choices(input);
                        var def = input.Default.IsDefaultOrEmpty ? 0 : (int)Math.Round(input.Default[0]);
                        var at = input.Values.IndexOf(def);
                        ps.Add(ParamSpec.Pick(input.Name, choices[at < 0 ? 0 : at], label, [.. choices]));
                        break;
                    }
                case IsfInputType.Long:
                    {
                        var (min, max, def) = Range(input, 0, 0, 10, 0);
                        ps.Add(ParamSpec.Int(input.Name, (int)Math.Round(def), (int)Math.Floor(min), (int)Math.Ceiling(max), label));
                        break;
                    }
                case IsfInputType.Bool or IsfInputType.Event:
                    ps.Add(ParamSpec.Flag(input.Name, !input.Default.IsDefaultOrEmpty && input.Default[0] > 0, label));
                    break;
                case IsfInputType.Point2D:
                    {
                        var (minX, maxX, defX) = Range(input, 0, 0, 1, 0);
                        var (minY, maxY, defY) = Range(input, 1, 0, 1, 0);
                        ps.Add(ParamSpec.Num(input.Name + ".x", defX, minX, maxX, label + " x"));
                        ps.Add(ParamSpec.Num(input.Name + ".y", defY, minY, maxY, label + " y"));
                        break;
                    }
                case IsfInputType.Color:
                    ps.Add(ParamSpec.Col(input.Name, Hex(input.Default), label));
                    break;
                default:
                    if (!IsChainImage(input.Name))
                        images.Add(input.Name);
                    break;
            }
        }
        var traits = new FxTraits(doc.HistoryDependent, doc.ReadsAudio, false, format == FxFormat.Ffgl, false);
        return new FxKind(id, format, name, family, doc.Categories, doc.Role, ps.ToImmutable(), images.ToImmutable(), traits, words, doc);
    }

    /// <summary>The choices a "long" input shows: its labels when they are distinct and match its values, else the values.</summary>
    public static ImmutableArray<string> Choices(IsfInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Labels.IsDefaultOrEmpty && input.Labels.Length == input.Values.Length
            && input.Labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() == input.Labels.Length && input.Labels.All(l => l.Trim().Length > 0))
            return input.Labels;
        return input.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>An input's uniform value from the slot's parameters (defaults filled in), as up to four floats: one for float,
    /// long and bool (1 = true), two for point2D, four for color. False for a picture input.</summary>
    public static bool TryUniform(IsfInput input, FxKind kind, ParamSet set, Span<float> value)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(set);
        value.Clear();
        switch (input.Type)
        {
            case IsfInputType.Float:
                value[0] = (float)Number(kind.Value(set, input.Name));
                return true;
            case IsfInputType.Long when !input.Values.IsDefaultOrEmpty:
                {
                    var choice = kind.Value(set, input.Name);
                    var choices = Choices(input);
                    var i = choices.IndexOf(choice, StringComparer.OrdinalIgnoreCase);
                    value[0] = i >= 0 && i < input.Values.Length && choices == input.Labels ? input.Values[i] : (float)Number(choice);
                    return true;
                }
            case IsfInputType.Long:
                value[0] = (float)Math.Round(Number(kind.Value(set, input.Name)));
                return true;
            case IsfInputType.Bool or IsfInputType.Event:
                value[0] = string.Equals(kind.Value(set, input.Name), "true", StringComparison.Ordinal) ? 1 : 0;
                return true;
            case IsfInputType.Point2D:
                value[0] = (float)Number(kind.Value(set, input.Name + ".x"));
                value[1] = (float)Number(kind.Value(set, input.Name + ".y"));
                return true;
            case IsfInputType.Color:
                {
                    var (r, g, b) = ColorText.TryParse(kind.Value(set, input.Name), out var rgb) ? ColorText.ToFloats(rgb) : (0f, 0f, 0f);
                    value[0] = r;
                    value[1] = g;
                    value[2] = b;
                    value[3] = input.Default.Length >= 4 ? (float)input.Default[3] : 1f;
                    return true;
                }
            default:
                return false;
        }
    }

    private static double Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : 0;

    /// <summary>(min, max, default) of one component; a missing bound takes the fallback and widens to hold the default.</summary>
    private static (double Min, double Max, double Default) Range(IsfInput input, int component, double defFallback, double maxFallback, double minFallback)
    {
        static double? At(ImmutableArray<double> a, int i) => !a.IsDefaultOrEmpty && i < a.Length ? a[i] : null;
        var def = At(input.Default, component) ?? defFallback;
        var min = At(input.Min, component) ?? Math.Min(minFallback, def);
        var max = At(input.Max, component) ?? Math.Max(maxFallback, def > 0 ? def * 2 : maxFallback);
        if (min > max)
            (min, max) = (max, min);
        if (max - min < 1e-9)
            max = min + 1;
        return (min, max, Math.Clamp(def, min, max));
    }

    private static string Hex(ImmutableArray<double> rgba)
    {
        if (rgba.IsDefaultOrEmpty || rgba.Length < 3)
            return "#FFFFFF";
        static int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return ColorText.Format((C(rgba[0]) << 16) | (C(rgba[1]) << 8) | C(rgba[2]));
    }
}
