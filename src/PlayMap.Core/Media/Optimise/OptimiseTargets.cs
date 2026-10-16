using PlayMap.Core.Media.Hap;

namespace PlayMap.Core.Media.Optimise;

/// <summary>The targets this node's encoder makes, by the word the wire and the project use ("hap", "hap-alpha", "hap-q",
/// "hap-q-alpha"), and the HAP codec each one writes. A word outside the table is no target.</summary>
public static class OptimiseTargets
{
    /// <summary>Every target word this encoder makes, in the order the wire lists them.</summary>
    public static readonly IReadOnlyList<string> Words = ["hap", "hap-alpha", "hap-q", "hap-q-alpha"];

    /// <summary>The codec a target word writes.</summary>
    public static bool TryCodec(string? word, out HapCodec codec)
    {
        switch (word?.Trim().ToLowerInvariant())
        {
            case "hap":
                codec = HapCodec.Hap;
                return true;
            case "hap-alpha":
                codec = HapCodec.HapAlpha;
                return true;
            case "hap-q":
                codec = HapCodec.HapQ;
                return true;
            case "hap-q-alpha":
                codec = HapCodec.HapQAlpha;
                return true;
            default:
                codec = default;
                return false;
        }
    }

    /// <summary>The target word of a codec this encoder makes ("" for HAP Alpha-Only, HAP R and HAP HDR).</summary>
    public static string Word(HapCodec codec) => codec switch
    {
        HapCodec.Hap => "hap",
        HapCodec.HapAlpha => "hap-alpha",
        HapCodec.HapQ => "hap-q",
        HapCodec.HapQAlpha => "hap-q-alpha",
        _ => "",
    };

    /// <summary>The words a refusal names for targets the wire's grammar takes but this round does not make.</summary>
    public static string NotMade(string word) => word.Trim().ToLowerInvariant() switch
    {
        "rechunk" or "strip" or "snappy" => "the lossless HAP rewriter arrives next round: re-encode with TO hap-q, or keep the file",
        "hap-r" => "a HAP R encoder arrives next round: TO hap-q-alpha keeps alpha on the GPU",
        "hap-hdr" => "a HAP HDR encoder arrives next round: HDR sources are kept as they are",
        "hap-alpha-only" => "a HAP Alpha-Only encoder arrives next round",
        _ => $"'{word}' is none of {string.Join(", ", Words)}",
    };
}
