using System.Collections.Immutable;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Site;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Facade;

/// <summary>The facade guide: each step's state follows the project as the workflow is done — survey, control points, pins, the lens
/// prior and the solve, a stack scanned, aligned and matched, the blend, the score — the next step is always the first not done, and
/// every step's line is a line of the catalogue.</summary>
public sealed class SiteGuideTests
{
    private static Projector Pinned(string id, int pins) =>
        new(id, id.ToUpperInvariant(), Intrinsics.FromThrowRatio(1920, 1080, 3), LensDistortion.None, Pose.Identity, 30_000, "#FFFFFF", false,
            [.. Enumerable.Range(0, pins).Select(i => new CalibrationPin($"pn{id}{i}", new Vec3d(i, 0, 0), new Vec2d(i * 10, 0), true))], SolveLocks.None, 0);

    private static Output OutputOf(string id, string projectorId) =>
        new(id, id, OutputKind.Virtual, "", 1920, 1080, 60, OutputMode.Projector, projectorId, SourceRef.None, new RectD(0, 0, 1920, 1080),
            OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(0, 0, 1920, 1080), 0, true);

    private static void AssertParses(GuideStep step)
    {
        Assert.True(WireGrammar.TryParse(step.Line, out var line, out var error), $"{step.Step}: {step.Line} — {error?.Reason}");
        Assert.True(VerbCatalog.Match(line!) is not null, $"{step.Step}: {step.Line} is no verb");
    }

    private static GuideStep Next(Project p, GuideFacts? facts = null)
    {
        var steps = SiteGuide.Steps(p, facts ?? GuideFacts.None);
        Assert.Equal(["survey", "controls", "pins", "solve", "stacks", "blend", "score"], steps.Select(s => s.Step));
        foreach (var s in steps)
        {
            AssertParses(s);
            Assert.Contains(s.State, new[] { SiteGuide.DoneState, SiteGuide.WaitingState, SiteGuide.RunningState, SiteGuide.FailedState });
        }
        var next = SiteGuide.Next(steps);
        Assert.NotNull(next);
        Assert.Equal(steps.First(s => !s.Done), next);
        return next;
    }

    [Fact]
    public void EachStepsStateFollowsTheProjectAndEveryLineParses()
    {
        var p = Project.CreateDefault("Facade");
        var step = Next(p);
        Assert.Equal(("survey", SiteGuide.WaitingState, "SURVEY IMPORT <path>"), (step.Step, step.State, step.Line));

        var survey = new SurveySource("sv00000001", "North front", SurveyFormat.E57, "/x.e57", "", "EPSG:25832", 10, 0, null, "clouds/sv00000001.ply", 0.01, 0,
            SurveyState.Importing, 0, "");
        p = p.WithStage(st => st with { Surveys = [survey] });
        Assert.Equal(("survey", SiteGuide.RunningState), (Next(p).Step, Next(p).State));
        p = p.WithStage(st => st with { Surveys = [survey with { State = SurveyState.Failed, Notes = "the file ended early" }] });
        Assert.Contains("the file ended early", Next(p).Words, StringComparison.Ordinal);
        Assert.Equal(SiteGuide.FailedState, Next(p).State);
        p = p.WithStage(st => st with { Surveys = [survey with { State = SurveyState.Ready }] });

        step = Next(p);
        Assert.Equal(("controls", "SITE POINTS <path>"), (step.Step, step.Line));
        p = p.WithStage(st => st with
        {
            Site = new SiteFrame("EPSG:25832", "ETRS89 / UTM zone 32N", new Vec3d(512_340, 5_401_230, 120), 0, 1, 50),
            Controls = [.. Enumerable.Range(1, 3).Select(i => new ControlPoint($"cp0000000{i}", $"CP{i}", new Vec3d(512_340 + i, 5_401_230, 120), 0.01, "", true))],
        });

        step = Next(p);
        Assert.Equal(("pins", "ADD projector <json>"), (step.Step, step.Line));
        p = p.WithStage(st => st with { Projectors = [Pinned("pj00000001", 2)] });
        step = Next(p);
        Assert.Equal("pins", step.Step);
        Assert.StartsWith("ADD pin pj00000001 {\"controlId\":\"cp00000001\"", step.Line, StringComparison.Ordinal);
        Assert.Contains("CP1", step.Words, StringComparison.Ordinal);

        p = p.WithStage(st => st with { Projectors = [Pinned("pj00000001", 4)] });
        step = Next(p);
        Assert.Equal(("solve", "PROJECTOR PRIOR pj00000001 THROW <ratio>"), (step.Step, step.Line));
        p = p.WithStage(st => st with { Projectors = st.Projectors.Update("pj00000001", pj => pj with { Priors = SolvePriors.None with { ThrowRatio = 3.02 } }) });
        Assert.Equal("PROJECTOR SOLVE pj00000001", Next(p).Line);
        p = p.WithStage(st => st with
        {
            Projectors = st.Projectors.Update("pj00000001", pj => pj with { Uncertainty = new SolveUncertainty(0.8, 1, 0, 0.02, 0.01, 10, "") }),
        });

        // No stack: done. A stack: scanned, then aligned, then matched.
        step = Next(p);
        Assert.Equal(("score", SiteGuide.WaitingState), (step.Step, step.State));
        var twin = Pinned("pj00000002", 4) with { Uncertainty = new SolveUncertainty(0.8, 1, 0, 0.02, 0.01, 10, "") };
        p = p with { Outputs = [OutputOf("op00000001", "pj00000001"), OutputOf("op00000002", "pj00000002")] };
        p = p.WithStage(st => st with
        {
            Projectors = st.Projectors.Add(twin),
            Stacks = [new StackGroup("sk00000001", "S1", ["pj00000001", "pj00000002"], "pj00000001", StackFailover.Hold)],
        });
        step = Next(p);
        Assert.Equal(("stacks", "SCAN START <camera> pj00000001 pj00000002"), (step.Step, step.Line));
        p = p.WithStage(st => st with { Scans = [new ScanRecord("sc00000001", "Night", ScanKind.StructuredLight, 0, null, ["pj00000001", "pj00000002"], "", null, 0, 0, "")] });
        Assert.Equal("STACK ALIGN sk00000001 FROM sc00000001", Next(p).Line);
        var lattice = WarpLattice.AtRest(3, 3);
        p = p with
        {
            Outputs = p.Outputs.Update("op00000002", o => o with { Measured = new MeasuredCorrection(null, lattice, null, null, 0.1, "sc00000001", "sk00000001", "", 1) }),
        };
        Assert.Equal("STACK MATCH sk00000001 FROM sc00000001", Next(p).Line);
        var colour = new MeasuredColor(1, 0.98, 0.95, 2.2, 1);
        p = p with
        {
            Outputs = [.. p.Outputs.Select(o => o with { Measured = (o.Measured ?? new MeasuredCorrection(null, null, null, null, 0, "sc00000001", "sk00000001", "", 1)) with { Color = colour } })],
        };

        step = Next(p);
        Assert.Equal(("blend", "BLEND SOLVE"), (step.Step, step.Line));
        var solved = new GuideFacts(true, _ => null);
        step = Next(p, solved);
        Assert.Equal("score", step.Step);

        var poor = QualityScore.None with
        {
            Grade = "C",
            Words = "two pins disagree",
            Terms = [new ScoreTerm(ScoreTermCodes.Outliers, "pin 3 is 6 px off", 6, "px", 3, "red", "pj00000002", "CALIBRATION STATUS pj00000002", "")],
        };
        step = Next(p, solved with { ScoreOf = id => id == "pj00000002" ? poor : QualityScore.None with { Grade = "A" } });
        Assert.Equal(("score", "CALIBRATION STATUS pj00000002"), (step.Step, step.Line));
        Assert.Contains("scores C", step.Words, StringComparison.Ordinal);

        var through = SiteGuide.Steps(p, solved with { ScoreOf = _ => QualityScore.None with { Grade = "A" } });
        Assert.All(through, s => Assert.True(s.Done, s.Step));
        Assert.Null(SiteGuide.Next(through));
        Assert.Equal(Light.Green, SiteGuide.LightOf(p, SiteGuide.Next(through)));
    }

    /// <summary>The guide asks for attention only once the facade work has begun: a project holding none of the facade's own inputs —
    /// a playback node's, or a rig of pinned projectors, an imported model, a scan and a stack, with or without solve priors (any rig's
    /// weak solve asks for the lens's throw) — is grey with its next step named (the survey or the control points: nothing after them can
    /// be next without three control points); each input alone (a site, a survey importing, imported or failed, a control point even
    /// disabled) makes it amber while a step is left.</summary>
    [Fact]
    public void TheGuidesLightWaitsForTheFacadeWorkToBegin()
    {
        static (string Step, Light Light) Lit(Project p)
        {
            var next = SiteGuide.Next(SiteGuide.Steps(p, GuideFacts.None));
            Assert.NotNull(next);
            return (next.Step, SiteGuide.LightOf(p, next));
        }

        var plain = Project.CreateDefault("Playback");
        Assert.False(SiteGuide.Begun(plain));
        Assert.Equal(("survey", Light.Grey), Lit(plain));

        // A rig that maps without a facade: projectors pinned and solved, an imported model on a surface, a scan and a stack.
        var mesh = new MeshAsset("ms00000001", "Car", MeshOrigin.Imported, "meshes/car.obj", "", "", ParamSet.Empty, 8, 12);
        var rig = plain with { Outputs = [OutputOf("op00000001", "pj00000001"), OutputOf("op00000002", "pj00000002")] };
        rig = rig.WithStage(st => st with
        {
            Meshes = [mesh],
            Surfaces = [new Surface("sf00000001", "Car", mesh.Id, Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#FFFFFF", [])],
            Projectors = [Pinned("pj00000001", 6) with { Uncertainty = new SolveUncertainty(0.8, 1, 0, 0.02, 0.01, 10, "") }, Pinned("pj00000002", 6)],
            Scans = [new ScanRecord("sc00000001", "Night", ScanKind.StructuredLight, 0, null, ["pj00000001", "pj00000002"], "", null, 0, 0, "")],
            Stacks = [new StackGroup("sk00000001", "S1", ["pj00000001", "pj00000002"], "pj00000001", StackFailover.Hold)],
        });
        Assert.False(SiteGuide.Begun(rig));
        Assert.Equal(("controls", Light.Grey), Lit(rig));

        // Solve priors serve every rig: the throw a weak solve asks for, a position measured on site.
        var priors = new (string Why, Project P)[]
        {
            ("a throw prior", rig.WithStage(st => st with { Projectors = st.Projectors.Update("pj00000002", pj => pj with { Priors = SolvePriors.None with { ThrowRatio = 3.02 } }) })),
            ("a position prior", rig.WithStage(st => st with { Projectors = st.Projectors.Update("pj00000002", pj => pj with { Priors = SolvePriors.None with { Position = new Vec3d(0, 10, 100) } }) })),
            ("a plain node's throw prior", plain.WithStage(st => st with { Projectors = [Pinned("pj00000001", 0) with { Priors = SolvePriors.None with { ThrowRatio = 1.5 } }] })),
        };
        foreach (var (why, p) in priors)
        {
            Assert.False(SiteGuide.Begun(p), why);
            Assert.Equal(Light.Grey, Lit(p).Light);
        }

        var survey = new SurveySource("sv00000001", "North front", SurveyFormat.E57, "/x.e57", "", "", 0, 0, null, "", 0, 0, SurveyState.Importing, 0, "");
        var control = new ControlPoint("cp00000001", "CP1", new Vec3d(512_341, 5_401_230, 120), 0.01, "", false);
        var begun = new (string Why, Project P)[]
        {
            ("a site", plain.WithStage(st => st with { Site = new SiteFrame("LOCAL", "", Vec3d.Zero, 0, 1, 0) })),
            ("a survey importing", plain.WithStage(st => st with { Surveys = [survey] })),
            ("a survey imported", plain.WithStage(st => st with { Surveys = [survey with { State = SurveyState.Ready }] })),
            ("a survey failed", plain.WithStage(st => st with { Surveys = [survey with { State = SurveyState.Failed, Notes = "the file ended early" }] })),
            ("a control point, disabled", rig.WithStage(st => st with { Controls = [control] })),
        };
        foreach (var (why, p) in begun)
        {
            Assert.True(SiteGuide.Begun(p), why);
            Assert.Equal(Light.Amber, Lit(p).Light);
        }
    }
}
