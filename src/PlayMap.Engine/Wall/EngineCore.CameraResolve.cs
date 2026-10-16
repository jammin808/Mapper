using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Align;
using PlayMap.Core.Calibration;
using PlayMap.Core.Calibration.Scoring;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Wire;
using PlayMap.Engine.Align;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Wall;

namespace PlayMap.Engine;

/// <summary>
/// CALIBRATION RESOLVE … CAMERA: a fresh look at only the named projectors (one alone: with another as a reference that is never
/// re-solved) through one calibrated camera. Every projector black,
/// then each named projector's jittered dots alone; each spot matched to the dot it is by where the project says that dot lands
/// (its stored pose onto the stage, into the camera); each projector judged unmoved, moved or unseen (and all moved together: the
/// camera moved); each mover's pose solved again from its dots (the camera's rays onto the stage ↔ the dots' pixels, optics kept),
/// then looked at once more to measure its wall error; scored, and applied or staged through the ledger in one edit, with its pins
/// and its pixel quads carried along. A simulated camera is its own display (the stage simulator, with the projectors where
/// <see cref="SimulatedStage"/> says they truly stand); a real camera shows the dots on the projectors' outputs.
/// </summary>
public sealed partial class EngineCore
{
    /// <summary>Where a simulated camera's projectors truly stand (knocked or as the project says).</summary>
    public SimulatedStage SimulatedStage { get; } = new();

    /// <summary>The projectors the last camera look found moved and that still stand where they were found (STATE.calibration.moved).</summary>
    private volatile string[] _calibrationMoved = [];

    private async Task<CommandReply> ResolveFromCameraAsync(VerbCall c, string cameraWord, List<Projector> requested, bool stage)
    {
        var p = Store.Project;
        // One projector alone cannot tell its own move from the camera's: another projector an output shows (or, simulated, any other)
        // is looked at too, as a reference that is never re-solved.
        var named = new List<Projector>(requested);
        var reference = requested.Count == 1
            ? p.Stage.Projectors.FirstOrDefault(x => x.Id != requested[0].Id
                && (p.Outputs.Any(o => o.Enabled && o.Mode == OutputMode.Projector && o.ProjectorId == x.Id) || p.Stage.Cameras.Find(cameraWord)?.SourceKind == CameraSourceKind.Simulated))
            : null;
        if (reference is not null)
            named.Add(reference);
        var rig = p.Stage.Cameras.Find(cameraWord) ?? p.Stage.Cameras.FirstOrDefault(x => x.Name.Equals(cameraWord, StringComparison.OrdinalIgnoreCase));
        if (rig is null)
            return CommandReply.Fail($"no camera {cameraWord} — the camera must be one of the stage's, placed and calibrated");
        if (!rig.Calibrated)
            return CommandReply.Fail($"{rig.Name} is not calibrated — a re-solve measures from where the camera stands (place it with a scan first: SCAN START {rig.Id})");
        var simulated = rig.SourceKind == CameraSourceKind.Simulated;
        var outputs = new List<Output>();
        foreach (var pj in named)
        {
            if (p.Outputs.FirstOrDefault(o => o.Enabled && o.Mode == OutputMode.Projector && o.ProjectorId == pj.Id) is { } o)
                outputs.Add(o);
            else if (!simulated)
                return CommandReply.Fail($"no output drives {pj.Name} — set an output's projector to it first");
            else
                outputs.Add(new Output("", pj.Name, OutputKind.Virtual, "", pj.Intrinsics.Width, pj.Intrinsics.Height, 60, OutputMode.Projector, pj.Id, SourceRef.None,
                    new RectD(0, 0, pj.Intrinsics.Width, pj.Intrinsics.Height), OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(0, 0, 1, 1), 0, true));
        }
        if (!simulated && Services.WallDisplay is null)
            return CommandReply.Fail("this node has no outputs to show the dots on");
        // Every other projector output is held black while the dots are up, so no show content reaches the camera's pictures.
        var shown = new List<Output>(outputs);
        if (!simulated)
            shown.AddRange(p.Outputs.Where(o => o.Enabled && o.Mode == OutputMode.Projector && !outputs.Any(x => x.Id == o.Id)));
        if (Services.Scan.Current is { State: "starting" or "running" })
            return CommandReply.Fail("a scan is running — a re-solve waits for it (SCAN CANCEL)");
        if (AlignBackend.IsRunning)
            return CommandReply.Fail("an alignment run is showing patterns — a re-solve waits for it (ALIGN CANCEL)");
        var world = await Task.Run(() => StageWorld.Of(p, Meshes), c.Ct).ConfigureAwait(false);
        if (world is null)
            return CommandReply.Fail("the stage has no surface the dots could land on — a camera re-solve measures against the stage's surfaces");

        var taskId = Ids.New("kt");
        var task = new CalibrationTask(taskId, "resolve", "capturing", named[0].Id, 0,
            $"looking at {string.Join(", ", named.Select(x => x.Name))} through {rig.Name}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Ct);
        if (BeginCalibrationTask(task, cts) is { } busy)
            return busy;
        var settings = Services.Settings.Wall;
        IScanCamera? camera = null;
        var showing = false;
        var frames = 0;
        var started = Services.Mono.NowNs;
        try
        {
            IAlignShooter shooter;
            if (simulated)
            {
                var truth = named.Select(SimulatedStage.TruthOf).ToList();
                var sim = await Task.Run(() => new StageSimulator(world, SimulatedStage.TruthOf(rig), truth), cts.Token).ConfigureAwait(false);
                shooter = new StageShooter(sim);
            }
            else
            {
                camera = (Services.WallCamera ?? (r => ScanCameras.Open(this, r)))(rig)
                    ?? throw new InvalidOperationException($"{rig.Name} is a {rig.SourceKind.ToString().ToLowerInvariant()} camera, which this node cannot open");
                if (!await camera.OpenAsync(cts.Token).ConfigureAwait(false))
                    throw new InvalidOperationException($"{rig.Name} did not open: {camera.Problem}");
                showing = true;
                shooter = new DisplayShooter(Services.WallDisplay!, camera, shown, new AlignRunnerOptions());
            }
            var cameraView = DeviceView.Of(rig);
            var black = await shooter.ShootAsync(-1, null, cts.Token).ConfigureAwait(false);
            frames++;
            var check = new List<ProjectorDots>();
            var patterns = new List<(ImmutableArray<Vec2d> Raster, byte[] Frame)>();
            for (var j = 0; j < named.Count; j++)
            {
                var (raster, frame) = DotFrame(named[j], outputs[j], settings.ResolveDotSpacing, j);
                patterns.Add((raster, frame));
                var shot = await shooter.ShootAsync(j, frame, cts.Token).ConfigureAwait(false);
                frames++;
                task = task with { Projector = named[j].Id, Progress = 0.5 * (j + 1) / named.Count, Words = $"{named[j].Name}: dots seen" };
                SetTask(task);
                var pj = named[j];
                check.Add(await Task.Run(() => Look(pj, raster, black, shot, cameraView, world), cts.Token).ConfigureAwait(false));
            }
            task = task with { State = "matching", Words = "judging who moved" };
            SetTask(task);
            var motion = MotionAttribution.Attribute(check);
            if (named.Count == 1 && motion[0].Verdict == MotionVerdict.Moved)
                motion[0] = motion[0] with { Words = motion[0].Words + " — with no other projector to compare, a moved camera would read the same" };
            var movers = new List<int>();
            for (var j = 0; j < named.Count; j++)
                if (motion[j].Verdict == MotionVerdict.Moved && requested.Any(x => x.Id == named[j].Id))
                    movers.Add(j);
            if (movers.Count == 0)
            {
                var why = motion.Any(m => m.Verdict == MotionVerdict.Camera) ? motion.First(m => m.Verdict == MotionVerdict.Camera).Words
                    : motion.Any(m => m.Verdict == MotionVerdict.Unseen) ? string.Join("; ", motion.Select(m => m.Words))
                    : "nothing moved: " + string.Join("; ", motion.Select(m => m.Words));
                SetTask(task with { State = "done", Progress = 1, Words = why });
                _calibrationMoved = [];
                FactsChanged();
                return CommandReply.Fail(why);
            }

            task = task with { State = "solving", Words = $"solving {string.Join(", ", movers.Select(j => named[j].Name))} from their dots" };
            SetTask(task);
            var solved = new List<Resolved>();
            foreach (var j in movers)
            {
                var pj = named[j];
                var pairs = MotionAttribution.Pairs(check[j], cameraView, world);
                var labels = Enumerable.Range(1, pairs.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
                // The optics are held, so the lens prior has nothing to weigh; a position prior joins the dots as rows, weighed
                // against the dots' own noise.
                var priors = PosePriors.From(pj.Priors, pj.Intrinsics, pj.Priors.Position, PosePriors.DensePixelSigma).WithoutFocal;
                var solution = await Task.Run(() => PoseSolver.Solve(pairs, pj.Intrinsics, pj.Lens, PoseSolveOptions.Default with
                {
                    Locks = SolveLocks.Focal | SolveLocks.Shift | SolveLocks.Distortion,
                    InitialPose = pj.Pose,
                    Noun = "dot",
                    Labels = labels,
                    OutlierThresholdPx = 3,
                    Priors = priors,
                }), cts.Token).ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();
                var after = pj with { Pose = solution.Pose, Pins = Reseat2D.Pins(pj.Pins, new DeviceView(pj.Intrinsics, pj.Lens, solution.Pose)) };

                // The verification: the dots looked at once more, matched through the new pose, their wall error under it.
                task = task with { State = "verifying", Projector = pj.Id, Words = $"{pj.Name}: checking the new pose on the wall" };
                SetTask(task);
                var again = await shooter.ShootAsync(j, patterns[j].Frame, cts.Token).ConfigureAwait(false);
                frames++;
                var verify = await Task.Run(() => Look(after, patterns[j].Raster, black, again, cameraView, world), cts.Token).ConfigureAwait(false);
                var verifyPairs = MotionAttribution.Pairs(verify, cameraView, world);
                var (p50, p95, _) = MotionAttribution.WallError(verifyPairs, DeviceView.Of(after));
                var (score, uncertainty) = DotScore(pj, rig, pairs, solution);
                var before = DeviceView.Of(pj);
                var newView = DeviceView.Of(after);
                var fallbackDistance = pairs.Count == 0 ? 5 : pairs.Average(x => Vec3d.Distance(x.World, pj.Pose.Position));
                solved.Add(new Resolved(pj, after, score, uncertainty, p50, p95, project => ReseatQuads(project, pj.Id, before, newView, world, fallbackDistance))
                {
                    RunId = CameraRunPrefix + rig.Id,
                    PriorsUsed = PriorsUsed(throwUsed: false, positionUsed: priors.Centre is not null),
                });
            }
            var patternMs = frames * settings.ResolveFrameMs;
            _calibrationMoved = [.. movers.Select(j => named[j].Id)];
            return await ApplyResolvedAsync(c, task, cts, solved, stage, w =>
            {
                w.WriteString("camera", rig.Id);
                WriteMotion(w, named, motion);
                w.WriteNumber("frames", frames);
                w.WriteNumber("patternSeconds", Math.Round(patternMs / 1000.0, 3));
                w.WriteNumber("captureSeconds", Math.Round((Services.Mono.NowNs - started) / 1e9, 3));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetTask(task with { State = "cancelled", Words = ResolveCancelled });
            return CommandReply.Fail(ResolveCancelled);
        }
        catch (InvalidOperationException ex)
        {
            SetTask(task with { State = "failed", Words = ex.Message });
            return CommandReply.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetTask(task with { State = "failed", Words = $"the re-solve faulted ({ex.GetType().Name}) — nothing was applied" });
            throw;
        }
        finally
        {
            if (showing)
                Services.WallDisplay?.Release();
            camera?.Dispose();
            EndCalibrationTask(cts);
        }
    }

    /// <summary>A projector's dots at its output's raster and the frame that shows them; the dots are answered in the projector's own
    /// (intrinsic) raster, where its pose lives.</summary>
    private static (ImmutableArray<Vec2d> Raster, byte[] Frame) DotFrame(Projector pj, Output o, double spacing, int index)
    {
        var shown = DotCheck.Pattern(o.Width, o.Height, spacing, 0x5EED + (ulong)index);
        var frame = DotCheck.Render(shown.AsSpan(), o.Width, o.Height, spacing / 16);
        var sx = (double)pj.Intrinsics.Width / o.Width;
        var sy = (double)pj.Intrinsics.Height / o.Height;
        return ([.. shown.Select(d => new Vec2d(d.X * sx, d.Y * sy))], frame);
    }

    /// <summary>One projector's dots looked for in the camera's picture of them: predicted through the projector's pose as given, the
    /// spots detected against the black picture, and matched.</summary>
    private static ProjectorDots Look(Projector pj, ImmutableArray<Vec2d> raster, CameraShot black, CameraShot shot, DeviceView camera, MeshRaycaster world)
    {
        var view = DeviceView.Of(pj);
        var predicted = raster.Select(d => MotionAttribution.Predict(d, view, camera, world)).ToImmutableArray();
        var blobs = DotCheck.Detect(shot.Gray, black.Gray, shot.Width, shot.Height);
        var cameraSpacing = DotCheck.CameraSpacing(predicted);
        var reach = Math.Max(4 * cameraSpacing, 8);
        var match = DotCheck.Match(predicted, blobs, reach);
        return new ProjectorDots(pj.Id, pj.Name, raster, predicted, [.. blobs], match);
    }

    /// <summary>A camera re-solve's score: the dot fit scored as a pin solve, each dot a pin; a fix that would name one dot is the
    /// re-solve again instead.</summary>
    private static (QualityScore Score, SolveUncertainty? Uncertainty) DotScore(Projector pj, CameraRig rig, List<Correspondence> pairs, PoseSolution solution)
    {
        var dots = pairs.Select((x, i) => new CalibrationPin("dot" + (i + 1).ToString(CultureInfo.InvariantCulture), x.World, x.Pixel, true)).ToImmutableArray();
        var asPins = pj with { Pose = solution.Pose, Pins = dots, Locks = SolveLocks.Focal | SolveLocks.Shift | SolveLocks.Distortion };
        var (pinScore, uncertainty) = CalibrationScoring.ForPins(asPins, solution, "resolve", PosePriors.DensePixelSigma);
        var score = pinScore with { Words = pinScore.Words.Replace(" pins", " dots", StringComparison.Ordinal) };
        var again = $"CALIBRATION RESOLVE {pj.Id} CAMERA {rig.Id}";
        return (score with
        {
            Terms = [.. score.Terms.Select(t => t.Fix.Contains("dot", StringComparison.Ordinal) || t.Fix.StartsWith("SET ", StringComparison.Ordinal)
                ? t with { Fix = again, Where = t.Where.Contains("dot", StringComparison.Ordinal) ? "" : t.Where }
                : t)],
        }, uncertainty);
    }

    /// <summary>Every pixel quad of a projector re-seated from its old pose to its new one (inside the apply edit).</summary>
    private static Project ReseatQuads(Project project, string projectorId, DeviceView before, DeviceView after, MeshRaycaster world, double fallbackDistance)
    {
        var surfaces = project.Stage.Surfaces;
        var changed = false;
        for (var i = 0; i < surfaces.Length; i++)
        {
            if (surfaces[i].Pixel is not { } q || !string.Equals(q.ProjectorId, projectorId, StringComparison.Ordinal))
                continue;
            if (Reseat2D.Quad(q, before, after, world, fallbackDistance) is { } moved)
            {
                surfaces = surfaces.SetItem(i, surfaces[i] with { Pixel = moved.Quad });
                changed = true;
            }
        }
        return changed ? project.WithStage(st => st with { Surfaces = surfaces }) : project;
    }

    private static void WriteMotion(Utf8JsonWriter w, List<Projector> named, List<ProjectorMotion> motion)
    {
        w.WriteStartArray("attribution");
        for (var j = 0; j < motion.Count; j++)
        {
            var m = motion[j];
            w.WriteStartObject();
            w.WriteString("id", m.ProjectorId);
            w.WriteString("name", named[j].Name);
            w.WriteString("verdict", m.Verdict.ToString().ToLowerInvariant());
            w.WriteNumber("dots", m.Expected);
            w.WriteNumber("matched", m.Matched);
            w.WriteNumber("shiftP50", Math.Round(m.ShiftP50, 3));
            w.WriteNumber("shiftP95", Math.Round(m.ShiftP95, 3));
            w.WriteString("words", m.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
