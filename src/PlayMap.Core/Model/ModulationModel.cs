namespace PlayMap.Core.Model;

public enum BindingMode { Add, Scale, Replace, Rate }
public enum BindingCurve { Linear, Ease, Step, Exponential, Logarithmic, SCurve }

/// <summary>A modulation source driving one numeric parameter, evaluated per frame on the render thread; the project keeps the
/// base value and is never written per frame. Source names: "audio:bass~10/250", "beat:pulse", "lfo:sine/4b/0", "sting".</summary>
public sealed record ParamBinding(string Param, string Source, double Amount, double Offset, BindingCurve Curve, double SmoothSeconds)
{
    public BindingMode Mode { get; init; } = BindingMode.Add;
    /// <summary>−1: SmoothSeconds both ways.</summary>
    public double AttackSeconds { get; init; } = -1;
    public double ReleaseSeconds { get; init; } = -1;
    /// <summary>REPLACE: fractions of the parameter's range.</summary>
    public double RangeMin { get; init; }
    public double RangeMax { get; init; } = 1;
    public bool Invert { get; init; }
    /// <summary>0 = continuous.</summary>
    public int Steps { get; init; }
    /// <summary>RATE: the leader ns the integral starts at (stamped by the verb).</summary>
    public long AnchorNs { get; init; }
    public bool Enabled { get; init; } = true;
}
