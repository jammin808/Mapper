namespace PlayMap.Core.Model;

public enum MorphCurve { Linear, Ease }

/// <summary>A crossfade from <see cref="From"/> to the generator's own Params, on the show clock.</summary>
public sealed record ParamMorph(ParamSet From, long StartNs, long LengthNs, MorphCurve Curve);

/// <summary>A look saved in the show: a kind and its full parameter set.</summary>
public sealed record GeneratorPreset(string Id, string Name, string Kind, ParamSet Params) : IEntity;
