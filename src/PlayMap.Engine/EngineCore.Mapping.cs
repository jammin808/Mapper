using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;

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

    private StageMeshes? _meshes;
    private BlendSolveResult? _lastBlendSolve;

    /// <summary>The project's meshes, loaded once per asset and shared by the solvers and the render host.</summary>
    public StageMeshes Meshes => LazyInitializer.EnsureInitialized(ref _meshes, () => new StageMeshes(MeshFolder));

    /// <summary>The last blend BLEND SOLVE computed (null before the first).</summary>
    public BlendSolveResult? LastBlendSolve => Volatile.Read(ref _lastBlendSolve);

    /// <summary>Raised on the control lane when BLEND SOLVE has computed a blend; the render host installs its
    /// maps at once (while the project's blend inputs are still the ones solved).</summary>
    public event Action<BlendSolveResult>? BlendSolved;

    /// <summary>The folder a project's relative mesh paths are read from: the open project file's, else the
    /// node's project folder.</summary>
    private string? MeshFolder() => _projectPath is { } path ? Path.GetDirectoryName(path) : Services.ProjectFolder;

    /// <summary>BLEND SOLVE [&lt;output&gt;|ALL]: the automatic 3D blend of every output that blends
    /// automatically — they light the stage together, so they are solved together; naming one output checks
    /// that it takes part and answers for it alone. Pure maths on the control lane (no GPU); the maps reach the
    /// render host through <see cref="BlendSolved"/>. A stage whose blend inputs have not moved since the last
    /// solve is refused: the solve would change nothing.</summary>
    private CommandReply SolveBlend(List<string> args)
    {
        if (args.Count > 1)
            return CommandReply.Fail("BLEND SOLVE [<output>|ALL]");
        var p = Store.Project;
        Output? named = null;
        if (args.Count == 1 && !args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            named = ResolveOutput(args[0]);
            if (named is null)
                return CommandReply.Fail($"no output {args[0]}");
            if (BlendInputs.WhyNot(named, p) is { } why)
                return CommandReply.Fail(why);
        }
        var inputs = BlendInputs.Of(p);
        if (inputs.IsEmpty)
            return CommandReply.Fail("no output blends automatically — an enabled output in Projector mode, driving a projector, with automatic blending on");
        if (LastBlendSolve is { } last && last.Inputs.Equals(inputs))
            return CommandReply.Fail($"the blend is already solved for this stage — nothing has moved since ({last.Solution.Words})");
        var solution = inputs.Solve(Meshes);
        var result = new BlendSolveResult(inputs, solution, Store.Current.Revision);
        Volatile.Write(ref _lastBlendSolve, result);
        BlendSolved?.Invoke(result);
        var answered = named is null ? solution.Outputs : solution.Outputs.Where(o => o.OutputId == named.Id).ToList();
        var json = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("outputs");
            foreach (var o in answered)
            {
                w.WriteStartObject();
                w.WriteString("id", o.OutputId);
                w.WriteString("name", p.FindOutput(o.OutputId)?.Name ?? o.OutputId);
                w.WriteString("projector", o.ProjectorId);
                w.WriteNumber("covered", Math.Round(o.CoveredFraction, 4));
                w.WriteNumber("overlap", Math.Round(o.OverlapFraction, 4));
                w.WriteNumber("shadow", Math.Round(o.ShadowFraction, 4));
                w.WriteNumber("maxOverlap", o.MaxOverlap);
                w.WriteStartArray("overlapsWith");
                foreach (var n in o.OverlapsWith)
                    w.WriteStringValue(n);
                w.WriteEndArray();
                w.WriteNumber("mapWidth", o.Map.Width);
                w.WriteNumber("mapHeight", o.Map.Height);
                w.WriteString("words", o.Words);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("problems");
            foreach (var problem in solution.Problems)
                w.WriteStringValue(problem);
            w.WriteEndArray();
            w.WriteString("words", solution.Words);
            w.WriteEndObject();
        });
        var count = solution.Outputs.Count;
        return CommandReply.Success(json, named is null
            ? $"blend solved for {count} output{(count == 1 ? "" : "s")}"
            : $"blend solved for {count} output{(count == 1 ? "" : "s")}, {named.Name} among them");
    }

    private CommandReply ApplyScan(ScanRecord scan) =>
        CommandReply.Fail($"applying {scan.Name} needs the scan module, which is not on this node yet");
}
