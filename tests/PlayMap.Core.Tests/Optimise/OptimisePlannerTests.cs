using System.Collections.Immutable;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Optimise;

/// <summary>The planner's built-in table, one row per rule: the decision, the target, the rule's name and its words, on probe
/// facts given as literals; the HDR row decides before the long-GOP row; show rules come first; the project's mode turns a rule's
/// target into encode, ask or never.</summary>
public class OptimisePlannerTests
{
    private static readonly TargetFacts Cpu = new("a1b2c3d4", BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4, [], 4);
    private static readonly TargetFacts Hw = new("9f8e7d6c", BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4, ["h264", "hevc"], 8);

    private static ProbeFacts Probe(string codec, int w = 1920, int h = 1080, MediaKind kind = MediaKind.Video, bool alpha = false,
        int bits = 8, string transfer = "sdr", bool wide = false, string pix = "yuv420p") =>
        new(new MediaInfo(2_000_000_000, w, h, "25", codec, pix, "aac", 2, 48000, alpha, "mp4"), kind, bits, transfer, wide,
            OptimisePlanner.LongGopCodecs.Contains(codec), 0);

    private static readonly OptimiseSettings Auto = OptimiseSettings.Default with { Mode = OptimiseMode.Auto };

    public static TheoryData<string, ProbeFacts, Decision, string?, string, string> Rows() => new()
    {
        { "hap", Probe("hap_q"), Decision.Keep, null, "hap-keep", "already a GPU codec" },
        { "pq", Probe("hevc", 3840, 2160, bits: 10, transfer: "pq", wide: true, pix: "yuv420p10le"), Decision.Keep, null, "hdr",
            "HDR source (PQ): kept as it is — a HAP HDR encoder arrives next round" },
        { "hlg", Probe("hevc", 3840, 2160, bits: 10, transfer: "hlg"), Decision.Keep, null, "hdr", "HDR source (HLG): kept as it is — a HAP HDR encoder arrives next round" },
        { "wide10", Probe("prores", 3840, 2160, bits: 12, wide: true, pix: "yuv422p12le"), Decision.Keep, null, "hdr",
            "HDR source (12-bit wide gamut): kept as it is — a HAP HDR encoder arrives next round" },
        { "still", Probe("png", 4000, 3000, MediaKind.Image), Decision.Keep, null, "stills", "a still uploads once" },
        { "alpha", Probe("prores", 1920, 1080, alpha: true, pix: "yuva444p10le", bits: 10), Decision.Encode, "hap-q-alpha", "alpha",
            "prores with alpha: HAP Q Alpha keeps it on the GPU" },
        { "h264-cpu", Probe("h264"), Decision.Encode, "hap-q", "long-gop-cpu", "node a1b2c3d4 decodes this h264 on the CPU: HAP Q plays on the GPU" },
        { "h264-small", Probe("h264", 1280, 720), Decision.Keep, null, "long-gop-small", "1280×720 h264 decodes on the CPU at little cost" },
        { "prores", Probe("prores", 3840, 2160, pix: "yuv422p10le", bits: 10), Decision.Encode, "hap-q", "cpu-intra", "prores decodes on the CPU here: HAP Q plays on the GPU" },
        { "sequence", Probe("png", 1920, 1080, MediaKind.ImageSequence), Decision.Keep, null, "fallback", "an image sequence is kept: sequences are optimised next round" },
        { "other", Probe("theora", 1920, 1080), Decision.Keep, null, "fallback", "no rule asks for a change" },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void EachBuiltInRuleDecidesWithItsWords(string row, ProbeFacts probe, Decision decision, string? to, string rule, string words)
    {
        var r = OptimisePlanner.Decide(probe, [], Auto, [Cpu]);
        Assert.True((decision, to, rule, words) == (r.Decision, r.Recipe?.To, r.Rule, r.Words), $"{row}: {r}");
    }

    [Fact]
    public void TheHdrRowDecidesAheadOfTheLongGopRow()
    {
        // A 4K HEVC that a CPU-only node would decode in software: long-GOP alone would make it HAP Q, but it is PQ.
        var pq = Probe("hevc", 3840, 2160, bits: 10, transfer: "pq", wide: true, pix: "yuv420p10le");
        var sdr = pq with { Transfer = "sdr", WideGamut = false, BitDepth = 8 };
        Assert.Equal(("hdr", Decision.Keep), (OptimisePlanner.Decide(pq, [], Auto, [Cpu]) is var a ? (a.Rule, a.Decision) : default));
        Assert.Equal(("long-gop-cpu", Decision.Encode), (OptimisePlanner.Decide(sdr, [], Auto, [Cpu]) is var b ? (b.Rule, b.Decision) : default));
        Assert.True(OptimisePlanner.BuiltIn.ToList().IndexOf("hdr") < OptimisePlanner.BuiltIn.ToList().IndexOf("long-gop-cpu"));
    }

    [Fact]
    public void LongGopIsKeptWhenEveryTargetDecodesItInHardwareAndEncodedWhenOneDoesNot()
    {
        var keep = OptimisePlanner.Decide(Probe("h264"), [], Auto, [Hw]);
        Assert.Equal((Decision.Keep, "long-gop-hw", "every target node decodes it on the GPU"), (keep.Decision, keep.Rule, keep.Words));
        var mixed = OptimisePlanner.Decide(Probe("h264"), [], Auto, [Hw, Cpu]);
        Assert.Equal((Decision.Encode, "hap-q", "node a1b2c3d4 decodes this h264 on the CPU: HAP Q plays on the GPU"),
            (mixed.Decision, mixed.Recipe?.To, mixed.Words));
    }

    [Fact]
    public void TheProjectsModeMakesARuleEncodeAskOrNever()
    {
        var probe = Probe("prores", 3840, 2160);
        Assert.Equal(Decision.Encode, OptimisePlanner.Decide(probe, [], Auto, [Cpu]).Decision);
        Assert.Equal(Decision.Ask, OptimisePlanner.Decide(probe, [], OptimiseSettings.Default, [Cpu]).Decision);
        var never = OptimisePlanner.Decide(probe, [], OptimiseSettings.Default with { Mode = OptimiseMode.Never }, [Cpu]);
        Assert.Equal((Decision.Never, "mode", "the project's optimise mode is never"), (never.Decision, never.Rule, never.Words));
    }

    [Fact]
    public void AShowRuleIsConsultedBeforeTheBuiltInTable()
    {
        var keepTagged = new OptimiseRule("tagged-keep", new OptimiseMatch { Tags = ["#keep"] }, "keep", RuleMode.Inherit);
        var toHap = new OptimiseRule("small-hap", new OptimiseMatch { Codecs = ["h264"] }, "hap", RuleMode.Auto) { Quality = EncodeQuality.Fast };
        var settings = OptimiseSettings.Default with { Rules = [keepTagged, toHap] };
        var tagged = OptimisePlanner.Decide(Probe("h264"), ["#keep"], settings, [Cpu]);
        Assert.Equal((Decision.Keep, "tagged-keep"), (tagged.Decision, tagged.Rule));
        var hap = OptimisePlanner.Decide(Probe("h264"), [], settings, [Cpu]);
        Assert.Equal((Decision.Encode, "small-hap", "hap", EncodeQuality.Fast), (hap.Decision, hap.Rule, hap.Recipe?.To, hap.Recipe?.Quality));
        var unmade = new OptimiseRule("to-r", new OptimiseMatch(), "hap-r", RuleMode.Auto);
        var r = OptimisePlanner.Decide(Probe("h264"), [], settings with { Rules = [unmade] }, [Cpu]);
        Assert.Equal((Decision.Keep, "the show's rule to-r; a HAP R encoder arrives next round: TO hap-q-alpha keeps alpha on the GPU"), (r.Decision, r.Words));
    }

    [Fact]
    public void ANamedTargetEncodesUnlessTheItemIsNotVideoIsThatCodecOrIsHdr()
    {
        Assert.Equal((Decision.Encode, "hap"), OptimisePlanner.Forced(Probe("h264", 640, 360), "hap", EncodeQuality.Fast) is var a ? (a.Decision, a.Recipe!.To) : default);
        Assert.Equal(Decision.Keep, OptimisePlanner.Forced(Probe("hap_q"), "hap-q", EncodeQuality.Show).Decision);
        Assert.Equal("only video clips are optimised this round", OptimisePlanner.Forced(Probe("png", kind: MediaKind.Image), "hap", EncodeQuality.Show).Words);
        var hdr = OptimisePlanner.Forced(Probe("hevc", bits: 10, transfer: "pq"), "hap-q", EncodeQuality.Show);
        Assert.Equal((Decision.Never, "HDR source (PQ): HAP Q would clip its range — a HAP HDR encoder arrives next round"), (hdr.Decision, hdr.Words));
        Assert.Equal("the lossless HAP rewriter arrives next round: re-encode with TO hap-q, or keep the file",
            OptimisePlanner.Forced(Probe("hap"), "rechunk", EncodeQuality.Show).Words);
    }

    [Theory]
    [InlineData("yuv420p", 8)]
    [InlineData("yuv420p10le", 10)]
    [InlineData("yuv422p12le", 12)]
    [InlineData("p016le", 16)]
    [InlineData("rgba64le", 16)]
    [InlineData("rgba", 8)]
    public void TheBitDepthIsReadFromThePixelFormat(string pix, int bits) => Assert.Equal(bits, ProbeFacts.BitDepthOf(pix));

    [Fact]
    public void ThePlanIsTheSameWithTheTagsInAnyOrder()
    {
        ImmutableArray<string> tags = ["b", "a"];
        Assert.Equal(OptimisePlanner.Decide(Probe("prores"), tags, Auto, [Cpu]), OptimisePlanner.Decide(Probe("prores"), [.. tags.Reverse()], Auto, [Cpu]));
    }
}
