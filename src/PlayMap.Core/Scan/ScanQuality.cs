using System.Globalization;

namespace PlayMap.Core.Scan;

/// <summary>A scan's quality: a 0–100 score, its grade, the measures behind it, the weakest area of the camera's
/// picture, and what to do about it — in plain words.</summary>
public sealed record ScanQualityReport(
    int Score,
    string Grade,
    double LitFraction,
    double DecodedFraction,
    double MeanConfidence,
    double UncertainBitShare,
    double SaturationShare,
    string WorstRegion,
    double WorstRegionDecoded,
    IReadOnlyList<string> Advice,
    string Words);

/// <summary>
/// Scores a decode the way an operator judges one. The score is the decoded share of the lit pixels, weighed
/// by the mean confidence (half of it), less a share for code bits too close to call and for clipped
/// pixels, and a little for the weakest area of a 3 × 3 grid over the camera's picture (an even scan beats
/// one that is perfect on one side and poor on the other). 85 and up is good, 65 usable, 40 poor, below that
/// failed; a camera that hardly sees the projector scores 0.
/// </summary>
public static class ScanQuality
{
    private static readonly string[] Rows3 = ["top", "middle", "bottom"];
    private static readonly string[] Columns3 = ["left", "centre", "right"];

    public static ScanQualityReport Assess(CorrespondenceMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var stats = map.Stats;
        var (worstName, worstDecoded, worstQuality) = WorstRegion(map);
        var advice = new List<string>();
        if (stats.LitFraction < 0.01)
        {
            advice.Add("The camera barely sees this projector's light: aim the camera at the projection or open its exposure.");
            return Report(0, stats, worstName, worstDecoded, advice);
        }
        var quality = stats.DecodedFraction * (0.5 + 0.5 * stats.MeanConfidence);
        var uncertainPenalty = Math.Clamp(stats.UncertainBitShare * 4, 0, 0.3);
        var saturationPenalty = Math.Clamp(stats.SaturationShare * 2, 0, 0.3);
        var regionFactor = 0.85 + 0.15 * worstQuality;
        var score = (int)Math.Round(100 * quality * (1 - uncertainPenalty) * (1 - saturationPenalty) * regionFactor);
        if (stats.SaturationShare > 0.01)
            advice.Add(string.Create(CultureInfo.InvariantCulture,
                $"{stats.SaturationShare * 100:0}% of the lit area is clipped white: lower the camera's exposure or gain."));
        if (stats.DecodedFraction < 0.85)
            advice.Add("Part of the lit area did not decode: darken the room, lock the camera's exposure and focus, or use the Robust mode.");
        if (stats.MeanConfidence < 0.5)
            advice.Add("Confidence is low: the picture is dim or noisy; raise the exposure a little or darken the room.");
        if (stats.UncertainBitShare > 0.03)
            advice.Add("Many code bits were too close to call: refocus the projector and the camera, or use the Robust or Difficult mode.");
        if (worstDecoded < 0.7 && stats.DecodedFraction >= 0.7)
            advice.Add(string.Create(CultureInfo.InvariantCulture,
                $"The {worstName} of the picture decodes poorly ({worstDecoded * 100:0}%): look for a shadow, glare or a dark surface there."));
        return Report(Math.Clamp(score, 0, 100), stats, worstName, worstDecoded, advice);
    }

    private static ScanQualityReport Report(int score, DecodeStats stats, string worstName, double worstDecoded, List<string> advice)
    {
        var grade = score >= 85 ? "Good" : score >= 65 ? "Usable" : score >= 40 ? "Poor" : "Failed";
        var clipped = stats.SaturationShare > 0.0005
            ? string.Create(CultureInfo.InvariantCulture, $"{stats.SaturationShare * 100:0.0}% clipped")
            : "nothing clipped";
        var words = string.Create(CultureInfo.InvariantCulture,
            $"{grade} scan (score {score}/100): {stats.DecodedFraction * 100:0}% of the lit area decoded at mean confidence {stats.MeanConfidence:0.00}, {stats.UncertainBitShare * 100:0.0}% of code bits uncertain, {clipped}. Weakest area: {worstName}, {worstDecoded * 100:0}% decoded.");
        if (advice.Count > 0)
            words += " " + string.Join(" ", advice);
        return new ScanQualityReport(score, grade, stats.LitFraction, stats.DecodedFraction, stats.MeanConfidence, stats.UncertainBitShare,
            stats.SaturationShare, worstName, worstDecoded, advice, words);
    }

    /// <summary>The 3 × 3 cell (with enough lit pixels to judge) whose decoded share × confidence is lowest.</summary>
    private static (string Name, double Decoded, double Quality) WorstRegion(CorrespondenceMap map)
    {
        Span<int> lit = stackalloc int[9];
        Span<int> valid = stackalloc int[9];
        Span<double> confidence = stackalloc double[9];
        for (var y = 0; y < map.Height; y++)
        {
            var row = Math.Min(2, y * 3 / map.Height);
            for (var x = 0; x < map.Width; x++)
            {
                var k = y * map.Width + x;
                var f = map.Flags[k];
                if ((f & DecodeStatus.Lit) == 0)
                    continue;
                var cell = row * 3 + Math.Min(2, x * 3 / map.Width);
                lit[cell]++;
                if ((f & DecodeStatus.Valid) != 0)
                {
                    valid[cell]++;
                    confidence[cell] += map.Confidence[k];
                }
            }
        }
        var minimum = Math.Max(50, map.Width * map.Height / 9 / 50);
        var worst = -1;
        var worstQuality = double.PositiveInfinity;
        var worstDecoded = 1.0;
        for (var c = 0; c < 9; c++)
        {
            if (lit[c] < minimum)
                continue;
            var decoded = (double)valid[c] / lit[c];
            var meanConfidence = valid[c] > 0 ? confidence[c] / valid[c] : 0;
            var quality = decoded * (0.5 + 0.5 * meanConfidence);
            if (quality < worstQuality)
            {
                worstQuality = quality;
                worst = c;
                worstDecoded = decoded;
            }
        }
        if (worst < 0)
            return ("whole picture", 0, 0);
        var name = worst == 4 ? "centre" : worst / 3 == 1 ? Columns3[worst % 3] : worst % 3 == 1 ? Rows3[worst / 3] : $"{Rows3[worst / 3]} {Columns3[worst % 3]}";
        return (name, worstDecoded, worstQuality);
    }
}
