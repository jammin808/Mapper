using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Workflows;

public enum StepKind
{
    /// <summary>A command sent to the projector.</summary>
    Command,

    /// <summary>A reading taken from the projector (and recorded).</summary>
    Read,

    /// <summary>The engine captures its camera frame here (the black frame).</summary>
    Capture,

    /// <summary>Words for the operator: something PlayMap cannot do through this protocol.</summary>
    Instruction,
}

/// <summary>One step of a plan: its words for the desk, its kind, the capability it uses and how long to let the
/// projector settle after it. Command and read steps carry the call; instructions carry only words.</summary>
public sealed record WorkflowStep(
    string Words,
    StepKind Kind,
    ProjectorCapabilities Uses,
    TimeSpan Settle,
    Func<IProjectorDriver, CancellationToken, Task<CommandResult>>? Command = null,
    Func<IProjectorDriver, CancellationToken, Task<DriverReading>>? Read = null);

/// <summary>A plan for one projector — the steps a workflow would run, so the desk can show them before (and as) they run.</summary>
public sealed record WorkflowPlan(string Name, string LinkId, ImmutableArray<WorkflowStep> Steps)
{
    /// <summary>"1. command — open the shutter" per step.</summary>
    public IReadOnlyList<string> Describe() =>
        Steps.Select((s, i) => (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + KindWord(s.Kind) + " — " + s.Words).ToList();

    public IReadOnlyList<string> Instructions => Steps.Where(s => s.Kind == StepKind.Instruction).Select(s => s.Words).ToList();

    private static string KindWord(StepKind k) => k switch
    {
        StepKind.Command => "command",
        StepKind.Read => "read",
        StepKind.Capture => "capture",
        _ => "instruction",
    };
}

public sealed record StepOutcome(WorkflowStep Step, CommandResult Result, DeviceState? Reading);

public sealed record WorkflowOutcome(WorkflowPlan Plan, ImmutableArray<StepOutcome> Steps)
{
    /// <summary>Every command and read reached the projector and got a yes (instructions are the operator's).</summary>
    public bool AllSucceeded => Steps.All(s => s.Step.Kind == StepKind.Instruction || s.Result.Succeeded);

    /// <summary>The lens positions a read step recorded, when one did.</summary>
    public LensPositions? Lens => Steps.Select(s => s.Reading?.Lens).LastOrDefault(l => l is { Any: true });
}

/// <summary>How long a shutter takes. No vendor document gives a mechanical shutter's or a laser blank's time; a
/// Panasonic projector's configurable shutter fade (SEFS1/SEFS2, 0.0 s = off by default in its table) is the
/// only documented one. <see cref="Margin"/> covers the rest and should be measured on site.</summary>
public sealed record ShutterTiming(TimeSpan Fade, TimeSpan Margin)
{
    public static readonly ShutterTiming Default = new(TimeSpan.Zero, TimeSpan.FromMilliseconds(500));

    public TimeSpan Settle => Fade + Margin;
}

/// <summary>A lens prior for the solver: raw positions always; a throw ratio and lens shift only when a calibration
/// maps this lens's raw units to optics (no projector vendor publishes that mapping).</summary>
public sealed record LensPrior(LensPositions Raw, double? ThrowRatio, double? ShiftX, double? ShiftY, string Words)
{
    /// <summary>Intrinsics from the prior at a native raster, or null when there is no throw ratio.</summary>
    public Intrinsics? ToIntrinsics(int width, int height) =>
        ThrowRatio is { } t ? Intrinsics.FromThrowRatio(width, height, t, ShiftX ?? 0, ShiftY ?? 0) : null;
}

/// <summary>One solved pairing of raw lens positions with the optics a solve found (throw ratio; shift as a
/// fraction of the image, +x right, +y up).</summary>
public sealed record LensSample(LensPositions Raw, double ThrowRatio, double ShiftX, double ShiftY);

/// <summary>
/// What PlayMap has learned about one lens: samples from solves, each pairing raw motor positions with solved
/// optics. With two or more samples spanning a reading, the optics are interpolated (piecewise linear in each
/// axis); outside the span nothing is extrapolated.
/// </summary>
public sealed record LensCalibration(ImmutableArray<LensSample> Samples)
{
    public static readonly LensCalibration Empty = new(ImmutableArray<LensSample>.Empty);

    public LensCalibration Add(LensSample s) => new(Samples.Add(s));

    public double? ThrowRatioAt(int? zoom) => Interpolate(zoom, s => s.Raw.Zoom, s => s.ThrowRatio);

    public double? ShiftXAt(int? shiftH) => Interpolate(shiftH, s => s.Raw.ShiftHorizontal, s => s.ShiftX);

    public double? ShiftYAt(int? shiftV) => Interpolate(shiftV, s => s.Raw.ShiftVertical, s => s.ShiftY);

    private double? Interpolate(int? at, Func<LensSample, int?> raw, Func<LensSample, double> value)
    {
        if (at is not { } x)
            return null;
        var points = Samples.Where(s => raw(s).HasValue).Select(s => (X: (double)raw(s)!.Value, Y: value(s)))
            .GroupBy(p => p.X).Select(g => (g.Key, g.Average(p => p.Y))).OrderBy(p => p.Key).ToList();
        if (points.Count == 1 && points[0].Key == x)
            return points[0].Item2;
        if (points.Count < 2 || x < points[0].Key || x > points[^1].Key)
            return null;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (x0, y0) = points[i];
            var (x1, y1) = points[i + 1];
            if (x >= x0 && x <= x1)
                return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
        }
        return null;
    }
}

/// <summary>
/// The orchestration the engine runs on a projector around mapping: each workflow returns the plan it would run —
/// commands the projector's protocol has, instructions for what it does not — so the desk shows the steps before
/// they run, and <see cref="WorkflowRunner"/> runs them through the manager's gate.
/// </summary>
public static class MappingWorkflows
{
    /// <summary>The capabilities a driver will actually send on its link: unverified ones count only when allowed.</summary>
    public static ProjectorCapabilities Effective(IProjectorDriver d) =>
        d.Capabilities & ~(d.Link.AllowUnverified ? ProjectorCapabilities.None : d.Unverified);

    /// <summary>
    /// Before a scan: the shutter open, freeze off, built-in patterns off, the projector's own geometry correction
    /// (and edge blending) off, and the lens read and recorded — each a command where the protocol has it and an
    /// instruction for the operator where it does not (or where it is gated as unverified on this link).
    /// </summary>
    public static WorkflowPlan PrepareForScan(IProjectorDriver d, ShutterTiming? timing = null)
    {
        var caps = Effective(d);
        var label = ProjectorProtocols.Label(d.Protocol);
        var steps = ImmutableArray.CreateBuilder<WorkflowStep>();
        var settle = (timing ?? ShutterTiming.Default).Settle;

        steps.Add(Has(caps, ProjectorCapabilities.Shutter)
            ? new WorkflowStep("open the shutter", StepKind.Command, ProjectorCapabilities.Shutter, settle, (x, ct) => x.SetShutterAsync(false, ct))
            : Instruction($"Open the projector's shutter (and cancel any AV mute) — {Why(d, ProjectorCapabilities.Shutter, label)}."));
        steps.Add(Has(caps, ProjectorCapabilities.Freeze)
            ? new WorkflowStep("freeze off", StepKind.Command, ProjectorCapabilities.Freeze, TimeSpan.Zero, (x, ct) => x.SetFreezeAsync(false, ct))
            : Instruction($"Make sure the picture is not frozen — {Why(d, ProjectorCapabilities.Freeze, label)}."));
        steps.Add(Has(caps, ProjectorCapabilities.TestPatterns)
            ? new WorkflowStep("built-in test patterns off", StepKind.Command, ProjectorCapabilities.TestPatterns, TimeSpan.Zero, (x, ct) => x.ShowTestPatternAsync(TestPatterns.Off, ct))
            : Instruction($"Make sure no built-in test pattern is showing — {Why(d, ProjectorCapabilities.TestPatterns, label)}."));
        if (Has(caps, ProjectorCapabilities.GeometryReset))
        {
            steps.Add(new WorkflowStep("the projector's own geometry correction (keystone/warp) off", StepKind.Command, ProjectorCapabilities.GeometryReset, TimeSpan.Zero, (x, ct) => x.ResetGeometryAsync(ct)));
        }
        else
        {
            steps.Add(Instruction($"Reset keystone and warp (and turn any internal edge blend off) on the projector's own menu before scanning — {Why(d, ProjectorCapabilities.GeometryReset, label)}. A scan through the projector's own correction maps the correction, not the projector."));
        }
        if (Has(caps, ProjectorCapabilities.EdgeBlend))
            steps.Add(new WorkflowStep("the projector's own edge blending off", StepKind.Command, ProjectorCapabilities.EdgeBlend, TimeSpan.Zero, (x, ct) => x.SetEdgeBlendAsync(EdgeBlendSettings.Off, ct)));
        steps.Add(Has(caps, ProjectorCapabilities.LensPositionRead)
            ? new WorkflowStep("read and record the lens positions", StepKind.Read, ProjectorCapabilities.LensPositionRead, TimeSpan.Zero, Read: (x, ct) => x.ReadLensAsync(ct))
            : Instruction($"Lock the lens: zoom, focus and shift must not move after the scan — the lens position cannot be read ({Why(d, ProjectorCapabilities.LensPositionRead, label)})."));
        return new WorkflowPlan("prepare for scan", d.Link.Id, steps.ToImmutable());
    }

    /// <summary>
    /// A black frame: the shutter closed and settled, the capture, the shutter open and settled. Without a shutter
    /// command the engine outputs black instead — video black, which still carries the projector's black floor.
    /// </summary>
    public static WorkflowPlan BlackFrame(IProjectorDriver d, ShutterTiming? timing = null)
    {
        var caps = Effective(d);
        var settle = (timing ?? ShutterTiming.Default).Settle;
        if (!Has(caps, ProjectorCapabilities.Shutter))
        {
            return new WorkflowPlan("black frame", d.Link.Id,
            [
                Instruction($"PlayMap outputs black for the black frame — {Why(d, ProjectorCapabilities.Shutter, ProjectorProtocols.Label(d.Protocol))}; video black still carries the projector's black floor."),
                new WorkflowStep("capture the black frame (video black)", StepKind.Capture, ProjectorCapabilities.None, TimeSpan.Zero),
            ]);
        }
        return new WorkflowPlan("black frame", d.Link.Id,
        [
            new WorkflowStep("close the shutter", StepKind.Command, ProjectorCapabilities.Shutter, settle, (x, ct) => x.SetShutterAsync(true, ct)),
            new WorkflowStep("capture the black frame (shutter closed)", StepKind.Capture, ProjectorCapabilities.None, TimeSpan.Zero),
            new WorkflowStep("open the shutter", StepKind.Command, ProjectorCapabilities.Shutter, settle, (x, ct) => x.SetShutterAsync(false, ct)),
        ]);
    }

    /// <summary>Identify a projector: its own pattern for <paramref name="hold"/>, then patterns off; without one, an
    /// identification card through PlayMap's own output.</summary>
    public static WorkflowPlan Identify(IProjectorDriver d, TimeSpan hold)
    {
        var caps = Effective(d);
        if (!Has(caps, ProjectorCapabilities.TestPatterns) || !d.CanIdentify)
        {
            return new WorkflowPlan("identify", d.Link.Id,
            [
                Instruction($"PlayMap shows an identification card on this projector's output — {ProjectorProtocols.Label(d.Protocol)} has no built-in pattern PlayMap can show."),
            ]);
        }
        return new WorkflowPlan("identify", d.Link.Id,
        [
            new WorkflowStep("show the projector's own identify pattern", StepKind.Command, ProjectorCapabilities.TestPatterns, hold, (x, ct) => x.ShowTestPatternAsync(TestPatterns.Identify, ct)),
            new WorkflowStep("built-in test patterns off", StepKind.Command, ProjectorCapabilities.TestPatterns, TimeSpan.Zero, (x, ct) => x.ShowTestPatternAsync(TestPatterns.Off, ct)),
        ]);
    }

    /// <summary>
    /// A lens prior from raw positions. No projector vendor publishes how its raw zoom/shift units map to a throw
    /// ratio or a shift fraction (the motor ranges differ by lens), so without a calibration learned from solves on
    /// this lens the prior is the raw positions alone, and says so.
    /// </summary>
    public static LensPrior LensFromProjector(LensPositions raw, ProjectorProtocol protocol, LensCalibration? calibration = null)
    {
        var label = ProjectorProtocols.Label(protocol);
        if (!raw.Any)
            return new LensPrior(raw, null, null, null, $"no lens position was read through {label}; the solve starts from the datasheet or a guess");
        var cal = calibration ?? LensCalibration.Empty;
        var t = cal.ThrowRatioAt(raw.Zoom);
        var sx = cal.ShiftXAt(raw.ShiftHorizontal);
        var sy = cal.ShiftYAt(raw.ShiftVertical);
        if (t is null && sx is null && sy is null)
            return new LensPrior(raw, null, null, null,
                $"raw positions recorded ({raw.Words()}); no public document maps {label}'s raw units to a throw ratio or a lens shift — solves on this lens teach PlayMap the mapping");
        var parts = new List<string>();
        if (t is { } tr)
            parts.Add("throw ratio " + tr.ToString("0.###", CultureInfo.InvariantCulture));
        if (sx is { } x)
            parts.Add("shift x " + x.ToString("0.###", CultureInfo.InvariantCulture));
        if (sy is { } y)
            parts.Add("shift y " + y.ToString("0.###", CultureInfo.InvariantCulture));
        return new LensPrior(raw, t, sx, sy, string.Join(", ", parts) + $" — interpolated from {cal.Samples.Length.ToString(CultureInfo.InvariantCulture)} solves on this lens");
    }

    private static bool Has(ProjectorCapabilities caps, ProjectorCapabilities c) => (caps & c) != 0;

    private static WorkflowStep Instruction(string words) => new(words, StepKind.Instruction, ProjectorCapabilities.None, TimeSpan.Zero);

    /// <summary>Why a capability is not used: not in the protocol, or gated as unverified on this link.</summary>
    private static string Why(IProjectorDriver d, ProjectorCapabilities c, string label) =>
        (d.Capabilities & c) == 0
            ? $"{label} has no published command for it"
            : $"{CapabilityWords.Word(c)} through {label} is unverified on this link (allow unverified commands after testing on the model)";
}

/// <summary>Runs a plan through the manager's gate for the projector (or straight on a driver), waiting each step's
/// settle time; a capture runs only when the step before it succeeded.</summary>
public sealed class WorkflowRunner(Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((t, ct) => Task.Delay(t, ct));

    public Task<WorkflowOutcome> RunAsync(DeviceManager manager, WorkflowPlan plan, Func<CancellationToken, Task>? capture, CancellationToken ct) =>
        RunCoreAsync(plan,
            (step, c) => manager.RunAsync(plan.LinkId, step.Command!, c),
            (step, c) => manager.ReadAsync(plan.LinkId, step.Read!, c),
            capture, ct);

    public Task<WorkflowOutcome> RunAsync(IProjectorDriver driver, WorkflowPlan plan, Func<CancellationToken, Task>? capture, CancellationToken ct) =>
        RunCoreAsync(plan, (step, c) => step.Command!(driver, c), (step, c) => step.Read!(driver, c), capture, ct);

    private async Task<WorkflowOutcome> RunCoreAsync(
        WorkflowPlan plan,
        Func<WorkflowStep, CancellationToken, Task<CommandResult>> command,
        Func<WorkflowStep, CancellationToken, Task<DriverReading>> read,
        Func<CancellationToken, Task>? capture,
        CancellationToken ct)
    {
        var outcomes = ImmutableArray.CreateBuilder<StepOutcome>(plan.Steps.Length);
        CommandResult? previous = null;
        foreach (var step in plan.Steps)
        {
            StepOutcome outcome;
            switch (step.Kind)
            {
                case StepKind.Command when step.Command is not null:
                    var r = await command(step, ct).ConfigureAwait(false);
                    outcome = new StepOutcome(step, r, null);
                    previous = r;
                    break;
                case StepKind.Read when step.Read is not null:
                    var reading = await read(step, ct).ConfigureAwait(false);
                    outcome = new StepOutcome(step, reading.Result, reading.State);
                    previous = reading.Result;
                    break;
                case StepKind.Capture:
                    if (previous is { Succeeded: false })
                    {
                        outcome = new StepOutcome(step, CommandResult.Refused("capture", $"not captured — the step before did not succeed ({previous.Words})"), null);
                    }
                    else if (capture is null)
                    {
                        outcome = new StepOutcome(step, CommandResult.Refused("capture", "no capture was given to the runner"), null);
                    }
                    else
                    {
                        await capture(ct).ConfigureAwait(false);
                        outcome = new StepOutcome(step, CommandResult.Accepted("capture", "captured"), null);
                    }
                    break;
                default:
                    outcome = new StepOutcome(step, CommandResult.Refused("instruction", step.Words), null);
                    break;
            }
            outcomes.Add(outcome);
            if (step.Settle > TimeSpan.Zero && outcome.Result.Stage >= CommandStage.Sent)
                await _delay(step.Settle, ct).ConfigureAwait(false);
        }
        return new WorkflowOutcome(plan, outcomes.ToImmutable());
    }
}
