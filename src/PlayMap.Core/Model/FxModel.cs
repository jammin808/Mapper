using System.Collections.Immutable;

namespace PlayMap.Core.Model;

public enum FxBlend { Normal, Add, Multiply, Screen, Difference }
public enum FxFormat { Builtin, Isf, Ffgl }

/// <summary>One effect in a chain: a kind from the FX catalogue ("fx.blur", "isf:fa1b2c3d4e", "ffgl:RM01") with its parameters,
/// how its result mixes over its input, at what render scale, and the show-time origin its TIME counts from.</summary>
/// <param name="Mix">0..1.</param><param name="Scale">0.25..1.</param><param name="OriginNs">0 = the owner's origin.</param>
public sealed record FxSlot(string Id, string Kind, string Name, ParamSet Params, bool Bypass, double Mix, FxBlend Blend,
    double Scale, long OriginNs) : IEntity
{
    public ImmutableArray<ParamBinding> Bindings { get; init; } = [];
}

/// <summary>The effects on an item, layer, canvas, surface or output, applied in order.</summary>
public sealed record FxChain(ImmutableArray<FxSlot> Slots)
{
    public static readonly FxChain Empty = new([]);
    public bool IsEmpty => Slots.IsDefaultOrEmpty;
}

/// <summary>An ISF file carried with the project: its name, where it was imported from, and its sources inline (an ISF file is a
/// few kilobytes, so the show carries it whole and every node that opens the show has it). <see cref="ContentHash"/> is the
/// SHA-256 of <see cref="Source"/> and <see cref="VertexSource"/> together (lowercase hex), the asset's identity across shows.</summary>
public sealed record FxAsset(string Id, string Name, FxFormat Format, string Path, string ContentHash) : IEntity
{
    /// <summary>The fragment file (.fs), with its JSON header.</summary>
    public string Source { get; init; } = "";

    /// <summary>The vertex file (.vs), or "" for the default full-screen vertex shader.</summary>
    public string VertexSource { get; init; } = "";
}
