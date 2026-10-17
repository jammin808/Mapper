using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Threading;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine;

/// <summary>A solve's life as STATE.solve shows it: its id and kind, running / done / stale / failed / cancelled,
/// progress 0..1, its words and — once applied — the result. A value: the lane replaces it at each step.</summary>
public sealed record SolveStatus(string Id, string Kind, string State, double Progress, string Words, BlendSolveResult? Result = null)
{
    public bool IsRunning => State == "running";
}

/// <summary>BLEND SOLVE off the control lane: the lane snapshots the inputs, a worker solves them, and the lane applies
/// the result as one step — or drops it, saying so, when the stage moved meanwhile. One solve runs at a time; the verb
/// answers at once with a receipt, and the outcome lands in the journal and STATE.solve.</summary>
public sealed partial class EngineCore
{
    /// <summary>Progress moves STATE's revision at most this often (in fractions of the solve).</summary>
    private const double SolveProgressStep = 0.05;

    private readonly CancellationTokenSource _solveStop = new();
    private SolveStatus? _solve;
    private double _solveProgress;
    private double _solveProgressShown;
    private TaskCompletionSource<SolveStatus>? _solveEnd;
    private long _solves;
    private int _solveStopped;

    /// <summary>The solver run on the worker (tests swap it to hold a solve open); default the stage's 3D blend.</summary>
    internal Func<BlendInputs, IProgress<double>, CancellationToken, Task<BlendSolution>>? BlendSolver { get; set; }

    /// <summary>The running or last solve (null before the first), with the worker's progress while it runs.</summary>
    public SolveStatus? Solve => Volatile.Read(ref _solve) is { IsRunning: true } running
        ? running with { Progress = Volatile.Read(ref _solveProgress) }
        : Volatile.Read(ref _solve);

    /// <summary>Completes once the lane has applied or dropped the running solve's result (at once when none runs).</summary>
    public Task<SolveStatus?> WhenSolved => Volatile.Read(ref _solveEnd) is { } end
        ? end.Task.ContinueWith(static t => (SolveStatus?)t.Result, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
        : Task.FromResult(Solve);

    /// <summary>BLEND SOLVE [&lt;output&gt;|ALL]: the automatic 3D blend of every output that blends automatically —
    /// they light the stage together, so they are solved together; naming one output checks that it takes part. A
    /// stage whose blend inputs have not moved since the last solve is refused (the solve would change nothing), and so
    /// is a second solve while one runs. Pure maths on a worker (no GPU); the maps reach the render host through
    /// <see cref="BlendSolved"/> once the lane applies them.</summary>
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
        if (Solve is { IsRunning: true } running)
            return CommandReply.Fail(string.Create(CultureInfo.InvariantCulture,
                $"a blend solve is already running ({running.Id}, {running.Progress:P0}) — one at a time; STATE.solve follows it"));
        var inputs = BlendInputs.Of(p);
        if (inputs.IsEmpty)
            return CommandReply.Fail("no output blends automatically — an enabled output in Projector mode, driving a projector, with automatic blending on");
        if (LastBlendSolve is { } last && last.Inputs.Equals(inputs))
            return CommandReply.Fail($"the blend is already solved for this stage — nothing has moved since ({last.Solution.Words})");

        var id = "bs" + (++_solves).ToString(CultureInfo.InvariantCulture);
        var count = inputs.Outputs.Length;
        var words = $"solving the blend for {count} output{(count == 1 ? "" : "s")}{(named is null ? "" : $", {named.Name} among them")}";
        var run = new SolveStatus(id, "blend", "running", 0, words);
        var end = new TaskCompletionSource<SolveStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _solveProgress, 0);
        _solveProgressShown = 0;
        Volatile.Write(ref _solve, run);
        Volatile.Write(ref _solveEnd, end);
        var solver = BlendSolver ?? DefaultBlendSolver;
        var ct = _solveStop.Token;
        var progress = new SolveProgress(this);
        _ = Task.Run(() => solver(inputs, progress, ct), ct).ContinueWith(t =>
        {
            _lane.Post(() => FinishSolve(run, inputs, t, end));
            // A lane that has stopped takes nothing more: the solve ends there, unapplied.
            if (ct.IsCancellationRequested)
                end.TrySetResult(run with { State = "cancelled", Words = "the node stopped before the blend was applied" });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        FactsChanged();
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "accepted");
            w.WriteString("effect", words);
            w.WriteString("solve", id);
            w.WriteEndObject();
        });
        return CommandReply.Success(payload, words) with { Applied = false };
    }

    private Task<BlendSolution> DefaultBlendSolver(BlendInputs inputs, IProgress<double> progress, CancellationToken ct) =>
        Compute.RunAsync(() => inputs.Solve(Meshes, progress, ct), ct);

    /// <summary>On the lane: the worker's result applied as one step while the stage is still the one solved, else
    /// dropped with the reason; the journal and STATE.solve say which.</summary>
    private void FinishSolve(SolveStatus run, BlendInputs inputs, Task<BlendSolution> solved, TaskCompletionSource<SolveStatus> end)
    {
        SolveStatus status;
        if (solved.IsCanceled || solved.Exception?.InnerException is OperationCanceledException)
        {
            status = run with { State = "cancelled", Words = "the blend solve was stopped before it finished" };
        }
        else if (solved.Exception?.InnerException is { } fault)
        {
            status = run with { State = "failed", Words = $"the blend solve failed: {fault.Message}" };
        }
        else if (!BlendInputs.Of(Store.Project).Equals(inputs))
        {
            status = run with
            {
                State = "stale", Progress = 1,
                Words = "the stage changed while the blend was solved — the result was dropped; BLEND SOLVE solves the stage as it is now",
            };
        }
        else
        {
#pragma warning disable VSTHRD002 // The task has completed (the continuation runs after it): Result reads it, it never waits.
            var solution = solved.Result;
#pragma warning restore VSTHRD002
            var result = new BlendSolveResult(inputs, solution, Store.Current.Revision);
            Volatile.Write(ref _lastBlendSolve, result);
            BlendSolved?.Invoke(result);
            var count = solution.Outputs.Count;
            status = run with { State = "done", Progress = 1, Words = $"blend solved for {count} output{(count == 1 ? "" : "s")}: {solution.Words}", Result = result };
        }
        Volatile.Write(ref _solve, status);
        Journal.Add("blend", status.Words, ok: status.State == "done");
        FactsChanged();
        end.TrySetResult(status);
    }

    /// <summary>STATE.solve: the running or last solve (null before the first); once done, each output's fractions.</summary>
    internal void WriteSolveState(Utf8JsonWriter w)
    {
        if (Solve is not { } s)
        {
            w.WriteNullValue();
            return;
        }
        var p = Store.Project;
        w.WriteStartObject();
        w.WriteString("id", s.Id);
        w.WriteString("kind", s.Kind);
        w.WriteString("state", s.State);
        w.WriteNumber("progress", Math.Round(s.Progress, 3));
        w.WriteString("words", s.Words);
        w.WriteStartArray("outputs");
        foreach (var o in s.Result?.Solution.Outputs ?? [])
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
        foreach (var problem in s.Result?.Solution.Problems ?? [])
            w.WriteStringValue(problem);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Stops a running solve (the node is stopping); its end says so. Once only.</summary>
    private void StopSolving()
    {
        if (Interlocked.Exchange(ref _solveStopped, 1) != 0)
            return;
        _solveStop.Cancel();
        Volatile.Read(ref _solveEnd)?.TrySetResult((Volatile.Read(ref _solve) ?? new SolveStatus("", "blend", "cancelled", 0, ""))
            with { State = "cancelled", Words = "the node stopped before the blend was applied" });
        _solveStop.Dispose();
    }

    /// <summary>The worker's progress: kept for STATE, and STATE's revision moved every <see cref="SolveProgressStep"/>.</summary>
    private sealed class SolveProgress(EngineCore engine) : IProgress<double>
    {
        public void Report(double value)
        {
            Volatile.Write(ref engine._solveProgress, Math.Clamp(value, 0, 1));
            if (value - engine._solveProgressShown < SolveProgressStep)
                return;
            engine._solveProgressShown = value;
            engine.FactsChanged();
        }
    }
}
