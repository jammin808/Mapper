using System.Collections.Immutable;

namespace PlayMap.Core.Model;

public enum EncodeQuality { Fast, Show, Archive }
public enum OptimiseMode { Auto, Ask, Never }
public enum RuleMode { Inherit, Auto, Ask, Never }

/// <summary>A derived file of a library item: same content, another codec, content-addressed; the source is never changed.</summary>
/// <param name="To">"hap", "hap-alpha", "hap-q", "hap-q-alpha", "rechunk".</param>
/// <param name="Recipe">The canonical recipe JSON (sorted keys, invariant).</param>
/// <param name="Key">"ok:" + 32 hex of (source hash, recipe, encoder version).</param>
public sealed record MediaVersion(string Id, string To, string Recipe, string Key, string ContentHash, long SizeBytes, MediaInfo Info,
    VersionQuality Quality, string Encoder, string MadeOn, long CreatedUnixMs) : IEntity
{
    /// <summary>Below the quality floor: never picked by "auto" until a person names it.</summary>
    public bool Review { get; init; }
}

/// <summary>How close a version is to its source, sampled.</summary>
public sealed record VersionQuality(double PsnrAvg, double PsnrMin, int Sampled, long WorstAtMs)
{
    public static readonly VersionQuality None = new(0, 0, 0, 0);
}

/// <summary>Which items a rule takes; an unset field matches anything.</summary>
public sealed record OptimiseMatch
{
    public ImmutableArray<string> Codecs { get; init; } = [];
    public ImmutableArray<MediaKind> Kinds { get; init; } = [];
    public bool? Alpha { get; init; }
    public bool? Hdr { get; init; }
    public int MinBitDepth { get; init; }
    public int MinWidth { get; init; }
    public bool? CpuDecode { get; init; }
    public int MaxChunks { get; init; }
    public ImmutableArray<string> Tags { get; init; } = [];
}

/// <summary>A show rule: items it matches are optimised to a target in a mode.</summary>
public sealed record OptimiseRule(string Name, OptimiseMatch When, string To, RuleMode Mode)
{
    public EncodeQuality? Quality { get; init; }
}

/// <summary>The show's optimise mode and its own rules, consulted before the built-in table (wp2-optimise's
/// <c>OptimiseRules.BuiltIn</c>); Targets = node ids the show plays on, [] = every PlayMap node heard.</summary>
public sealed record OptimiseSettings
{
    public static readonly OptimiseSettings Default = new();
    public OptimiseMode Mode { get; init; } = OptimiseMode.Ask;
    public ImmutableArray<OptimiseRule> Rules { get; init; } = [];
    public EncodeQuality Quality { get; init; } = EncodeQuality.Show;
    public ImmutableArray<string> Targets { get; init; } = [];
}

/// <summary>How to read an item's colour when its tags are missing or wrong: "auto" values follow the file, then the codec's rule.</summary>
/// <param name="Transfer">"auto" | "sdr" | "pq" | "hlg" | "linear".</param><param name="WhiteNits">0 = the rule's (203 for HAP HDR).</param>
public sealed record ColorOverride(string Transfer, double WhiteNits, string Range, string Matrix)
{
    public static readonly ColorOverride Auto = new("auto", 0, "auto", "auto");
}
