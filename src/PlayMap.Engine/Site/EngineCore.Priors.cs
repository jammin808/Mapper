using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>PROJECTOR PRIOR (what a solve may assume about a projector's lens and position) and the long-throw solve PROJECTOR SOLVE
/// runs: the priors as residual rows, the focal locked from the lens when the pins cannot carry it, and the uncertainty reported.</summary>
public sealed partial class EngineCore
{
    /// <summary>"±2", "±2%" or "+-2" as a number (null when the word is not one).</summary>
    private static double? PlusMinus(string word)
    {
        var w = word.StartsWith('±') ? word[1..] : word.StartsWith("+-", StringComparison.Ordinal) ? word[2..] : null;
        if (w is null)
            return null;
        w = w.TrimEnd('%');
        return double.TryParse(w, NumberStyles.Float, SiteInv, out var v) && double.IsFinite(v) && v > 0 ? v : null;
    }

    /// <summary>PROJECTOR PRIOR &lt;projector&gt; THROW &lt;ratio&gt; [±&lt;pct&gt;] | AT &lt;control&gt; [±&lt;m&gt;] | AT &lt;x&gt; &lt;y&gt; &lt;z&gt; [±&lt;m&gt;] |
    /// CLEAR. A throw keeps the position and a position keeps the throw; a prior bound to a control point follows it.</summary>
    private CommandReply ProjectorPrior(List<string> args)
    {
        const string Usage = "PROJECTOR PRIOR <projector> THROW <ratio> [±<pct>] | PROJECTOR PRIOR <projector> AT <control>|<x> <y> <z> [±<m>] | PROJECTOR PRIOR <projector> CLEAR";
        if (args.Count < 2)
            return CommandReply.Fail(Usage);
        var p = Edits.Project;
        var pj = p.FindProjector(args[0]) ?? p.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (pj is null)
            return CommandReply.Fail($"no projector {args[0]}");
        var before = pj.Priors;
        SolvePriors after;
        switch (args[1].ToUpperInvariant())
        {
            case "CLEAR" when args.Count == 2:
                after = SolvePriors.None;
                break;
            case "THROW" when args.Count is 3 or 4 && TryNumber(args[2], out var ratio) && ratio is > 0.05 and < 50:
            {
                var pct = PosePriors.DefaultThrowSigmaPct;
                if (args.Count == 4)
                {
                    if (PlusMinus(args[3]) is not { } given)
                        return CommandReply.Fail("the throw's uncertainty is ±<percent>, as ±2");
                    pct = given;
                }
                after = before with { ThrowRatio = ratio, ThrowRatioSigmaPct = pct, Source = "PROJECTOR PRIOR" };
                break;
            }
            case "AT" when args.Count >= 3:
            {
                var rest = args.Skip(2).ToList();
                var sigma = PosePriors.DefaultPositionSigmaM;
                if (rest.Count > 1 && PlusMinus(rest[^1]) is { } m)
                {
                    sigma = m;
                    rest.RemoveAt(rest.Count - 1);
                }
                if (rest.Count == 3 && TryNumber(rest[0], out var x) && TryNumber(rest[1], out var y) && TryNumber(rest[2], out var z))
                {
                    after = before with { Position = new Vec3d(x, y, z), ControlId = "", PositionSigmaM = sigma, Source = "PROJECTOR PRIOR" };
                }
                else if (rest.Count == 1 && (p.Stage.Controls.Find(rest[0]) ?? p.Stage.Controls.FirstOrDefault(c => c.Name.Equals(rest[0], StringComparison.OrdinalIgnoreCase))) is { } cp)
                {
                    after = before with { Position = p.Stage.Site.ToStage(cp.Grid), ControlId = cp.Id, PositionSigmaM = sigma, Source = "PROJECTOR PRIOR" };
                }
                else
                {
                    return CommandReply.Fail(rest.Count == 1 ? $"no control point {rest[0]} — SITE POINTS imports them" : Usage);
                }
                break;
            }
            default:
                return CommandReply.Fail(Usage);
        }
        if (after == before)
            return CommandReply.Fail($"{pj.Name}'s priors are that already — this would change nothing");
        var r = Edits.Apply($"priors of {pj.Name}", pp => pp.WithStage(st => st with { Projectors = st.Projectors.Update(pj.Id, x => x with { Priors = after }) }));
        if (!r.Changed)
            return FromEdit(r);
        var words = PriorWords(Edits.Project, after);
        var staged = EditingBlind;
        var said = Staged(staged, words.Length > 0 ? $"{pj.Name}: {words}" : $"{pj.Name}: no priors");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("priors");
            w.WriteNumber("throw", after.ThrowRatio);
            w.WriteNumber("throwSigmaPct", after.ThrowRatio > 0 ? after.ThrowRatioSigmaPct : 0);
            if (after.Position is { } at)
            {
                w.WriteStartArray("position");
                w.WriteNumberValue(at.X);
                w.WriteNumberValue(at.Y);
                w.WriteNumberValue(at.Z);
                w.WriteEndArray();
            }
            else
            {
                w.WriteNull("position");
            }
            w.WriteString("controlId", after.ControlId);
            w.WriteNumber("positionSigmaM", after.Position is null ? 0 : after.PositionSigmaM);
            w.WriteEndObject();
            w.WriteBoolean("staged", staged);
            w.WriteString("words", said);
            w.WriteEndObject();
        }), said);
    }

    /// <summary>PROJECTOR SOLVE's solve (wp2-facade): the projector's priors as rows, the focal locked from the lens when the pins
    /// cannot carry it (the locks it answers then hold the focal; with no LOCK word given, such a lock is asked again), the
    /// uncertainty with the priors' information. A pin bound to a control point that is not at it is refused with the line that puts
    /// it there: a solve never runs on a bound pin's stale world point.</summary>
    private LongThrowResult SolveWithPriors(Projector pj, SolveLocks locks, bool retestLock)
    {
        if (BoundPinAway(Edits.Project.Stage, pj) is { } away)
            throw new EditRefusedException(away);
        return LongThrowSolve.Solve(pj with { Locks = locks }, PoseSolveOptions.Default with { Locks = locks, InitialPose = pj.Pose }, retestLock);
    }

    /// <summary>The refusal for a projector whose enabled pin is bound to a control point it is not at, with the line that puts it
    /// there; null when every bound pin sits at its point. Every solve from pins asks it first.</summary>
    internal static string? BoundPinAway(Stage stage, Projector pj)
    {
        for (var i = 0; i < pj.Pins.Length; i++)
        {
            var pin = pj.Pins[i];
            if (!pin.Enabled || pin.ControlId.Length == 0 || stage.Controls.Find(pin.ControlId) is not { } cp)
                continue;
            var at = stage.Site.ToStage(cp.Grid);
            if ((pin.World - at).Length > BoundPinToleranceM)
                return string.Create(SiteInv,
                    $"pin {i + 1} is bound to {cp.Name} but is not at it — SET {pin.Id} world {{\"x\":{at.X:R},\"y\":{at.Y:R},\"z\":{at.Z:R}}}");
        }
        return null;
    }

    /// <summary>The priors a solve used, as the replies name them ("throw", "position").</summary>
    internal static List<string> PriorsUsed(SolvePriors priors, bool focal = true)
    {
        var used = new List<string>(2);
        if (focal && priors.ThrowRatio > 0)
            used.Add("throw");
        if (priors.Position is not null)
            used.Add("position");
        return used;
    }

    /// <summary>How far a bound pin's world point may be from its control point's (metres).</summary>
    internal const double BoundPinToleranceM = 0.001;

    /// <summary>The long-throw part of PROJECTOR SOLVE's reply: the uncertainty, what was locked, which priors were used.</summary>
    private static void WriteLongThrow(Utf8JsonWriter w, Projector pj, LongThrowResult r)
    {
        w.WritePropertyName("uncertainty");
        if (r.Uncertainty is { } u)
        {
            w.WriteStartObject();
            w.WriteNumber("wallPx", Math.Round(u.WallPx, 3));
            w.WriteNumber("proudPx", Math.Round(u.ProudPx, 3));
            w.WriteNumber("focalPct", Math.Round(u.FocalPct, 3));
            w.WriteNumber("axisM", Math.Round(u.AxisM, 3));
            w.WriteNumber("lateralM", Math.Round(u.LateralM, 3));
            w.WriteNumber("condition", Math.Round(u.Condition, 1));
            w.WriteString("weak", r.Weak);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNullValue();
        }
        w.WriteStartArray("locked");
        if (r.FocalLocked)
            w.WriteStringValue("focal");
        w.WriteEndArray();
        w.WriteStartArray("priorsUsed");
        foreach (var used in PriorsUsed(pj.Priors))
            w.WriteStringValue(used);
        w.WriteEndArray();
    }

    /// <summary>The long-throw words a solve's reply adds ("" when there is nothing to say beyond the solve's own).</summary>
    private static string LongThrowWords(LongThrowResult r) =>
        r.Uncertainty is { } u ? " — " + LongThrowSolve.Words(u with { Weak = r.Weak }) : "";
}
