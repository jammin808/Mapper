using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>The mapping verbs that call the solvers. A solver that is not on this node yet refuses with the
/// reason rather than pretending.</summary>
public sealed partial class EngineCore
{
    /// <summary>PROJECTOR SOLVE: the pose (and the unlocked optics) from the projector's pins. "LOCK SHIFT|FOCAL|
    /// NONE" overrides the projector's own locks for this solve and is kept on it. A solve worse than the one the
    /// projector has is still applied — the operator asked — but the words say it got worse.</summary>
    private CommandReply SolveProjector(List<string> args)
    {
        var pj = Store.Project.FindProjector(args[0]) ?? Store.Project.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (pj is null)
            return CommandReply.Fail($"no projector {args[0]}");
        var locks = pj.Locks;
        var li = args.FindIndex(a => a.Equals("LOCK", StringComparison.OrdinalIgnoreCase));
        if (li >= 0)
        {
            locks = SolveLocks.None;
            foreach (var word in args.Skip(li + 1))
            {
                locks |= word.ToUpperInvariant() switch
                {
                    "SHIFT" => SolveLocks.Shift,
                    "FOCAL" or "ZOOM" => SolveLocks.Focal,
                    "DISTORTION" or "LENS" => SolveLocks.Distortion,
                    "NONE" => SolveLocks.None,
                    _ => throw new EditRefusedException($"LOCK takes SHIFT, FOCAL, DISTORTION or NONE — not {word}"),
                };
            }
        }
        var pins = pj.Pins.Count(x => x.Enabled);
        if (pins < 4)
            return CommandReply.Fail($"{pj.Name} has {pins} pin{(pins == 1 ? "" : "s")} — a solve needs six or more spread out (four with the optics locked)");
        var solution = PoseSolver.Solve(pj with { Locks = locks }, PoseSolveOptions.Default with { Locks = locks, InitialPose = pj.Pose });
        if (solution.Inliers.Count(x => x) < 4 || double.IsNaN(solution.RmsPx))
            return CommandReply.Fail($"{pj.Name}: no solve — {solution.Words}");
        var before = pj.SolveResidualPx;
        var r = Store.Apply($"solve {pj.Name}", p => p.WithStage(st => st with
        {
            Projectors = st.Projectors.Update(pj.Id, x => x with
            {
                Intrinsics = solution.Intrinsics,
                Lens = solution.Lens,
                Pose = solution.Pose,
                Locks = locks,
                SolveResidualPx = Math.Max(solution.RmsPx, 1e-6),
            }),
        }));
        if (!r.Changed)
            return FromEdit(r);
        var worse = before > 0 && solution.RmsPx > before * 1.5 ? $" (worse than the {before:0.0} px it had — check the pins)" : "";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("rmsPx", Math.Round(solution.RmsPx, 3));
            w.WriteStartArray("residualsPx");
            foreach (var e in solution.ResidualsPx)
                w.WriteNumberValue(Math.Round(e, 2));
            w.WriteEndArray();
            w.WriteStartArray("inliers");
            foreach (var i in solution.Inliers)
                w.WriteBooleanValue(i);
            w.WriteEndArray();
            w.WriteString("words", solution.Words);
            w.WriteEndObject();
        }), $"{pj.Name}: {solution.Words}{worse}");
    }

    private CommandReply SolveBlend(List<string> args) =>
        CommandReply.Fail("the 3D blend solver is not on this node yet");

    private CommandReply ApplyScan(ScanRecord scan) =>
        CommandReply.Fail($"applying {scan.Name} needs the scan module, which is not on this node yet");
}
