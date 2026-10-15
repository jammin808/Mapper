using System.Collections.Immutable;
using PlayMap.Core.Calibration;
using PlayMap.Core.Calibration.Scoring;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Calibration;

/// <summary>The explainable score: a clean pin solve grades A with the fit as its words; each injected fault — a pin dragged off, pins
/// bunched in a corner, a lens beyond what a lens does, a solve pointing backwards — raises its own term with its words, its fix
/// line and its light; every fix line of the vocabulary is a line of the catalogue; scan- and align-sourced scores grade and word
/// in the same vocabulary.</summary>
public class ScoreTests
{
    private static readonly Intrinsics Optics = Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0, 0.1);
    private static readonly Pose Truth = Pose.LookAt(new Vec3d(0, 1.6, 0), new Vec3d(0, 1.2, 5), Vec3d.UnitY);

    /// <summary>A projector with pins from its true pose, solved; pixels in [lo, hi] of the raster.</summary>
    private static (Projector Solved, PoseSolution Solution) Solve(int count = 16, double noise = 0.2, double lo = 0.05, double hi = 0.95,
        Func<int, CalibrationPin, CalibrationPin>? fault = null, SolveLocks locks = SolveLocks.Shift | SolveLocks.Distortion)
    {
        var rng = new Pcg32(42);
        var pins = ImmutableArray.CreateBuilder<CalibrationPin>();
        for (var i = 0; i < count; i++)
        {
            var px = new Vec2d(rng.Range(lo, hi) * Optics.Width, rng.Range(lo, hi) * Optics.Height);
            var world = Synthetic.PointAtDepth(px, Optics, LensDistortion.None, Truth, rng.Range(3.0, 7.0));
            var pin = new CalibrationPin($"pn_{i:x4}", world, px + Synthetic.Noise(ref rng, noise), true);
            pins.Add(fault?.Invoke(i, pin) ?? pin);
        }
        var start = new Pose(Truth.Position + new Vec3d(0.05, 0.02, -0.03), Truth.Rotation + new Vec3d(0.01, 0, 0.01));
        var pj = new Projector("pj_score", "Projector 1", Optics, LensDistortion.None, start, 0, "#FF8800", false, pins.ToImmutable(), locks, 0);
        var solution = PoseSolver.Solve(pj, PoseSolveOptions.Default with { Locks = locks, InitialPose = start });
        return (pj with { Intrinsics = solution.Intrinsics, Lens = solution.Lens, Pose = solution.Pose }, solution);
    }

    private static ScoreTerm TermOf(QualityScore s, string code) => s.Terms.Single(t => t.Code == code);

    [Fact]
    public void ACleanSolveGradesAWithItsFitAsItsWords()
    {
        var (pj, solution) = Solve();
        var (score, uncertainty) = CalibrationScoring.ForPins(pj, solution);
        Assert.Equal("A", score.Grade);
        Assert.Equal("px", score.Unit);
        Assert.Equal("pins", score.Source);
        Assert.InRange(score.Value, 0.1, 1.0);
        Assert.Equal(ScoreTermCodes.Rms, score.Terms[0].Code);
        Assert.StartsWith("A ", score.Words, StringComparison.Ordinal);
        Assert.Contains("fits its 16 pins to", score.Words, StringComparison.Ordinal);
        Assert.All(score.Terms, t => Assert.True(ScoreTermCodes.All.Contains(t.Code), t.Code));
        Assert.NotNull(uncertainty);
        Assert.InRange(uncertainty!.WallPx, 0, 1);
        Assert.InRange(score.Confidence, 0.9, 1);
        Assert.Equal("", CalibrationScoring.Worst(score));
    }

    [Fact]
    public void APinDraggedOffRaisesTheOutliersTermWithThatPin()
    {
        var (pj, solution) = Solve(fault: (i, pin) => i == 3 ? pin with { Pixel = pin.Pixel + new Vec2d(14, 0) } : pin);
        var (score, _) = CalibrationScoring.ForPins(pj, solution);
        var t = TermOf(score, ScoreTermCodes.Outliers);
        Assert.True(t.Penalty >= 0.5);
        Assert.Equal("pin 4 disagrees by 14 px", t.Words);
        Assert.Equal("pins:pn_0003", t.Where);
        Assert.Equal("SET pn_0003 enabled false", t.Fix);
        Assert.Equal("or drag pin 4 onto its feature", t.Hint);
        Assert.Contains("pin 4 disagrees by 14 px", score.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void PinsDraggedALittleOffRaiseTheP95TermWithTheWorstPin()
    {
        // Within the solver's 6 px outlier threshold: they stay in the fit and push its tail out.
        var (pj, solution) = Solve(fault: (i, pin) => i switch
        {
            3 => pin with { Pixel = pin.Pixel + new Vec2d(4.5, 0) },
            9 => pin with { Pixel = pin.Pixel + new Vec2d(0, -3.5) },
            _ => pin,
        });
        Assert.All(solution.Inliers, Assert.True);
        var (clean, cleanSolution) = Solve();
        var (good, _) = CalibrationScoring.ForPins(clean, cleanSolution);
        var (score, _) = CalibrationScoring.ForPins(pj, solution);
        var t = TermOf(score, ScoreTermCodes.P95);
        Assert.Equal(0, TermOf(good, ScoreTermCodes.P95).Penalty);
        Assert.True(t.Penalty > 0, $"p95 {t.Penalty}");
        Assert.True(t.Value > 1.5, $"p95 {t.Value}");
        Assert.StartsWith("95 % of its pins within", t.Words, StringComparison.Ordinal);
        Assert.Contains("pin 4 is off by", t.Words, StringComparison.Ordinal);
        Assert.Equal("pins:pn_0003", t.Where);
        Assert.Equal("SET pn_0003 enabled false", t.Fix);
        Assert.Equal("or drag pin 4 onto its feature", t.Hint);
        Assert.True(score.Value > good.Value, $"{score.Words} vs {good.Words}");
    }

    [Fact]
    public void PinsBunchedInACornerRaiseSpreadCoverageAndConditioning()
    {
        var (clean, cleanSolution) = Solve();
        var (bunched, solution) = Solve(lo: 0.05, hi: 0.35);
        var (good, _) = CalibrationScoring.ForPins(clean, cleanSolution);
        var (score, _) = CalibrationScoring.ForPins(bunched, solution);
        var spread = TermOf(score, ScoreTermCodes.Spread);
        Assert.True(spread.Penalty > 1, $"spread {spread.Penalty}");
        Assert.Contains("the pins cover", spread.Words, StringComparison.Ordinal);
        Assert.Contains("bottom-right extrapolates", spread.Words, StringComparison.Ordinal);
        Assert.Equal("quadrant:br", spread.Where);
        Assert.Equal("PROJECTOR SOLVE pj_score LOCK FOCAL", spread.Fix);
        Assert.True(TermOf(score, ScoreTermCodes.Coverage).Penalty > TermOf(good, ScoreTermCodes.Coverage).Penalty,
            $"coverage {TermOf(score, ScoreTermCodes.Coverage).Words} vs {TermOf(good, ScoreTermCodes.Coverage).Words}");
        var conditioning = TermOf(score, ScoreTermCodes.Conditioning);
        Assert.True(conditioning.Value > TermOf(good, ScoreTermCodes.Conditioning).Value * 2, $"{conditioning.Value} vs {TermOf(good, ScoreTermCodes.Conditioning).Value}");
        Assert.StartsWith("could be", conditioning.Words, StringComparison.Ordinal);
        Assert.True(score.Value > good.Value + 1, $"{score.Words} vs {good.Words}");
        Assert.True(score.Confidence <= good.Confidence, $"confidence {score.Confidence} vs {good.Confidence}");
    }

    [Fact]
    public void ALensBeyondALensRaisesTheLensTerm()
    {
        var (pj, solution) = Solve();
        var stretched = pj with { Intrinsics = pj.Intrinsics with { Fy = pj.Intrinsics.Fx * 1.03, Cx = pj.Intrinsics.Width * 1.25 } };
        var (score, _) = CalibrationScoring.ForPins(stretched, solution with { Intrinsics = stretched.Intrinsics });
        var lens = TermOf(score, ScoreTermCodes.Lens);
        Assert.True(lens.Penalty > 2, $"lens {lens.Penalty}");
        Assert.Contains("pixels not square (2.9 %)", lens.Words, StringComparison.Ordinal);
        Assert.Contains("lens shift 75 % — beyond what a lens does", lens.Words, StringComparison.Ordinal);
        Assert.Equal("PROJECTOR SOLVE pj_score LOCK SHIFT", lens.Fix);
        Assert.Equal("red", lens.Light);
    }

    [Fact]
    public void ASolvePointingBackwardsIsAHardFailure()
    {
        var (pj, solution) = Solve();
        var turned = pj with { Pose = Pose.LookAt(Truth.Position, Truth.Position - (Truth.Forward * 5), Vec3d.UnitY) };
        var (score, _) = CalibrationScoring.ForPins(turned, solution with { Pose = turned.Pose });
        Assert.Equal("F", score.Grade);
        Assert.Equal(99.9, score.Value);
        var g = TermOf(score, ScoreTermCodes.Geometry);
        Assert.Contains("pointing backwards", g.Words, StringComparison.Ordinal);
        Assert.Equal("CALIBRATION FALLBACK pj_score", g.Fix);
        Assert.Equal("red", g.Light);
    }

    [Fact]
    public void EveryCodeHasWordsAndEveryFixLineIsACatalogueLine()
    {
        Assert.Equal(ScoreTermCodes.All.Count, ScoreVocabulary.Entries.Count);
        Assert.All(ScoreTermCodes.All, code => Assert.True(ScoreVocabulary.Entries.ContainsKey(code), code));
        var lines = CalibrationScoring.SampleFixLines();
        Assert.Equal(ScoreTermCodes.All.Count, lines.Length);
        foreach (var line in lines)
        {
            Assert.False(string.IsNullOrEmpty(line));
            Assert.DoesNotContain('{', line);
            Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), $"{line}: {error?.Reason}");
            Assert.True(VerbCatalog.Match(parsed) is not null, $"{line} is not a line of the catalogue");
        }
        // A fix whose hole has no value is no line at all, never a line with a hole in it.
        Assert.Equal("", ScoreVocabulary.Fill("SCAN START {camera} {target}", new Dictionary<string, string> { ["target"] = "pj_x" }));
    }

    [Fact]
    public void AScanSourcedScoreGradesAndWordsInTheSameVocabulary()
    {
        var good = CalibrationScoring.ForScan(new ScanScoreInputs("pj_s", 0.42, 48_000, 0.96, 2) { CameraId = "cm_s" });
        Assert.Equal("A", good.Grade);
        Assert.Equal("scan", good.Source);
        Assert.Equal(0.5, good.Value, 6);
        Assert.Equal(ScoreTermCodes.DenseRms, good.Terms[0].Code);
        Assert.Contains("the scan fits 48,000 points to 0.42 px", good.Words, StringComparison.Ordinal);

        var poor = CalibrationScoring.ForScan(new ScanScoreInputs("pj_s", 0.8, 9_000, 0.55, 1) { CameraId = "cm_s" });
        var decoded = TermOf(poor, ScoreTermCodes.Decoded);
        Assert.Equal(1.5, decoded.Penalty, 6);
        Assert.Equal("55 % of the projector's picture decoded", decoded.Words);
        Assert.Equal("SCAN START cm_s pj_s MODE DIFFICULT", decoded.Fix);
        var views = TermOf(poor, ScoreTermCodes.Views);
        Assert.Equal(0.5, views.Penalty, 6);
        Assert.Equal("1 view: depth from one camera position", views.Words);
        Assert.Equal(2.8, poor.Value, 6);
        Assert.Equal("C", poor.Grade);
        Assert.Equal("C 2.8 — 55 % of the projector's picture decoded; 1 view: depth from one camera position", poor.Words);
    }

    [Fact]
    public void AnAlignSourcedScoreGradesAndWordsInPoints()
    {
        var good = CalibrationScoring.ForAlign(new AlignScoreInputs("al_1", 0.99, 0.3, -1, 0.05, 1.5));
        Assert.Equal("points", good.Unit);
        Assert.Equal("align", good.Source);
        Assert.Equal(100, good.Value);
        Assert.Equal("A", good.Grade);
        Assert.Equal("A 100 — aligned", good.Words);
        Assert.DoesNotContain(good.Terms, t => t.Code == ScoreTermCodes.StackConvergence);

        var poor = CalibrationScoring.ForAlign(new AlignScoreInputs("al_1", 0.90, 1.5, 1.0, 0.2, 4) { StackId = "sk_1", ScanId = "sc_1" });
        var seam = TermOf(poor, ScoreTermCodes.Seam);
        Assert.Equal(20, seam.Penalty, 6);
        Assert.Equal("neighbours disagree by 1.5 px in their overlap", seam.Words);
        Assert.Equal("ALIGN RUN al_1", seam.Fix);
        Assert.Equal("STACK ALIGN sk_1 FROM sc_1", TermOf(poor, ScoreTermCodes.StackConvergence).Fix);
        // 100 − coverage 8 − seam 20 − stack 10 − uniformity 10 − colour 10 = 42: F on the points scale.
        Assert.Equal(42, poor.Value, 6);
        Assert.Equal("F", poor.Grade);
        Assert.StartsWith("F 42 — neighbours disagree by 1.5 px", poor.Words, StringComparison.Ordinal);
    }
}
