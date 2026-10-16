using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration.Scoring;

/// <summary>What a scan's solve measured, for its score: its dense reprojection RMS over its points, the share of the projector's
/// picture it decoded, and how many camera views it had.</summary>
public sealed record ScanScoreInputs(string TargetId, double DenseRmsPx, int Points, double DecodedShare, int Views)
{
    /// <summary>The camera that scanned, for the fix lines ("" when none is known).</summary>
    public string CameraId { get; init; } = "";

    /// <summary>The scan, and the view of it that disagrees with the rest ("" when none does): the dense fit's fix drops that view
    /// (wp2-multiscan).</summary>
    public string ScanId { get; init; } = "";

    public string DropView { get; init; } = "";
}

/// <summary>What a camera alignment measured, for its score: the share of the canvas its outputs cover, the seam
/// misregistration between neighbours, the spread of a stack's members, brightness non-uniformity and the colour difference.
/// A negative value was not measured and makes no term.</summary>
public sealed record AlignScoreInputs(string GroupId, double CoverageShare, double SeamPx, double StackPx, double UniformityShare,
    double ColourDeltaE)
{
    public string StackId { get; init; } = "";

    public string ScanId { get; init; } = "";
}

/// <summary>Explainable calibration scores, every one in <see cref="ScoreTermCodes"/>' vocabulary. A pin solve's and a scan's are
/// px-equivalents (lower is better): the fit's RMS rounded up to 0.1 px is the base, and each fault adds its penalty; a hard
/// failure is 99.9 and F. An alignment's is points (100 less each fault's deduction). The words give the grade, the value and
/// the two terms that cost most; each term carries its own words, where, fix line and hint.</summary>
public static class CalibrationScoring
{
    /// <summary>A pin solve's score: the solved projector (its optics, pose, locks, pins and priors) and the solve's residuals and
    /// inliers in pin order. Answers the uncertainty too (null when the pins cannot carry one): the pins' information with the
    /// projector's priors added as the solve weighed them (<paramref name="priorPixelSigma"/>: the noise its rows were weighed against,
    /// 1 px for hand pins), so the score, STATE and the solve's reply carry one uncertainty.</summary>
    public static (QualityScore Score, SolveUncertainty? Uncertainty) ForPins(Projector solved, PoseSolution solution,
        string source = "pins", double priorPixelSigma = 1)
    {
        ArgumentNullException.ThrowIfNull(solved);
        ArgumentNullException.ThrowIfNull(solution);
        var k = solved.Intrinsics;
        var pins = solved.Pins;
        var holes = new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = solved.Id };
        var terms = new List<ScoreTerm>();
        var inlierResiduals = new List<double>();
        var inlierPixels = new List<Vec2d>();
        var inlierPairs = new List<Correspondence>();
        var behind = false;
        var outliers = new List<(int Number, string Id, double Error)>();
        for (var i = 0; i < pins.Length; i++)
        {
            if (!pins[i].Enabled)
                continue;
            var err = i < solution.ResidualsPx.Count ? solution.ResidualsPx[i] : double.PositiveInfinity;
            var inlier = i < solution.Inliers.Count && solution.Inliers[i];
            if (inlier)
            {
                inlierResiduals.Add(err);
                inlierPixels.Add(pins[i].Pixel);
                inlierPairs.Add(new Correspondence(pins[i].World, pins[i].Pixel));
                if (solved.Pose.WorldToCamera(pins[i].World).Z <= 0)
                    behind = true;
            }
            else
            {
                outliers.Add((i + 1, pins[i].Id, err));
            }
        }
        var n = inlierResiduals.Count;
        var rms = n > 0 ? Math.Sqrt(inlierResiduals.Sum(e => e * e) / n) : double.PositiveInfinity;
        var failed = behind || n < 4 || !double.IsFinite(rms) || k.Fx <= 0 || k.Fy <= 0;
        var p95 = n > 0 ? SolverMath.Quantile(inlierResiduals.ToArray(), 0.95) : double.PositiveInfinity;

        // The base: the fit.
        var rmsShown = double.IsFinite(rms) ? rms : 99.9;
        terms.Add(Term(ScoreTermCodes.Rms, ScoreVocabulary.Of(ScoreTermCodes.Rms).Words(n, rmsShown), rmsShown, Ceil01(rmsShown), "", holes,
            light: Light(QualityScore.GradeOf(Ceil01(rmsShown), "px", false))));

        if (failed)
        {
            var why = behind ? "solved pointing backwards: a pin is behind the projector"
                : n < 4 ? string.Create(CultureInfo.InvariantCulture, $"only {n} pins agree — a solve needs six or more spread out")
                : "the solve has no optics";
            terms.Add(Term(ScoreTermCodes.Geometry, ScoreVocabulary.Of(ScoreTermCodes.Geometry).Words(why), 1, 99.9, "", holes));
            return (Compose(terms, "px", failed: true, rms, p95, 0, source), null);
        }

        // The worst inlier: p95 and the pin it is.
        var worstIndex = -1;
        var worstErr = -1.0;
        for (var i = 0; i < pins.Length; i++)
            if (pins[i].Enabled && i < solution.Inliers.Count && solution.Inliers[i] && solution.ResidualsPx[i] > worstErr)
                (worstIndex, worstErr) = (i, solution.ResidualsPx[i]);
        var p95Penalty = 0.5 * Math.Max(0, p95 - 1.5);
        var p95Holes = With(holes, ("pin", worstIndex >= 0 ? pins[worstIndex].Id : ""), ("pinNo", (worstIndex + 1).ToString(CultureInfo.InvariantCulture)));
        terms.Add(Term(ScoreTermCodes.P95, ScoreVocabulary.Of(ScoreTermCodes.P95).Words(p95, worstIndex + 1, worstErr), p95, p95Penalty,
            worstIndex >= 0 ? "pins:" + pins[worstIndex].Id : "", p95Holes));

        // Outliers: each enabled pin the solve threw out.
        if (outliers.Count > 0)
        {
            var o = outliers.MaxBy(x => x.Error);
            var more = outliers.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" ({outliers.Count - 1} more)") : "";
            terms.Add(Term(ScoreTermCodes.Outliers, ScoreVocabulary.Of(ScoreTermCodes.Outliers).Words(o.Number, double.IsFinite(o.Error) ? o.Error : 999, more),
                outliers.Count, 0.5 * outliers.Count, "pins:" + o.Id,
                With(holes, ("pin", o.Id), ("pinNo", o.Number.ToString(CultureInfo.InvariantCulture)))));
        }

        // Spread: the inliers' convex hull over the raster, and the emptiest quarter (named when the hull is short of the mark).
        var hull = HullArea(inlierPixels) / Math.Max(1.0, (double)k.Width * k.Height);
        var (empty, inEmpty) = EmptyQuarter(inlierPixels, k.Width, k.Height);
        var spreadPenalty = 2 * Math.Max(0, 0.35 - hull) / 0.35;
        terms.Add(Term(ScoreTermCodes.Spread, ScoreVocabulary.Of(ScoreTermCodes.Spread).Words(hull * 100,
            spreadPenalty > 0 ? $"; the {QuarterWords(empty)} extrapolates" : ""), hull * 100, spreadPenalty, "quadrant:" + empty, holes));

        // Coverage: a 4 × 3 grid of the raster's cells holding an inlier pin.
        var cells = new bool[12];
        foreach (var px in inlierPixels)
        {
            var cx = Math.Clamp((int)(px.X / k.Width * 4), 0, 3);
            var cy = Math.Clamp((int)(px.Y / k.Height * 3), 0, 2);
            cells[cy * 4 + cx] = true;
        }
        var covered = cells.Count(c => c) / 12.0;
        terms.Add(Term(ScoreTermCodes.Coverage, ScoreVocabulary.Of(ScoreTermCodes.Coverage).Words(covered * 100,
            inEmpty == 0 ? $"; none in the {QuarterWords(empty)} quarter" : ""), covered * 100, 2 * Math.Max(0, 0.5 - covered), "quadrant:" + empty, holes));

        // Conditioning: the solve's own uncertainty on the wall, and its weakest direction.
        // A focal the solve held carries no row: the lens prior adds nothing to a locked focal.
        var priors = solved.Priors.ThrowRatio > 0 || solved.Priors.Position is not null
            ? PosePriors.From(solved.Priors, k, solved.Priors.Position, priorPixelSigma)
            : null;
        var analysed = SolveUncertaintyAnalysis.Analyse(k, solved.Lens, solved.Pose, inlierPairs, solved.Locks, priors);
        var confidence = 0.0;
        SolveUncertainty? uncertainty = null;
        if (analysed is { } a)
        {
            uncertainty = a.Uncertainty;
            confidence = a.SamplesPx.Count(s => s <= 1.0) / (double)a.SamplesPx.Length;
            var zoom = a.Uncertainty.Weak == "zoom↔distance" && (solved.Locks & SolveLocks.Focal) == 0;
            var weak = zoom ? " — points nearly flat: zoom cannot be told from distance" : a.Uncertainty.Weak.Length > 0 ? $" — weakest along {a.Uncertainty.Weak}" : "";
            terms.Add(Term(ScoreTermCodes.Conditioning, ScoreVocabulary.Of(ScoreTermCodes.Conditioning).Words(a.Uncertainty.WallPx, weak), a.Uncertainty.WallPx,
                Math.Max(0, a.Uncertainty.WallPx - 0.5) + (zoom ? 1 : 0), "edges", holes));
        }

        // Lens plausibility: square pixels, the principal point within the picture's reach, a throw a lens has, mild distortion.
        var (lensPenalty, lensWords, lensValue) = LensPlausibility(k, solved.Lens);
        terms.Add(Term(ScoreTermCodes.Lens, ScoreVocabulary.Of(ScoreTermCodes.Lens).Words(lensWords), lensValue, lensPenalty, "", holes));

        return (Compose(terms, "px", failed: false, rms, p95, confidence, source), uncertainty);
    }

    /// <summary>A scan's score (px-equivalents): the dense fit is the base; a poorly decoded picture and a single view add.</summary>
    public static QualityScore ForScan(ScanScoreInputs s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var holes = new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = s.TargetId, ["camera"] = s.CameraId };
        var failed = !double.IsFinite(s.DenseRmsPx) || s.Points <= 0;
        var rms = failed ? 99.9 : s.DenseRmsPx;
        var terms = new List<ScoreTerm>
        {
            Term(ScoreTermCodes.DenseRms, ScoreVocabulary.Of(ScoreTermCodes.DenseRms).Words(s.Points, rms), rms, Ceil01(rms), "", holes,
                light: Light(QualityScore.GradeOf(Ceil01(rms), "px", failed))),
            Term(ScoreTermCodes.Decoded, ScoreVocabulary.Of(ScoreTermCodes.Decoded).Words(s.DecodedShare * 100), s.DecodedShare * 100,
                Math.Max(0, 0.85 - s.DecodedShare) / 0.2, "", holes),
            Term(ScoreTermCodes.Views, ScoreVocabulary.Of(ScoreTermCodes.Views).Words(s.Views, s.Views == 1 ? "" : "s"), s.Views,
                0.5 * Math.Max(0, 2 - s.Views), "", holes),
        };
        if (s.Views >= 2)
            terms[2] = terms[2] with { Words = string.Create(CultureInfo.InvariantCulture, $"{s.Views} views") };
        if (s.DropView.Length > 0 && s.ScanId.Length > 0)
            terms[0] = terms[0] with { Where = s.DropView, Fix = $"SCAN DROP {s.ScanId} {s.DropView}", Hint = "that view disagrees with the others: the camera moved during it" };
        return Compose(terms, "px", failed, failed ? double.PositiveInfinity : s.DenseRmsPx, 0, s.DecodedShare, "scan");
    }

    /// <summary>An alignment's score (points, 100 less each deduction): coverage, seams, a stack's spread, uniformity, colour.</summary>
    public static QualityScore ForAlign(AlignScoreInputs a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var holes = new Dictionary<string, string>(StringComparer.Ordinal) { ["group"] = a.GroupId, ["stack"] = a.StackId, ["scan"] = a.ScanId };
        var terms = new List<ScoreTerm>();
        if (a.CoverageShare >= 0)
            terms.Add(PointsTerm(ScoreTermCodes.Coverage, string.Create(CultureInfo.InvariantCulture, $"the outputs cover {a.CoverageShare * 100:0} % of the canvas"),
                a.CoverageShare * 100, Math.Min(50, 100 * Math.Max(0, 0.98 - a.CoverageShare)), "PROJECTOR SOLVE {target} LOCK SHIFT", holes, "%"));
        if (a.SeamPx >= 0)
            terms.Add(PointsTerm(ScoreTermCodes.Seam, ScoreVocabulary.Of(ScoreTermCodes.Seam).Words(a.SeamPx), a.SeamPx,
                Math.Min(50, 20 * Math.Max(0, a.SeamPx - 0.5)), null, holes, "px"));
        if (a.StackPx >= 0)
            terms.Add(PointsTerm(ScoreTermCodes.StackConvergence, ScoreVocabulary.Of(ScoreTermCodes.StackConvergence).Words(a.StackPx), a.StackPx,
                Math.Min(50, 20 * Math.Max(0, a.StackPx - 0.5)), null, holes, "px"));
        if (a.UniformityShare >= 0)
            terms.Add(PointsTerm(ScoreTermCodes.Uniformity, ScoreVocabulary.Of(ScoreTermCodes.Uniformity).Words(a.UniformityShare * 100), a.UniformityShare * 100,
                Math.Min(30, 100 * Math.Max(0, a.UniformityShare - 0.1)), null, holes, "%"));
        if (a.ColourDeltaE >= 0)
            terms.Add(PointsTerm(ScoreTermCodes.ColourMatch, ScoreVocabulary.Of(ScoreTermCodes.ColourMatch).Words(a.ColourDeltaE), a.ColourDeltaE,
                Math.Min(30, 5 * Math.Max(0, a.ColourDeltaE - 2)), null, holes, "ΔE"));
        var value = Math.Clamp(100 - terms.Sum(t => t.Penalty), 0, 100);
        var grade = QualityScore.GradeOf(value, "points", false);
        var ranked = terms.Where(t => t.Penalty > 0).OrderByDescending(t => t.Penalty).Take(2).Select(t => t.Words).ToList();
        var words = string.Create(CultureInfo.InvariantCulture, $"{grade} {value:0}") + (ranked.Count > 0 ? " — " + string.Join("; ", ranked) : " — aligned");
        return new QualityScore(Math.Round(value, 1), "points", grade, Math.Max(0, a.SeamPx), 0, Math.Max(0, a.CoverageShare), [.. terms], words)
        {
            Source = "align",
        };
    }

    /// <summary>The term that cost most beyond the base, in words ("" when none did).</summary>
    public static string Worst(QualityScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        var baseCode = score.Terms.IsDefaultOrEmpty ? "" : score.Terms[0].Code;
        var worst = score.Terms.IsDefaultOrEmpty ? null : score.Terms.Where(t => t.Code != baseCode && t.Penalty > 0).MaxBy(t => t.Penalty);
        return worst?.Words ?? "";
    }

    private static QualityScore Compose(List<ScoreTerm> terms, string unit, bool failed, double rms, double p95, double confidence, string source)
    {
        var value = failed ? 99.9 : Math.Round(terms.Sum(t => t.Penalty), 2);
        var grade = QualityScore.GradeOf(value, unit, failed);
        var extras = terms.Skip(1).Where(t => t.Penalty > 0).OrderByDescending(t => t.Penalty).Take(2).Select(t => t.Words).ToList();
        var words = string.Create(CultureInfo.InvariantCulture, $"{grade} {value:0.0} — ")
            + (extras.Count > 0 ? string.Join("; ", extras) : terms[0].Words);
        return new QualityScore(value, unit, grade, double.IsFinite(rms) ? Math.Round(rms, 3) : 99.9, double.IsFinite(p95) ? Math.Round(p95, 3) : 99.9,
            Math.Round(confidence, 3), [.. terms], words)
        {
            Source = source,
        };
    }

    private static ScoreTerm Term(string code, string words, double value, double penalty, string where, IReadOnlyDictionary<string, string> holes,
        string? light = null)
    {
        var v = ScoreVocabulary.Of(code);
        penalty = Math.Round(penalty, 3);
        return new ScoreTerm(code, words, Math.Round(value, 3), v.Unit, penalty, light ?? (penalty <= 0.2 ? "green" : penalty <= 1 ? "amber" : "red"),
            where, ScoreVocabulary.Fill(v.Fix, holes), ScoreVocabulary.Fill(v.Hint, holes));
    }

    private static ScoreTerm PointsTerm(string code, string words, double value, double deduction, string? fixOverride,
        IReadOnlyDictionary<string, string> holes, string unit)
    {
        var v = ScoreVocabulary.Of(code);
        deduction = Math.Round(deduction, 2);
        return new ScoreTerm(code, words, Math.Round(value, 3), unit, deduction, deduction <= 5 ? "green" : deduction <= 15 ? "amber" : "red", "",
            ScoreVocabulary.Fill(fixOverride ?? v.Fix, holes), v.Hint);
    }

    private static Dictionary<string, string> With(IReadOnlyDictionary<string, string> holes, params (string Key, string Value)[] more)
    {
        var d = new Dictionary<string, string>(holes, StringComparer.Ordinal);
        foreach (var (key, value) in more)
            d[key] = value;
        return d;
    }

    private static string Light(string grade) => grade switch
    {
        "A" or "B" => "green",
        "C" => "amber",
        _ => "red",
    };

    private static double Ceil01(double v) => Math.Ceiling(v * 10 - 1e-9) / 10;

    /// <summary>The lens's plausibility: a penalty, its words and the largest relative excess.</summary>
    private static (double Penalty, string Words, double Value) LensPlausibility(Intrinsics k, LensDistortion lens)
    {
        var penalty = 0.0;
        var words = new List<string>();
        var aspect = Math.Abs(k.Fx / k.Fy - 1);
        if (aspect > 0.005)
        {
            penalty += 100 * (aspect - 0.005);
            words.Add(string.Create(CultureInfo.InvariantCulture, $"pixels not square ({aspect * 100:0.#} %): a scaling mode in the projector or a wrong raster"));
        }
        var shiftX = Math.Abs(k.Cx - k.Width / 2.0) / k.Width;
        var shiftY = Math.Abs(k.Cy - k.Height / 2.0) / k.Height;
        var shift = Math.Max(shiftX, shiftY);
        if (shift > 0.6)
        {
            penalty += 1 + 2 * (shift - 0.6);
            words.Add(string.Create(CultureInfo.InvariantCulture, $"lens shift {shift * 100:0} % — beyond what a lens does"));
        }
        var throwRatio = k.Fx / k.Width;
        if (throwRatio is < 0.25 or > 12)
        {
            penalty += 1 + 10 * (throwRatio < 0.25 ? (0.25 - throwRatio) / 0.25 : (throwRatio - 12) / 12);
            words.Add(string.Create(CultureInfo.InvariantCulture, $"throw {throwRatio:0.00} — no projector lens throws that"));
        }
        if (Math.Abs(lens.K1) > 0.3)
        {
            penalty += 1;
            words.Add("lens distortion unrealistic for a projector");
        }
        var w = words.Count > 0 ? string.Join("; ", words)
            : LensWords(throwRatio, (k.Cy - k.Height / 2.0) / k.Height);
        return (penalty, w, Math.Round(Math.Max(aspect, shift), 4));
    }

    /// <summary>"throw 1.50, shift 10 % up": the datasheet's sign (the picture up of the axis when cy is below the centre row).</summary>
    private static string LensWords(double throwRatio, double shiftUp) =>
        Math.Abs(shiftUp) < 0.005
            ? string.Create(CultureInfo.InvariantCulture, $"throw {throwRatio:0.00}, no lens shift")
            : string.Create(CultureInfo.InvariantCulture, $"throw {throwRatio:0.00}, shift {Math.Abs(shiftUp) * 100:0} % {(shiftUp > 0 ? "up" : "down")}");

    private static string QuarterWords(string q) => q switch
    {
        "tl" => "top-left",
        "tr" => "top-right",
        "bl" => "bottom-left",
        "br" => "bottom-right",
        _ => "edge",
    };

    /// <summary>The raster quarter with the fewest pins (tl, tr, bl, br); of equals, the one farthest from the pins' centre.</summary>
    private static (string Quarter, int Pins) EmptyQuarter(List<Vec2d> pixels, int width, int height)
    {
        Span<int> count = stackalloc int[4];
        var centre = Vec2d.Zero;
        foreach (var p in pixels)
        {
            count[(p.X < width / 2.0 ? 0 : 1) + (p.Y < height / 2.0 ? 0 : 2)]++;
            centre += p;
        }
        if (pixels.Count > 0)
            centre *= 1.0 / pixels.Count;
        double Far(int q) => (new Vec2d((q % 2 == 0 ? 0.25 : 0.75) * width, (q < 2 ? 0.25 : 0.75) * height) - centre).LengthSquared;
        var best = 0;
        for (var i = 1; i < 4; i++)
            if (count[i] < count[best] || (count[i] == count[best] && Far(i) > Far(best)))
                best = i;
        return (best switch { 0 => "tl", 1 => "tr", 2 => "bl", _ => "br" }, count[best]);
    }

    /// <summary>The area of the points' convex hull (monotone chain).</summary>
    internal static double HullArea(List<Vec2d> points)
    {
        if (points.Count < 3)
            return 0;
        var p = points.OrderBy(x => x.X).ThenBy(x => x.Y).ToArray();
        var hull = new Vec2d[2 * p.Length];
        var k = 0;
        for (var i = 0; i < p.Length; i++)
        {
            while (k >= 2 && Vec2d.Cross(hull[k - 1] - hull[k - 2], p[i] - hull[k - 2]) <= 0)
                k--;
            hull[k++] = p[i];
        }
        for (int i = p.Length - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Vec2d.Cross(hull[k - 1] - hull[k - 2], p[i] - hull[k - 2]) <= 0)
                k--;
            hull[k++] = p[i];
        }
        var area = 0.0;
        for (var i = 0; i < k - 1; i++)
            area += Vec2d.Cross(hull[i], hull[i + 1]);
        return Math.Abs(area) / 2;
    }

    /// <summary>Every term code's fix line filled with sample ids (for checks that every fix parses).</summary>
    public static ImmutableArray<string> SampleFixLines()
    {
        var holes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target"] = "pj_sample1", ["pin"] = "pn_sample1", ["pinNo"] = "1", ["camera"] = "cm_sample1", ["group"] = "al_sample1",
            ["stack"] = "sk_sample1", ["scan"] = "sc_sample1",
        };
        return [.. ScoreVocabulary.Entries.Values.Select(e => ScoreVocabulary.Fill(e.Fix, holes))];
    }
}
