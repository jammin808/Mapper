using PlayMap.Core.Model;

namespace PlayMap.Core.Modulation;

/// <summary>A binding's effect on a number, given its source's value now: u = curve(clamp(invert(s) − Offset, 0, 1)), quantised to
/// Steps levels when set, then ADD: base + Amount·u·(max − min); SCALE: base·lerp(1 − |Amount|, 1, u) (quiet pulls the number
/// down); REPLACE: the range's own min + u·(its width), the range given as fractions of the parameter's (RangeMin…RangeMax). The
/// result is clamped to the parameter's range. RATE is not evaluated on this node (the number holds its base). Pure.</summary>
public static class BindingEval
{
    private static readonly double E4 = Math.Exp(4) - 1;

    /// <summary>The number the binding makes from <paramref name="baseValue"/> when its source reads <paramref name="source01"/>.</summary>
    public static double Apply(ParamBinding binding, double source01, double baseValue, double min, double max)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.Enabled || binding.Mode == BindingMode.Rate || !double.IsFinite(source01))
            return baseValue;
        var u = Shape(binding, source01);
        var span = max - min;
        var v = binding.Mode switch
        {
            BindingMode.Scale => baseValue * (1 - Math.Abs(binding.Amount) + Math.Abs(binding.Amount) * u),
            BindingMode.Replace => min + span * (binding.RangeMin + u * (binding.RangeMax - binding.RangeMin)),
            _ => baseValue + binding.Amount * u * span,
        };
        return Math.Clamp(v, Math.Min(min, max), Math.Max(min, max));
    }

    /// <summary>The binding's u: the source inverted, offset, clamped, quantised and curved.</summary>
    public static double Shape(ParamBinding binding, double source01)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var s = Math.Clamp(source01, 0, 1);
        if (binding.Invert)
            s = 1 - s;
        var u = Math.Clamp(s - binding.Offset, 0, 1);
        if (binding.Steps >= 2)
            u = Math.Round(u * (binding.Steps - 1)) / (binding.Steps - 1);
        else if (binding.Steps == 1)
            u = u >= 0.5 ? 1 : 0;
        return Curve(binding.Curve, u);
    }

    public static double Curve(BindingCurve curve, double u) => curve switch
    {
        BindingCurve.Ease => u * u * (3 - 2 * u),
        BindingCurve.Step => u >= 0.5 ? 1 : 0,
        BindingCurve.Exponential => (Math.Exp(4 * u) - 1) / E4,
        BindingCurve.Logarithmic => Math.Log(1 + E4 * u) / 4,
        BindingCurve.SCurve => u * u * u * (u * (6 * u - 15) + 10),
        _ => u,
    };
}
