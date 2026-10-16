using System.Globalization;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Align;

/// <summary>One camera picture, gray8 at the camera's raster.</summary>
public sealed record CameraShot(byte[] Gray, int Width, int Height);

/// <summary>Shows one output's frame (every other output of the group black) and answers the camera's picture of it; a null frame
/// shows every output black.</summary>
public interface IAlignShooter
{
    Task<CameraShot> ShootAsync(int output, byte[]? frame, CancellationToken ct);

    /// <summary>Shows each output's colour frame at once (interleaved RGB at its raster; null shows black) and answers the camera's
    /// colour picture; null when the display or the camera has no colour.</summary>
    Task<CameraShotRgb?> ShootRgbAsync(IReadOnlyList<byte[]?> frames, CancellationToken ct) => Task.FromResult<CameraShotRgb?>(null);
}

/// <summary>What the colour step found: each output's colour, the white match (null when none was made), whether the camera held its
/// exposure, and the words.</summary>
public sealed record AlignColour(IReadOnlyList<ProjectorColour> Colours, WhiteMatch? Match, bool Stable, string Words, int Shots);

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

    /// <summary>The sequence over some of a group's outputs (ONLY): <paramref name="shooterIndex"/> names each spec's output as the
    /// shooter counts them (null: in order), and <paramref name="kept"/> are the footprints of the outputs not scanned again, so each
    /// scanned output's photometry still leaves their light out.</summary>
    public static async Task<(CameraShot Black, IReadOnlyList<AlignCapture> Captures)> CaptureAsync(IAlignShooter shooter,
        IReadOnlyList<AlignOutputSpec> outputs, Action<AlignStepProgress>? progress, CancellationToken ct, IReadOnlyList<int>? shooterIndex = null,
        IReadOnlyList<Footprint>? kept = null)
    {
        ArgumentNullException.ThrowIfNull(shooter);
        ArgumentNullException.ThrowIfNull(outputs);
        int At(int j) => shooterIndex is null ? j : shooterIndex[j];
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
            whites[j] = Same(await shooter.ShootAsync(At(j), Solid(o.Width, o.Height, 255), ct).ConfigureAwait(false), cw, ch);
            Step("identify", j, $"{o.Name} white");
            ramps[j] = [];
            foreach (var level in Photometry.RampLevels)
            {
                ramps[j].Add((level, Same(await shooter.ShootAsync(At(j), Solid(o.Width, o.Height, level), ct).ConfigureAwait(false), cw, ch)));
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
            var photometry = Photometry.Measure(Photometry.Core(footprints[j], kept is null ? footprints : [.. footprints, .. kept]), black.Gray, whites[j], ramps[j]);
            var plan = PlanFor(o.Width, o.Height, photometry.Measured ? photometry.Gamma : 2.2);
            var frames = new byte[plan.Count][];
            var pattern = new byte[o.Width * o.Height];
            for (var i = 0; i < plan.Count; i++)
            {
                plan.Render(i, pattern);
                frames[i] = Same(await shooter.ShootAsync(At(j), (byte[])pattern.Clone(), ct).ConfigureAwait(false), cw, ch);
                Step("scan", j, $"{o.Name}: pattern {i + 1} of {plan.Count}");
            }
            ct.ThrowIfCancellationRequested();
            var map = await Task.Run(() => new StructuredLightDecoder(plan, new DecodeOptions { CameraGamma = Photometry.CameraGamma }).Decode(cw, ch, frames), ct)
                .ConfigureAwait(false);
            captures[j] = new AlignCapture(o.Id, o.Width, o.Height, map, footprints[j], whites[j], photometry, o.Prior);
        }
        return (black, captures);
    }

    /// <summary>How many colour pictures the colour step takes: black, the stability probe, and each output's red, green, blue and white.</summary>
    public static int ColourShots(int outputs) => 2 + 4 * outputs;

    /// <summary>
    /// WHITE (autoalign §3.5, the diagonal half): every output black, then each output's full red, green, blue and white alone, each
    /// measured over the core of its footprint in camera-linear RGB; first, the camera's stability probe — the first output's white
    /// alone and then with every other output white too (a lone output, or a stack whose outputs share every pixel: on the left of its
    /// raster only): where only it reaches, a camera that holds its exposure reads the same light, one that sets its own does not. A camera that moved its exposure, or that has no colour, matches nothing (the words
    /// say why; the geometry stands).
    /// </summary>
    public static async Task<AlignColour> ColourAsync(IAlignShooter shooter, IReadOnlyList<AlignOutputSpec> outputs, IReadOnlyList<AlignCapture> captures,
        Action<AlignStepProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shooter);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(captures);
        var total = ColourShots(outputs.Count);
        var done = 0;
        void Step(int output, string words)
        {
            done++;
            progress?.Invoke(new AlignStepProgress("color", output, (double)done / total, words));
        }
        var frames = new byte[]?[outputs.Count];
        async Task<CameraShotRgb?> Shoot(int j, byte[]? frame)
        {
            Array.Clear(frames);
            if (j >= 0)
                frames[j] = frame;
            return await shooter.ShootRgbAsync(frames, ct).ConfigureAwait(false);
        }
        if (await Shoot(-1, null).ConfigureAwait(false) is not { } black)
            return new AlignColour([], null, false, "the camera or the display shows no colour — colour was not matched", 0);
        Step(-1, "every output black");
        // Each colour shot lights one output alone, so its own footprint (eroded off its edge) speaks for it; the probe that lights the
        // others too needs the core no other output reaches, and a stack has none: it probes on the left of the first output instead.
        var footprints = captures.Select(c => c.Footprint).ToList();
        var regions = captures.Select(c => Footprints.Erode(c.Footprint.Mask, c.Footprint.Width, c.Footprint.Height, 4)).ToList();
        var core0 = Photometry.Core(captures[0].Footprint, footprints);
        var coreCount = core0.Count(x => x);

        // The stability probe: the first output's white alone, then again with every other output white too (alone in the group: on
        // the left of its raster only). Its core sees none of the others' light, so a camera that holds its exposure reads the same.
        var first = outputs[0];
        var whiteFirst = await Shoot(0, SolidRgb(first.Width, first.Height, 255, 255, 255, 1)).ConfigureAwait(false);
        Step(0, $"{first.Name} white");
        bool[] probe;
        CameraShotRgb? second;
        if (outputs.Count >= 2 && coreCount >= 64)
        {
            Array.Clear(frames);
            for (var j = 0; j < outputs.Count; j++)
                frames[j] = SolidRgb(outputs[j].Width, outputs[j].Height, 255, 255, 255, 1);
            second = await shooter.ShootRgbAsync(frames, ct).ConfigureAwait(false);
            Step(-1, "every output white: is the camera's exposure held?");
            probe = core0;
        }
        else
        {
            second = await Shoot(0, SolidRgb(first.Width, first.Height, 255, 255, 255, 0.5)).ConfigureAwait(false);
            Step(0, $"{first.Name} white on its left half: is the camera's exposure held?");
            var map = captures[0].Map;
            probe = new bool[regions[0].Length];
            for (var i = 0; i < probe.Length; i++)
                probe[i] = regions[0][i] && map.ProjectorX[i] is var x && float.IsFinite(x) && x < 0.4 * map.ProjectorWidth;
        }
        var a = ColourMatch.Luminance(ColourMatch.Mean(whiteFirst!.Rgb, probe) - ColourMatch.Mean(black.Rgb, probe));
        var b = ColourMatch.Luminance(ColourMatch.Mean(second!.Rgb, probe) - ColourMatch.Mean(black.Rgb, probe));
        var drift = a > 0 ? Math.Abs(b - a) / a : double.PositiveInfinity;
        if (!(drift <= 0.03))
        {
            return new AlignColour([], null, false, string.Create(CultureInfo.InvariantCulture,
                $"the camera sets its own exposure (the same white read {drift * 100:0} % apart) — set it to manual exposure and white balance, then ALIGN RUN with COLOR WHITE; the geometry stands"), done);
        }

        var colours = new List<ProjectorColour>(outputs.Count);
        for (var j = 0; j < outputs.Count; j++)
        {
            var o = outputs[j];
            var k = ColourMatch.Mean(black.Rgb, regions[j]);
            var shots = new Vec3d[4];
            (byte R, byte G, byte B, string Name)[] primaries = [(255, 0, 0, "red"), (0, 255, 0, "green"), (0, 0, 255, "blue"), (255, 255, 255, "white")];
            for (var p = 0; p < 4; p++)
            {
                var (r, g, bl, name) = primaries[p];
                var shot = j == 0 && p == 3 ? whiteFirst : await Shoot(j, SolidRgb(o.Width, o.Height, r, g, bl, 1)).ConfigureAwait(false);
                if (!(j == 0 && p == 3))
                    Step(j, $"{o.Name} {name}");
                shots[p] = ColourMatch.Mean(shot!.Rgb, regions[j]);
            }
            colours.Add(new ProjectorColour(k, shots[0], shots[1], shots[2], shots[3]));
        }
        var (match, words) = ColourMatch.White(colours);
        return new AlignColour(colours, match, true, words, done);
    }

    /// <summary>A colour frame of one RGB over the left <paramref name="share"/> of a raster, black beyond.</summary>
    public static byte[] SolidRgb(int width, int height, byte r, byte g, byte b, double share)
    {
        var f = new byte[width * height * 3];
        var lit = (int)Math.Round(width * Math.Clamp(share, 0, 1));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < lit; x++)
            {
                var i = (y * width + x) * 3;
                f[i] = r;
                f[i + 1] = g;
                f[i + 2] = b;
            }
        return f;
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

    public Task<CameraShotRgb?> ShootRgbAsync(IReadOnlyList<byte[]?> frames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ct.ThrowIfCancellationRequested();
        for (var j = 0; j < frames.Count && j < _shown.Length; j++)
            if (frames[j] is not null)
                _shown[j]++;
        _shots++;
        return Task.FromResult<CameraShotRgb?>(new CameraShotRgb(sim.CaptureRgb(frames), sim.CameraWidth, sim.CameraHeight));
    }

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
