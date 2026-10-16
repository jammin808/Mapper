using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Site;

/// <summary>One step of the facade workflow: its key, its state ("done", "waiting", "running", "failed"), the words, and the line that
/// does it (with &lt;…&gt; where only the operator knows the value: a file, a pixel).</summary>
public sealed record GuideStep(string Step, string State, string Words, string Line)
{
    public bool Done => State == SiteGuide.DoneState;
}

/// <summary>What the guide reads beyond the document: whether the 3D blend in place was solved for the stage as it is, and each
/// projector's current calibration score (null when it has none).</summary>
public sealed record GuideFacts(bool BlendCurrent, Func<string, QualityScore?> ScoreOf)
{
    public static readonly GuideFacts None = new(false, _ => null);
}

/// <summary>
/// The facade workflow as a step list (facade §3.5's recommended path): survey → control points → pins → priors and solve → stacks →
/// blend → score. Each step's state is a pure function of the project (and the two facts the engine knows), so it follows every
/// edit; each says the line that does it, so a desk, Companion or an assistant can drive the workflow with the verbs everyone uses.
/// The next step is the first not done.
/// </summary>
public static class SiteGuide
{
    public const string DoneState = "done";
    public const string WaitingState = "waiting";
    public const string RunningState = "running";
    public const string FailedState = "failed";

    /// <summary>The fewest control points that tell a model's shift, turn and scale.</summary>
    public const int MinControls = 3;

    /// <summary>The fewest pins a solve takes (with the zoom from the lens).</summary>
    public const int MinPins = 4;

    public static IReadOnlyList<GuideStep> Steps(Project p, GuideFacts facts)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(facts);
        return [Survey(p), Controls(p), Pins(p), Solve(p), Stacks(p), Blend(p, facts), Score(p, facts)];
    }

    /// <summary>The first step not done, or null when the facade is through.</summary>
    public static GuideStep? Next(IReadOnlyList<GuideStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.FirstOrDefault(s => !s.Done);
    }

    private static string Count(int n, string one) => n == 1 ? $"1 {one}" : string.Create(CultureInfo.InvariantCulture, $"{n} {one}s");

    private static GuideStep Survey(Project p)
    {
        const string line = "SURVEY IMPORT <path>";
        var surveys = p.Stage.Surveys;
        if (surveys.Any(s => s.State == SurveyState.Ready))
            return new("survey", DoneState, $"{Count(surveys.Count(s => s.State == SurveyState.Ready), "survey")} imported", line);
        if (surveys.FirstOrDefault(s => s.State == SurveyState.Importing) is { } running)
            return new("survey", RunningState, $"{running.Name} is importing", line);
        var model = p.Stage.Surfaces.Any(s => s.Visible && p.Stage.Meshes.Find(s.MeshId) is { Origin: MeshOrigin.Imported });
        if (model)
            return new("survey", DoneState, "the stage has an imported model", line);
        if (surveys.LastOrDefault(s => s.State == SurveyState.Failed) is { } failed)
            return new("survey", FailedState, $"{failed.Name} failed: {failed.Notes} — import it again", line);
        return new("survey", WaitingState, "Import the surveyed model or cloud (E57, LAS, OBJ, PLY, glTF)", line);
    }

    private static GuideStep Controls(Project p)
    {
        const string line = "SITE POINTS <path>";
        var n = p.Stage.Controls.Count(c => c.Enabled);
        return n >= MinControls
            ? new("controls", DoneState, $"{Count(n, "control point")}", line)
            : new("controls", WaitingState, n == 0 ? "Import the surveyed control points" : $"{Count(n, "control point")}: import at least {MinControls}", line);
    }

    private static GuideStep Pins(Project p)
    {
        var projectors = p.Stage.Projectors;
        if (projectors.Length == 0)
            return new("pins", WaitingState, "Add the projectors", "ADD projector <json>");
        foreach (var pj in projectors)
        {
            var pins = pj.Pins.Count(x => x.Enabled);
            if (pins >= MinPins)
                continue;
            var used = pj.Pins.Select(x => x.ControlId).ToHashSet(StringComparer.Ordinal);
            var cp = p.Stage.Controls.FirstOrDefault(c => c.Enabled && !used.Contains(c.Id));
            var line = cp is null
                ? $"ADD pin {pj.Id} <json>"
                : string.Create(CultureInfo.InvariantCulture,
                    $"ADD pin {pj.Id} {{\"controlId\":\"{cp.Id}\",\"world\":{Json(p.Stage.Site.ToStage(cp.Grid))},\"pixel\":{{\"x\":<x>,\"y\":<y>}},\"enabled\":true}}");
            return new("pins", WaitingState,
                $"{pj.Name} has {Count(pins, "pin")}: pin {MinPins - pins} more on control points{(cp is null ? "" : $" (next: {cp.Name})")}", line);
        }
        return new("pins", DoneState, $"every projector has {MinPins} pins or more", $"ADD pin {projectors[0].Id} <json>");
    }

    private static string Json(Numerics.Vec3d v) => string.Create(CultureInfo.InvariantCulture, $"{{\"x\":{v.X:R},\"y\":{v.Y:R},\"z\":{v.Z:R}}}");

    private static GuideStep Solve(Project p)
    {
        var pinned = p.Stage.Projectors.Where(pj => pj.Pins.Count(x => x.Enabled) >= MinPins).ToList();
        if (pinned.Count == 0)
            return new("solve", WaitingState, "Pin the projectors first", "PROJECTOR SOLVE <projector>");
        foreach (var pj in pinned)
        {
            if (pj.Uncertainty is not null || pj.CalibrationId is not null)
                continue;
            if (pj.Priors.ThrowRatio <= 0 && pj.Pins.Count(x => x.Enabled) < 6)
                return new("solve", WaitingState, $"{pj.Name}: give the lens's throw ratio first — {Count(pj.Pins.Count(x => x.Enabled), "pin")} cannot tell zoom from distance",
                    $"PROJECTOR PRIOR {pj.Id} THROW <ratio>");
            return new("solve", WaitingState, $"Solve {pj.Name} from its pins{(pj.Priors.ThrowRatio > 0 ? " with its lens prior" : "")}", $"PROJECTOR SOLVE {pj.Id}");
        }
        return new("solve", DoneState, $"{Count(pinned.Count, "projector")} solved", $"PROJECTOR SOLVE {pinned[0].Id}");
    }

    private static GuideStep Stacks(Project p)
    {
        if (p.Stage.Stacks.IsEmpty)
            return new("stacks", DoneState, "no stacks (STACK NEW groups projectors lighting one picture)", "STACK NEW <name> <projector> <projector>");
        foreach (var sk in p.Stage.Stacks)
        {
            if (sk.ProjectorIds.IsDefault || sk.Members.Length < 2)
                return new("stacks", WaitingState, $"{sk.Name} stacks fewer than two projectors — remove it, or STACK NEW one that names them", $"REMOVE {sk.Id}");
            var scan = p.Stage.Scans.LastOrDefault(s => sk.Members.All(id => s.ProjectorIds.Contains(id, StringComparer.Ordinal)));
            var from = scan?.Id ?? "<scan>";
            // The output that draws a member is its enabled one first, as STATE, preflight and INDEX take it.
            var outputs = sk.Members.Select(id => p.Outputs.Where(o => o.Mode == OutputMode.Projector && o.ProjectorId == id).OrderByDescending(o => o.Enabled)
                .FirstOrDefault()).ToList();
            var aligned = sk.Members.Zip(outputs).All(x => x.First == sk.MasterId || x.Second?.Measured is { Lattice: not null } m && m.StackId == sk.Id);
            // A lattice fitted to poses that are gone (the member or the master solved again since) aligns nothing any more.
            var moved = !aligned ? null : sk.Members.Zip(outputs).Where(x => x.First != sk.MasterId && x.Second?.Measured is { } m
                && Core.Stacks.StackPoses.MovedSince(p, sk, x.First, m)).Select(x => x.First).FirstOrDefault();
            if (moved is not null)
                return new("stacks", WaitingState, $"{p.FindProjector(moved)?.Name ?? moved} or {p.FindProjector(sk.MasterId)?.Name ?? sk.MasterId} moved since {sk.Name} was aligned — align it again"
                    + (scan is null ? "" : " (scan again first if it moved)"), scan is null ? $"SCAN START <camera> {string.Join(' ', sk.Members)}" : $"STACK ALIGN {sk.Id} FROM {from}");
            if (!aligned)
                return new("stacks", WaitingState, scan is null
                    ? $"Scan {sk.Name}'s members through one camera, then align them"
                    : $"Align {sk.Name} from {scan.Name}", scan is null ? $"SCAN START <camera> {string.Join(' ', sk.Members)}" : $"STACK ALIGN {sk.Id} FROM {from}");
            var matched = outputs.All(o => o?.Measured is { Color: not null } m && m.StackId == sk.Id);
            if (!matched)
                return new("stacks", WaitingState, $"Match {sk.Name}'s colour", $"STACK MATCH {sk.Id} FROM {from}");
        }
        return new("stacks", DoneState, $"{Count(p.Stage.Stacks.Length, "stack")} aligned and matched", $"STACK LIST");
    }

    private static GuideStep Blend(Project p, GuideFacts facts)
    {
        var blended = p.Outputs.Count(o => o.Enabled && o.Mode == OutputMode.Projector && o.ProjectorId is not null && o.Blend.Auto3D);
        if (blended < 2)
            return new("blend", DoneState, blended == 0 ? "no projector blends automatically" : "one projector: nothing to blend", "BLEND SOLVE");
        return facts.BlendCurrent
            ? new("blend", DoneState, $"{blended} projectors blended for the stage as it is", "BLEND SOLVE")
            : new("blend", WaitingState, $"Blend the {blended} projectors for the stage as it is", "BLEND SOLVE");
    }

    private static GuideStep Score(Project p, GuideFacts facts)
    {
        var solved = p.Stage.Projectors.Where(pj => pj.CalibrationId is not null || pj.Uncertainty is not null).ToList();
        if (solved.Count == 0)
            return new("score", WaitingState, "Solve the projectors first", "CALIBRATION STATUS ALL");
        foreach (var pj in solved)
        {
            var score = facts.ScoreOf(pj.Id);
            if (score is null || score.Grade.Length == 0)
                return new("score", WaitingState, $"{pj.Name} has no calibration score yet", $"CALIBRATION STATUS {pj.Id}");
            if (score.Grade is not ("A" or "B"))
            {
                var fix = score.Terms.OrderByDescending(t => t.Penalty).FirstOrDefault(t => t.Fix.Length > 0)?.Fix;
                return new("score", WaitingState, $"{pj.Name} scores {score.Grade}: {score.Words}", fix ?? $"CALIBRATION STATUS {pj.Id}");
            }
        }
        return new("score", DoneState, $"{Count(solved.Count, "projector")} at grade B or better", "CALIBRATION STATUS ALL");
    }
}
