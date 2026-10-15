using PlayMap.Core.Scan;

namespace PlayMap.Core.Align;

/// <summary>One camera picture, gray8 at the camera's raster.</summary>
public sealed record CameraShot(byte[] Gray, int Width, int Height);

/// <summary>Shows one output's frame (every other output of the group black) and answers the camera's picture of it; a null frame
/// shows every output black.</summary>
public interface IAlignShooter
{
    Task<CameraShot> ShootAsync(int output, byte[]? frame, CancellationToken ct);
}

/// <summary>An output as the sequence shows to it: its id, raster and its projector's prior.</summary>
public sealed record AlignOutputSpec(string Id, string Name, int Width, int Height, ProjectorPrior Prior);

/// <summary>How far a sequence has come: the step ("identify", "scan"), the output it is on (−1: all), the share done (0–1) and words.</summary>
public readonly record struct AlignStepProgress(string Step, int Output, double Progress, string Words);

/// <summary>
/// The camera's side of an alignment, one output at a time: every output black, then each output white alone and its grey ramp
/// (its footprint and photometry — identify), then each output's structured-light plan alone, shown through its measured γ and decoded
/// (scan). Grouping outputs whose footprints do not touch into one pass is next round. The same sequence runs against the simulator
/// and against a real display and camera; only the shooter differs.
/// </summary>
public static class AlignSequence
{
    /// <summary>The plan an output's scan shows: phase-shifted sinusoids over Gray-code periods (sub-pixel), predistorted for its γ.</summary>
    public static ScanPlan PlanFor(int width, int height, double gamma) =>
        ScanPlan.Create(width, height, new ScanPlanOptions { Mode = ScanMode.Robust, ProjectorGamma = Math.Clamp(gamma, 1.0, 3.0) });

    /// <summary>How many pictures a group of outputs takes: one black, then per output white, the ramp and its plan.</summary>
    public static int Shots(IReadOnlyList<AlignOutputSpec> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        return 1 + outputs.Sum(o => 1 + Photometry.RampLevels.Count + PlanFor(o.Width, o.Height, 2.2).Count);
    }

    public static async Task<(CameraShot Black, IReadOnlyList<AlignCapture> Captures)> CaptureAsync(IAlignShooter shooter,
        IReadOnlyList<AlignOutputSpec> outputs, Action<AlignStepProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shooter);
        ArgumentNullException.ThrowIfNull(outputs);
        var total = Shots(outputs);
        var done = 0;
        void Step(string step, int output, string words) => progress?.Invoke(new AlignStepProgress(step, output, (double)++done / total, words));

        var black = await shooter.ShootAsync(-1, null, ct).ConfigureAwait(false);
        Step("identify", -1, "every output black");
        var cw = black.Width;
        var ch = black.Height;
        var whites = new byte[outputs.Count][];
        var ramps = new List<(byte Level, byte[] Picture)>[outputs.Count];
        var footprints = new Footprint[outputs.Count];
        for (var j = 0; j < outputs.Count; j++)
        {
            var o = outputs[j];
            whites[j] = Same(await shooter.ShootAsync(j, Solid(o.Width, o.Height, 255), ct).ConfigureAwait(false), cw, ch);
            Step("identify", j, $"{o.Name} white");
            ramps[j] = [];
            foreach (var level in Photometry.RampLevels)
            {
                ramps[j].Add((level, Same(await shooter.ShootAsync(j, Solid(o.Width, o.Height, level), ct).ConfigureAwait(false), cw, ch)));
                Step("identify", j, $"{o.Name} grey {level}");
            }
            footprints[j] = Footprints.Find(black.Gray, whites[j], cw, ch);
        }
        var captures = new AlignCapture[outputs.Count];
        for (var j = 0; j < outputs.Count; j++)
        {
            var o = outputs[j];
            if (footprints[j].Area < 64)
                throw new InvalidOperationException($"{o.Name} lit nothing the camera saw — is it on, its shutter open, and in the camera's view?");
            var photometry = Photometry.Measure(Photometry.Core(footprints[j], footprints), black.Gray, whites[j], ramps[j]);
            var plan = PlanFor(o.Width, o.Height, photometry.Measured ? photometry.Gamma : 2.2);
            var frames = new byte[plan.Count][];
            var pattern = new byte[o.Width * o.Height];
            for (var i = 0; i < plan.Count; i++)
            {
                plan.Render(i, pattern);
                frames[i] = Same(await shooter.ShootAsync(j, (byte[])pattern.Clone(), ct).ConfigureAwait(false), cw, ch);
                Step("scan", j, $"{o.Name}: pattern {i + 1} of {plan.Count}");
            }
            ct.ThrowIfCancellationRequested();
            var map = await Task.Run(() => new StructuredLightDecoder(plan, new DecodeOptions { CameraGamma = Photometry.CameraGamma }).Decode(cw, ch, frames), ct)
                .ConfigureAwait(false);
            captures[j] = new AlignCapture(o.Id, o.Width, o.Height, map, footprints[j], whites[j], photometry, o.Prior);
        }
        return (black, captures);
    }

    /// <summary>A frame of one level at a raster.</summary>
    public static byte[] Solid(int width, int height, byte level)
    {
        var f = new byte[width * height];
        if (level != 0)
            Array.Fill(f, level);
        return f;
    }

    private static byte[] Same(CameraShot shot, int width, int height) =>
        shot.Width == width && shot.Height == height ? shot.Gray : throw new InvalidOperationException("the camera changed its picture size during the run");
}

/// <summary>The simulator as a shooter: each shot is the simulated camera's picture of the frames the outputs show.</summary>
public sealed class SimShooter(MultiProjectorSim sim) : IAlignShooter
{
    private int _shots;

    /// <summary>How many frames each output was shown (the black shot counts for none).</summary>
    public IReadOnlyList<int> Shown => _shown;

    private readonly int[] _shown = new int[sim.Projectors.Count];

    public int Shots => _shots;

    public Task<CameraShot> ShootAsync(int output, byte[]? frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var shown = new byte[]?[sim.Projectors.Count];
        if (output >= 0 && frame is not null)
        {
            shown[output] = frame;
            _shown[output]++;
        }
        _shots++;
        return Task.FromResult(new CameraShot(sim.Capture(shown), sim.CameraWidth, sim.CameraHeight));
    }
}
