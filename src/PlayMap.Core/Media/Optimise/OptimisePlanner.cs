using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;

namespace PlayMap.Core.Media.Optimise;

/// <summary>What the probe says about a file, beyond <see cref="MediaInfo"/>: its bit depth, its transfer ("sdr", "pq", "hlg",
/// "linear"), whether its primaries are wide (BT.2020), whether it is long-GOP (frames that need others to decode), and for a
/// HAP file its chunk count.</summary>
public sealed record ProbeFacts(MediaInfo Info, MediaKind Kind, int BitDepth, string Transfer, bool WideGamut, bool LongGop, int HapChunks)
{
    /// <summary>A probe from the library facts alone: 8-bit SDR, long-GOP when the codec is one of the long-GOP family.</summary>
    public static ProbeFacts From(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ProbeFacts(item.Info, item.Kind, BitDepthOf(item.Info.PixelFormat), "sdr", false,
            OptimisePlanner.LongGopCodecs.Contains(item.Info.VideoCodec), 0);
    }

    /// <summary>The bit depth a pixel format's name says (yuv420p10le → 10, p016 → 16, rgba64 → 16; 8 otherwise).</summary>
    public static int BitDepthOf(string pixelFormat)
    {
        var f = pixelFormat ?? "";
        foreach (var depth in new[] { 16, 14, 12, 10, 9 })
        {
            var d = depth.ToString(CultureInfo.InvariantCulture);
            if (f.Contains("p" + d, StringComparison.Ordinal) || f.EndsWith(d + "le", StringComparison.Ordinal) || f.EndsWith(d + "be", StringComparison.Ordinal))
                return depth;
        }
        if (f.StartsWith("rgba64", StringComparison.Ordinal) || f.StartsWith("rgb48", StringComparison.Ordinal) || f.StartsWith("gray16", StringComparison.Ordinal))
            return 16;
        if (f.Contains("f16", StringComparison.Ordinal) || f.Contains("f32", StringComparison.Ordinal))
            return 16;
        return 8;
    }

    /// <summary>HDR: a PQ or HLG transfer, linear light, or 10 bits and more in a wide gamut.</summary>
    public bool Hdr => Transfer is "pq" or "hlg" or "linear" || (BitDepth >= 10 && WideGamut);
}

/// <summary>What a node the show plays on decodes: the block families its GPU samples, the codecs it decodes in hardware, its
/// cores.</summary>
public sealed record TargetFacts(string NodeId, BlockFormats Blocks, ImmutableArray<string> HwCodecs, int Cores);

/// <summary>What the planner decides for an item: leave it as it is, make a version now, propose one to a person, or nothing
/// at all (the project's mode is never, or the item is not a picture this encoder reads). <see cref="Decision.Lossless"/> is the
/// lossless rewriter's, which arrives next round: no rule decides it this round.</summary>
public enum Decision
{
    Keep,
    Lossless,
    Encode,
    Ask,
    Never,
}

/// <summary>The decision, the recipe when there is one, the rule that decided and its words.</summary>
public sealed record PlanResult(Decision Decision, Recipe? Recipe, string Rule, string Words);

/// <summary>Optimise-on-import's rules: the show's own rules first (<see cref="OptimiseSettings.Rules"/>), then the built-in
/// table in its order; the first rule that matches decides. A rule that names a target makes the decision the project's mode
/// gives it (auto → encode, ask → ask, never → never), or its own mode when it has one. Pure: the same item, settings and
/// targets decide the same everywhere.</summary>
public static class OptimisePlanner
{
    /// <summary>Codecs whose frames depend on other frames: their decode cost is the CPU's unless the node has a hardware decoder.</summary>
    public static readonly ImmutableHashSet<string> LongGopCodecs = ["h264", "hevc", "av1", "vp9", "vp8", "mpeg2video", "mpeg4"];

    /// <summary>Intra codecs that decode on the CPU at every size.</summary>
    public static readonly ImmutableHashSet<string> CpuIntraCodecs = ["prores", "dnxhd", "cfhd", "mjpeg", "qtrle", "png", "tiff", "ffv1", "notchlc", "dxv", "apv", "rawvideo"];

    /// <summary>A long-GOP picture this large or larger is moved to HAP Q when a target decodes it on the CPU (1080p).</summary>
    public const long LongGopMinPixels = 1920L * 1080;

    /// <summary>The built-in table, in its order (the names are what STATE and the replies say).</summary>
    public static readonly IReadOnlyList<string> BuiltIn = ["hap-keep", "hdr", "stills", "alpha", "long-gop-hw", "long-gop-small", "long-gop-cpu", "cpu-intra", "fallback"];

    public static PlanResult Decide(ProbeFacts probe, ImmutableArray<string> tags, OptimiseSettings settings, IReadOnlyList<TargetFacts> targets)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(targets);
        if (settings.Mode == OptimiseMode.Never)
            return new PlanResult(Decision.Never, null, "mode", "the project's optimise mode is never");
        foreach (var rule in settings.Rules)
        {
            if (Matches(rule.When, probe, tags, targets))
                return FromRule(rule.Name, rule.To, rule.Mode, rule.Quality ?? settings.Quality, settings, ShowRuleWords(rule));
        }
        var info = probe.Info;
        var codec = info.VideoCodec;
        var cpu = CpuDecoders(codec, targets);
        if (HapCodecs.TryFromName(codec, out _) || codec == "hap")
            return Keep("hap-keep", "already a GPU codec");
        if (probe.Hdr)
            return Keep("hdr", $"HDR source ({HdrWords(probe)}): kept as it is — a HAP HDR encoder arrives next round");
        if (probe.Kind is MediaKind.Image)
            return Keep("stills", "a still uploads once");
        if (probe.Kind is not MediaKind.Video)
            return Keep("fallback", probe.Kind == MediaKind.ImageSequence ? "an image sequence is kept: sequences are optimised next round" : "not a video clip");
        if (info.HasAlpha)
            return FromRule("alpha", "hap-q-alpha", RuleMode.Inherit, settings.Quality, settings, $"{codec} with alpha: HAP Q Alpha keeps it on the GPU");
        if (probe.LongGop || LongGopCodecs.Contains(codec))
        {
            if (cpu.Count == 0)
                return Keep("long-gop-hw", "every target node decodes it on the GPU");
            if ((long)info.Width * info.Height < LongGopMinPixels)
                return Keep("long-gop-small", string.Create(CultureInfo.InvariantCulture, $"{info.Width}×{info.Height} {codec} decodes on the CPU at little cost"));
            return FromRule("long-gop-cpu", "hap-q", RuleMode.Inherit, settings.Quality, settings,
                $"{Names(cpu)} decode{(cpu.Count == 1 ? "s" : "")} this {codec} on the CPU: HAP Q plays on the GPU");
        }
        if (CpuIntraCodecs.Contains(codec))
            return FromRule("cpu-intra", "hap-q", RuleMode.Inherit, settings.Quality, settings, $"{codec} decodes on the CPU here: HAP Q plays on the GPU");
        return Keep("fallback", "no rule asks for a change");
    }

    /// <summary>The plan when a person names the target: encode it, unless the item is not a video clip, is HAP already in that
    /// codec, or is HDR (whose range a SDR HAP codec would lose: refused with the words).</summary>
    public static PlanResult Forced(ProbeFacts probe, string to, EncodeQuality quality)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!OptimiseTargets.TryCodec(to, out var codec))
            return new PlanResult(Decision.Never, null, "asked", OptimiseTargets.NotMade(to));
        if (probe.Kind != MediaKind.Video)
            return new PlanResult(Decision.Never, null, "asked", "only video clips are optimised this round");
        if (HapCodecs.TryFromName(probe.Info.VideoCodec, out var have) && have == codec)
            return new PlanResult(Decision.Keep, null, "asked", $"it is {HapCodecs.Words(codec)} already");
        if (probe.Hdr)
            return new PlanResult(Decision.Never, null, "asked", $"HDR source ({HdrWords(probe)}): {HapCodecs.Words(codec)} would clip its range — a HAP HDR encoder arrives next round");
        return new PlanResult(Decision.Encode, Recipe.For(OptimiseTargets.Word(codec), quality), "asked", $"asked for {HapCodecs.Words(codec)}");
    }

    private static PlanResult Keep(string rule, string words) => new(Decision.Keep, null, rule, words);

    private static PlanResult FromRule(string name, string to, RuleMode mode, EncodeQuality quality, OptimiseSettings settings, string words)
    {
        if (to.Equals("keep", StringComparison.OrdinalIgnoreCase))
            return Keep(name, words);
        if (!OptimiseTargets.TryCodec(to, out _))
            return new PlanResult(Decision.Keep, null, name, $"{words}; {OptimiseTargets.NotMade(to)}");
        var effective = mode switch
        {
            RuleMode.Auto => OptimiseMode.Auto,
            RuleMode.Ask => OptimiseMode.Ask,
            RuleMode.Never => OptimiseMode.Never,
            _ => settings.Mode,
        };
        var decision = effective switch
        {
            OptimiseMode.Auto => Decision.Encode,
            OptimiseMode.Never => Decision.Never,
            _ => Decision.Ask,
        };
        return new PlanResult(decision, Recipe.For(to.ToLowerInvariant(), quality), name, words);
    }

    private static string ShowRuleWords(OptimiseRule rule) => $"the show's rule {rule.Name}";

    private static bool Matches(OptimiseMatch m, ProbeFacts probe, ImmutableArray<string> tags, IReadOnlyList<TargetFacts> targets)
    {
        var info = probe.Info;
        if (m.Codecs.Length > 0 && !m.Codecs.Contains(info.VideoCodec, StringComparer.OrdinalIgnoreCase))
            return false;
        if (m.Kinds.Length > 0 && !m.Kinds.Contains(probe.Kind))
            return false;
        if (m.Alpha is { } alpha && alpha != info.HasAlpha)
            return false;
        if (m.Hdr is { } hdr && hdr != probe.Hdr)
            return false;
        if (probe.BitDepth < m.MinBitDepth || info.Width < m.MinWidth)
            return false;
        if (m.CpuDecode is { } cpu && cpu != (CpuDecoders(info.VideoCodec, targets).Count > 0))
            return false;
        if (m.MaxChunks > 0 && (probe.HapChunks == 0 || probe.HapChunks > m.MaxChunks))
            return false;
        if (m.Tags.Length > 0 && !m.Tags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    /// <summary>The targets that decode a codec on the CPU (no hardware decoder for it); this node alone when none is named.</summary>
    private static List<string> CpuDecoders(string codec, IReadOnlyList<TargetFacts> targets) =>
        targets.Where(t => !t.HwCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase)).Select(t => t.NodeId).ToList();

    private static string Names(List<string> nodes) => nodes.Count switch
    {
        1 => "node " + nodes[0],
        _ => string.Join(", ", nodes.Take(nodes.Count - 1)) + " and " + nodes[^1],
    };

    private static string HdrWords(ProbeFacts p) => p.Transfer switch
    {
        "pq" => "PQ",
        "hlg" => "HLG",
        "linear" => "linear light",
        _ => string.Create(CultureInfo.InvariantCulture, $"{p.BitDepth}-bit wide gamut"),
    };
}
