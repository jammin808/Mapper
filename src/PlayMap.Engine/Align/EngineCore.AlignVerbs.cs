using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;
using PlayMap.Engine.Align;
using PlayMap.Engine.Wall;

namespace PlayMap.Engine;

/// <summary>
/// The camera alignment of flat screens: ALIGN NEW (outputs aligned together through one camera onto one canvas), RUN (measure and
/// stage, off the lane), STATUS, CANCEL, ACCEPT (every output's measured correction applied through the calibration ledger as one
/// edit, the correction it had before kept as its fallback), REVERT (back to the fallbacks), CORNERS (the screen's corners for an exact
/// canvas) and CLEAR; STATE.align, outputs[].measured and the align context items. The measured files are read off the lane and off the
/// render thread (<see cref="MeasuredLibrary"/>), and the render host draws what they hold.
/// </summary>
public sealed partial class EngineCore
{
    private readonly MeasuredLibrary _measured = new();

    /// <summary>The runner ALIGN RUN starts: the services' (the host's, with the render host as its display); a node composed without
    /// the align part refuses a run with words.</summary>
    internal IAlignBackend AlignBackend => Services.Align ?? NoAlignBackend.Instance;

    /// <summary>The outputs' measured corrections as their files read (the render host uploads them on its thread).</summary>
    public MeasuredSet AlignMeasured => _measured.Current;

    internal MeasuredLibrary AlignFiles => _measured;

    private void RouteAlignVerbs(VerbRoutes routes)
    {
        // A run's progress and a finished read of the measured files are facts: STATE's revision moves with each.
        AlignBackend.Progressed += _ => FactsChanged();
        _measured.Published += FactsChanged;
        Store.Changed += _ =>
        {
            _measured.Refresh(Store.Project, ProjectFolderForFiles);
            // A project opened or recovered replaces the store before its path is set: its files are looked for again, beside it,
            // once the lane's work in hand is done.
            Lane.Post(() => _measured.Refresh(Store.Project, ProjectFolderForFiles));
        };
        _measured.Refresh(Store.Project, ProjectFolderForFiles);
        routes.Lane("ALIGN NEW", AlignGuarded(AlignNew));
        routes.Lane("ALIGN RUN", AlignGuarded(AlignRun));
        routes.Lane("ALIGN STATUS", _ => CommandReply.Success(JsonOut(w => WriteAlignRun(w, AlignBackend.View))));
        routes.Lane("ALIGN CANCEL", AlignCancel);
        routes.OffLane("ALIGN ACCEPT", c => AlignGate(c) is { } refused ? Task.FromResult(refused) : AlignAcceptAsync(c));
        routes.Lane("ALIGN REVERT", AlignGuarded(AlignRevert));
        routes.Lane("ALIGN CORNERS", AlignGuarded(AlignCorners));
        routes.Lane("ALIGN CLEAR", AlignGuarded(AlignClear));
        routes.Index(new AlignIndex(this));
    }

    /// <summary>The show's gate asked again for an align verb (a gate standing in for the show's is obeyed too).</summary>
    private CommandReply? AlignGate(VerbCall c)
    {
        var gate = Services.ShowGate;
        if (!gate.Armed)
            return null;
        var d = gate.Decide(c.Spec, c.Line, c.Session);
        return d.Verdict == Core.Show.ShowVerdict.Allow ? null
            : CommandReply.Fail(d.Words ?? "the show is armed — mapping is locked (SHOW DISARM, or blind-edit)");
    }

    private Func<VerbCall, CommandReply> AlignGuarded(Func<VerbCall, CommandReply> handler) => c => AlignGate(c) ?? handler(c);

    private static AlignGroup? AlignNamed(Project p, string word) =>
        p.FindAlign(word) ?? p.Stage.Aligns.FirstOrDefault(g => g.Name.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static Output? AlignOutputNamed(Project p, string word) =>
        p.FindOutput(word) ?? p.Outputs.FirstOrDefault(o => o.Name.Equals(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value after a keyword (removed from the words with it), or null when the keyword is absent; "" when it ends them.</summary>
    private static string? TakeOption(List<string> args, string keyword)
    {
        var i = args.FindIndex(a => a.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            return null;
        var value = i + 1 < args.Count ? args[i + 1] : "";
        args.RemoveRange(i, Math.Min(2, args.Count - i));
        return value;
    }

    // ---- ALIGN NEW ---------------------------------------------------------------------------------------------------------

    private CommandReply AlignNew(VerbCall c)
    {
        const string syntax = "ALIGN NEW <name> <camera> <output>…|ALL [CANVAS <canvas>|NEW] [SHAPE AUTO|FLAT]";
        var args = c.Args.ToList();
        var canvasWord = TakeOption(args, "CANVAS");
        var shapeWord = TakeOption(args, "SHAPE");
        if (args.Count < 3 || canvasWord == "" || shapeWord == "")
            return CommandReply.Fail(syntax);
        var shape = ScreenShape.Auto;
        if (shapeWord is not null)
        {
            if (shapeWord.Equals("FLAT", StringComparison.OrdinalIgnoreCase))
                shape = ScreenShape.Flat;
            else if (!shapeWord.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
                return CommandReply.Fail($"SHAPE {shapeWord.ToUpperInvariant()}: curved screens are next round (cylinder, then dome) — SHAPE AUTO or FLAT");
        }
        var p = Edits.Project;
        var name = args[0];
        var cameraWord = args[1];
        string cameraId;
        if (cameraWord.Equals("sim", StringComparison.OrdinalIgnoreCase))
            cameraId = "sim";
        else if ((p.Stage.Cameras.Find(cameraWord) ?? p.Stage.Cameras.FirstOrDefault(r => r.Name.Equals(cameraWord, StringComparison.OrdinalIgnoreCase))) is { } rig)
            cameraId = rig.Id;
        else
            return CommandReply.Fail($"no camera {cameraWord} — CAMERA ADD makes one, or \"sim\" for the simulator");
        var outputs = new List<Output>();
        if (args.Count == 3 && args[2].Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            outputs.AddRange(p.Outputs.Where(o => o.Enabled && o.Mode != OutputMode.Projector));
            if (outputs.Count == 0)
                return CommandReply.Fail("no enabled output shows a canvas or a source to align");
        }
        else
        {
            foreach (var word in args.Skip(2))
            {
                if (AlignOutputNamed(p, word) is not { } o)
                    return CommandReply.Fail($"no output {word}");
                if (outputs.Any(x => x.Id == o.Id))
                    return CommandReply.Fail($"{o.Name} is named twice");
                outputs.Add(o);
            }
        }
        if (outputs.FirstOrDefault(o => o.Mode == OutputMode.Projector) is { } projector)
            return CommandReply.Fail($"{projector.Name} renders a projector's 3D picture — SET {projector.Id} mode \"Canvas\" to align it flat (free-form alignment is next round)");
        var ids = outputs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (p.Stage.Aligns.FirstOrDefault(g => g.CameraId == cameraId && g.OutputIds.Length == ids.Count && g.OutputIds.All(ids.Contains)) is { } same)
            return CommandReply.Fail($"{same.Name} ({same.Id}) aligns these outputs through {cameraId} already");

        Canvas? canvas = null;
        var makeCanvas = canvasWord is not null && canvasWord.Equals("NEW", StringComparison.OrdinalIgnoreCase);
        if (canvasWord is not null && !makeCanvas)
        {
            canvas = p.FindCanvas(canvasWord) ?? p.Canvases.FirstOrDefault(x => x.Name.Equals(canvasWord, StringComparison.OrdinalIgnoreCase));
            if (canvas is null)
                return CommandReply.Fail($"no canvas {canvasWord}");
        }
        if (canvas is null && !makeCanvas)
        {
            canvas = outputs.Where(o => o.Mode == OutputMode.Canvas).Select(o => p.FindCanvas(o.Source.Id)).FirstOrDefault(x => x is not null);
            makeCanvas = canvas is null;
        }
        var groupId = "";
        var canvasId = "";
        var moved = new List<string>();
        var r = Edits.Apply($"align {name}", project =>
        {
            moved.Clear();
            groupId = project.NewId(Ids.Align);
            var target = canvas;
            if (makeCanvas)
            {
                var width = Math.Clamp(outputs.Sum(o => o.Width), 16, 16384);
                var height = Math.Clamp(outputs.Max(o => o.Height), 16, 16384);
                target = new Canvas(project.NewId(Ids.Canvas), name, width, height, "#000000", []);
                project = project with { Canvases = project.Canvases.Add(target) };
            }
            canvasId = target!.Id;
            var showing = outputs.Where(o => o.Mode == OutputMode.Canvas && o.Source.Id == canvasId).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            var others = outputs.Where(o => !showing.Contains(o.Id)).ToList();
            for (var i = 0; i < others.Count; i++)
            {
                var share = target.Width / (double)others.Count;
                var rect = new RectD(i * share, 0, share, target.Height);
                project = project with
                {
                    Outputs = project.Outputs.Update(others[i].Id, o => o with { Mode = OutputMode.Canvas, Source = SourceRef.Canvas(canvasId), SourceRect = rect }),
                };
                moved.Add(others[i].Name);
            }
            var group = new AlignGroup(groupId, name, cameraId, [.. outputs.Select(o => o.Id)], canvasId, shape, null, AlignTarget.Default, AlignOptions.Default, "");
            return project.WithStage(st => st with { Aligns = st.Aligns.Add(group) });
        });
        if (!r.Changed)
            return FromEdit(r);
        var words = $"{name} ({groupId}) aligns {string.Join(", ", outputs.Select(o => o.Name))} through {cameraId} onto {canvasId}"
            + (moved.Count > 0 ? $"; {string.Join(", ", moved)} now show{(moved.Count == 1 ? "s" : "")} {canvasId}" : "")
            + $" — ALIGN RUN {groupId} measures them";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", groupId);
            w.WriteStartArray("outputs");
            foreach (var o in outputs)
                w.WriteStringValue(o.Id);
            w.WriteEndArray();
            w.WriteString("canvas", canvasId);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    // ---- ALIGN RUN, CANCEL ---------------------------------------------------------------------------------------------------

    private CommandReply AlignRun(VerbCall c)
    {
        const string syntax = "ALIGN RUN <group> [ONLY <output>…] [STEPS SCAN,SOLVE,COLOR|ALL] [FIT INSIDE|CORNERS] [COLOR OFF|WHITE]";
        var args = c.Args.ToList();
        var steps = TakeOption(args, "STEPS");
        var fitWord = TakeOption(args, "FIT");
        var colourWord = TakeOption(args, "COLOR");
        var onlyAt = args.FindIndex(a => a.Equals("ONLY", StringComparison.OrdinalIgnoreCase));
        List<string>? onlyWords = null;
        if (onlyAt >= 0)
        {
            onlyWords = args.Skip(onlyAt + 1).ToList();
            args.RemoveRange(onlyAt, args.Count - onlyAt);
            if (onlyWords.Count == 0)
                return CommandReply.Fail("ONLY takes the outputs to scan again — " + syntax);
        }
        if (args.Count != 1 || steps == "" || fitWord == "" || colourWord == "")
            return CommandReply.Fail(syntax);
        var colour = false;
        if (steps is not null)
        {
            var set = steps.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                ? ["SCAN", "SOLVE", "COLOR"]
                : steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
            if (!set.Contains("SCAN") || !set.Contains("SOLVE") || set.Any(x => x is not ("SCAN" or "SOLVE" or "COLOR")))
                return CommandReply.Fail($"STEPS {steps.ToUpperInvariant()}: a run scans and solves (STEPS SCAN,SOLVE), and may match colour (,COLOR) — or ALL; a step on its own and the projectors' own settings are next round");
            colour = set.Contains("COLOR");
        }
        if (colourWord is not null)
        {
            if (colourWord.Equals("WHITE", StringComparison.OrdinalIgnoreCase))
                colour = true;
            else if (colourWord.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                colour = false;
            else
                return CommandReply.Fail($"COLOR {colourWord.ToUpperInvariant()}: WHITE or OFF (FULL is next round)");
        }
        var p = Edits.Project;
        if (AlignNamed(p, args[0]) is not { } group)
            return CommandReply.Fail($"no alignment group {args[0]} — ALIGN NEW makes one");
        var fit = group.Target.Fit;
        if (fitWord is not null)
        {
            if (fitWord.Equals("INSIDE", StringComparison.OrdinalIgnoreCase))
                fit = CanvasFit.Inside;
            else if (fitWord.Equals("CORNERS", StringComparison.OrdinalIgnoreCase))
                fit = CanvasFit.Corners;
            else
                return CommandReply.Fail($"FIT {fitWord.ToUpperInvariant()}: INSIDE or CORNERS");
        }
        if (fit == CanvasFit.Corners && group.Target.CameraCorners.Length != 4)
            return CommandReply.Fail($"FIT CORNERS needs the screen's corners — ALIGN CORNERS {group.Id} <x1> <y1> … <x4> <y4> first");
        var runner = AlignBackend;
        if (runner.IsRunning)
            return CommandReply.Fail($"{runner.View!.Id} is running — ALIGN CANCEL first");
        if (Services.Scan.Current is { State: "starting" or "running" })
            return CommandReply.Fail("a scan is running — one camera and one display at a time");
        if (ProjectFolderForFiles is not { } folder)
            return CommandReply.Fail("the project is not saved yet — an alignment keeps its files beside it: PROJECT SAVE <path> first");
        CameraRig? rig = null;
        if (group.CameraId != "sim" && (rig = p.Stage.Cameras.Find(group.CameraId)) is null)
            return CommandReply.Fail($"{group.Name}'s camera {group.CameraId} is gone — SET {group.Id} cameraId \"<camera>\"");
        var outputs = new List<Output>();
        foreach (var id in group.OutputIds)
        {
            if (p.FindOutput(id) is not { } o)
                return CommandReply.Fail($"{group.Name}'s output {id} is gone");
            if (!o.Enabled)
                return CommandReply.Fail($"{o.Name} is off — OUTPUT ENABLE {o.Id} first");
            if (o.Mode == OutputMode.Projector)
                return CommandReply.Fail($"{o.Name} renders a projector's 3D picture — free-form alignment is next round");
            outputs.Add(o);
        }
        if (outputs.Count == 0)
            return CommandReply.Fail($"{group.Name} has no output");
        var only = ImmutableArray<int>.Empty;
        var reference = "";
        if (onlyWords is not null)
        {
            if (colour)
                return CommandReply.Fail("colour matches the whole group — ALIGN RUN " + group.Id + " COLOR WHITE without ONLY");
            var picked = new List<int>();
            foreach (var word in onlyWords)
            {
                var j = outputs.FindIndex(o => o.Id.Equals(word, StringComparison.OrdinalIgnoreCase) || o.Name.Equals(word, StringComparison.OrdinalIgnoreCase));
                if (j < 0)
                    return CommandReply.Fail($"{word} is not one of {group.Name}'s outputs");
                if (!picked.Contains(j))
                    picked.Add(j);
            }
            var runs = outputs.Select(o => o.Measured?.RunId ?? "").Distinct(StringComparer.Ordinal).ToList();
            if (runs.Count != 1 || runs[0].Length == 0)
                return CommandReply.Fail($"ONLY keeps the others' measurements from the run they came from — {group.Name}'s outputs do not all stand on one accepted run (ALIGN RUN {group.Id} first)");
            if (picked.Count == outputs.Count)
                return CommandReply.Fail($"ONLY names every output of {group.Name} — ALIGN RUN {group.Id} scans them all");
            only = [.. picked.Order()];
            reference = runs[0];
        }
        var aspect = group.Target.Aspect > 0 ? group.Target.Aspect
            : p.FindCanvas(group.CanvasId) is { Width: > 0, Height: > 0 } cv ? (double)cv.Width / cv.Height
            : 16.0 / 9;
        string runId;
        do
            runId = Ids.New("ar");
        while (Directory.Exists(Path.Combine(folder, AlignArchive.Folder, runId)));
        var projectors = outputs.Select(o => o.ProjectorId is { } pid ? p.FindProjector(pid) : null).ToList();
        try
        {
            runner.Start(new AlignRunSetup(runId, group, outputs, projectors, rig, fit, aspect, folder) { Colour = colour, Only = only, ReferenceRunId = reference });
        }
        catch (InvalidOperationException ex)
        {
            return CommandReply.Fail(ex.Message);
        }
        var shots = Core.Align.AlignSequence.Shots([.. outputs.Where((_, j) => only.IsEmpty || only.Contains(j)).Select(o => new Core.Align.AlignOutputSpec(o.Id, o.Name,
            o.Width, o.Height, Core.Align.ProjectorPrior.Unknown(o.Width, o.Height)))]) + (colour ? Core.Align.AlignSequence.ColourShots(outputs.Count) : 0);
        var words = string.Create(CultureInfo.InvariantCulture,
            $"{runId} measures {group.Name} through {group.CameraId} ({shots} pictures, FIT {fit.ToString().ToUpperInvariant()}); nothing changes on the air until ALIGN ACCEPT {runId}");
        return new CommandReply(true, JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "accepted");
            w.WriteString("run", runId);
            w.WriteString("group", group.Id);
            w.WriteNumber("pictures", shots);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words, Applied: false);
    }

    private CommandReply AlignCancel(VerbCall c)
    {
        if (c.Args.Count != 0)
            return CommandReply.Fail("ALIGN CANCEL");
        var runner = AlignBackend;
        if (runner.View is not { IsRunning: true } v)
            return CommandReply.Fail("no alignment is running");
        return runner.Cancel() ? CommandReply.Success(effect: $"cancelling {v.Id}") : CommandReply.Fail($"{v.Id} could not be cancelled");
    }

    // ---- ALIGN ACCEPT ----------------------------------------------------------------------------------------------------------

    private async Task<CommandReply> AlignAcceptAsync(VerbCall c)
    {
        if (c.Args.Count != 1)
            return CommandReply.Fail("ALIGN ACCEPT <run>|<group>");
        var word = c.Args[0];
        var runner = AlignBackend;
        string runId;
        if (AlignArchive.IsRunId(word))
        {
            runId = word;
        }
        else if (AlignNamed(Store.Project, word) is { } g)
        {
            if (runner.View is { State: "staged" } v && v.GroupId == g.Id)
                runId = v.Id;
            else
                return CommandReply.Fail($"{g.Name} has no staged run — ALIGN RUN {g.Id} first");
        }
        else
        {
            return CommandReply.Fail($"no run or alignment group {word}");
        }
        var folder = ProjectFolderForFiles;
        var record = runner.Finished(runId) ?? await Task.Run(() => AlignArchive.Load(folder, runId), c.Ct).ConfigureAwait(false);
        if (record is null)
            return CommandReply.Fail($"no run {runId} — ALIGN STATUS names the last one");
        if (record.State is "failed" or "cancelled")
            return CommandReply.Fail($"{runId} {record.State}: {record.Words}");
        var missing = await Task.Run(() => MissingFiles(folder, record), c.Ct).ConfigureAwait(false);
        if (missing.Count > 0)
            return CommandReply.Fail($"{string.Join(", ", missing)} missing or changed — ALIGN RUN {record.GroupId} again");
        var applied = new List<(string Output, string Snapshot, string Fallback)>();
        var who = c.Session.ClientName;
        var r = await EditAsync(c.Session, $"accept alignment {runId}", p =>
        {
            applied.Clear();
            return AcceptRun(p, record, who, Services.Wall.UnixMs, applied);
        }).ConfigureAwait(false);
        if (!r.Changed)
            return FromEdit(r);
        runner.MarkAccepted(runId);
        RaiseEvent("align.accepted", LeaderNowNs, w =>
        {
            w.WriteString("group", record.GroupId);
            w.WriteString("run", runId);
        });
        var p2 = Store.Project;
        var words = string.Create(CultureInfo.InvariantCulture, $"{runId} applied ({record.Score.Grade} {record.Score.Value:0}): ")
            + string.Join("; ", applied.Select(a => $"{p2.FindOutput(a.Output)?.Name ?? a.Output} falls back to {a.Fallback}"));
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("group", record.GroupId);
            w.WriteString("run", runId);
            w.WriteStartArray("applied");
            foreach (var (output, snapshot, fallback) in applied)
            {
                w.WriteStartObject();
                w.WriteString("output", output);
                w.WriteString("snapshot", snapshot);
                w.WriteString("fallback", fallback);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("score");
            AlignArchive.WriteScore(w, record.Score);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>A run's files that are missing or no longer what it wrote.</summary>
    private static List<string> MissingFiles(string? folder, AlignRunRecord record)
    {
        var missing = new List<string>();
        foreach (var o in record.Outputs)
        {
            foreach (var (file, hash) in new[] { (o.Measured.Warp?.File, o.Measured.Warp?.Hash), (o.Measured.Blend?.File, o.Measured.Blend?.Hash) })
            {
                if (file is null)
                    continue;
                var path = AlignArchive.Resolve(folder, file);
                if (path is null || !File.Exists(path) || !string.Equals(Core.Align.Pfm.Hash(File.ReadAllBytes(path)), hash, StringComparison.Ordinal))
                    missing.Add(file);
            }
        }
        return missing;
    }

    /// <summary>ACCEPT's edit: for every output of the run, the correction it has now kept as a result (recorded as it stands when it
    /// is not one already), then the run's correction recorded and applied, the kept one its fallback, its blend γ the measured one;
    /// the group's last run the run. Refused when every output stands as the run has it already.</summary>
    private Project AcceptRun(Project p, AlignRunRecord record, string who, long now, List<(string Output, string Snapshot, string Fallback)> applied)
    {
        var group = p.FindAlign(record.GroupId) ?? throw new EditRefusedException($"the alignment group {record.GroupId} is gone");
        foreach (var ro in record.Outputs)
        {
            var o = p.FindOutput(ro.OutputId) ?? throw new EditRefusedException($"{ro.Name} ({ro.OutputId}) is gone");
            if (Equals(o.Measured, ro.Measured))
                continue;
            (p, var previous) = KeepAsItStands(p, o, who, now);
            var id = p.NewId(Ids.Calibration);
            var snap = new CalibrationSnapshot(id, CalibrationLedger.NameFor(p, o.Id, CalibrationSource.Align), CalibrationTarget.Output, o.Id, now,
                CalibrationSource.Align, null, previous, record.Score, who, false)
            {
                Output = ro.Measured,
            };
            (p, _) = CalibrationLedger.Record(p, snap, apply: true);
            var gamma = Math.Clamp(ro.Gamma, 1.0, BlendMath.MaxGamma);
            p = p with
            {
                Outputs = p.Outputs.Update(o.Id, x => x with
                {
                    FallbackId = previous,
                    Blend = double.IsFinite(gamma) ? x.Blend with { Gamma = Math.Round(gamma, 3) } : x.Blend,
                }),
            };
            applied.Add((o.Id, id, previous));
        }
        if (applied.Count == 0)
            throw new EditRefusedException($"every output of {group.Name} stands as {record.Id} has it already");
        return p.WithStage(st => st with { Aligns = st.Aligns.Update(group.Id, g => g with { LastRunId = record.Id }) });
    }

    /// <summary>A result recorded by hand rather than measured: no score, made by "manual" (the ledger's word for it).</summary>
    private static readonly QualityScore AsItStandsScore = QualityScore.None with { Source = "manual" };

    /// <summary>The output's current result when it stands as that result; otherwise the output as it stands recorded (a kept
    /// result, applied) — so whatever ACCEPT, CLEAR or REVERT replaces can be returned to.</summary>
    private static (Project Project, string Id) KeepAsItStands(Project p, Output o, string who, long now)
    {
        if (o.CalibrationId is { } cid && p.FindCalibration(cid) is { } cur && Equals(cur.Output, o.Measured))
            return (p, cid);
        var id = p.NewId(Ids.Calibration);
        var snap = new CalibrationSnapshot(id, o.Measured is null ? "No measured correction" : "As it stood", CalibrationTarget.Output, o.Id, now,
            CalibrationSource.Manual, null, o.CalibrationId, AsItStandsScore, who, false)
        {
            Output = o.Measured,
        };
        return (CalibrationLedger.Record(p, snap, apply: true).Project, id);
    }

    // ---- ALIGN REVERT, CLEAR, CORNERS -------------------------------------------------------------------------------------------

    private CommandReply AlignRevert(VerbCall c)
    {
        if (c.Args.Count != 1)
            return CommandReply.Fail("ALIGN REVERT <group>");
        var p = Edits.Project;
        if (AlignNamed(p, c.Args[0]) is not { } group)
            return CommandReply.Fail($"no alignment group {c.Args[0]}");
        var back = new List<CalibrationSnapshot>();
        var notes = new List<string>();
        foreach (var id in group.OutputIds)
        {
            if (p.FindOutput(id) is not { } o)
                continue;
            if (o.FallbackId is not { } fb || p.FindCalibration(fb) is not { } s)
                notes.Add($"{o.Name} has no fallback");
            else if (Equals(o.Measured, s.Output))
                notes.Add($"{o.Name} stands on its fallback already");
            else
                back.Add(s);
        }
        if (back.Count == 0)
            return CommandReply.Fail(notes.Count > 0 ? string.Join("; ", notes) : $"{group.Name} has no output");
        var recorded = new List<string>();
        var who = c.Session.ClientName;
        var r = Edits.Apply($"revert alignment {group.Name}", project =>
        {
            recorded.Clear();
            foreach (var s in back)
            {
                (project, var id) = CalibrationLedger.Recall(project, s, project.NewId(Ids.Calibration), who, Services.Wall.UnixMs);
                if (s.Output?.Color is { Gamma: > 0 } colour)
                    project = project with { Outputs = project.Outputs.Update(s.TargetId, o => o with { Blend = o.Blend with { Gamma = colour.Gamma } }) };
                recorded.Add(id);
            }
            return project;
        });
        if (!r.Changed)
            return FromEdit(r);
        var words = $"{group.Name} is back to its fallbacks: " + string.Join(", ", back.Select(s => $"{p.FindOutput(s.TargetId)?.Name} ({s.Id})"))
            + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("group", group.Id);
            w.WriteStartArray("applied");
            for (var i = 0; i < back.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("output", back[i].TargetId);
                w.WriteString("fallback", back[i].Id);
                w.WriteString("recorded", recorded[i]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    private CommandReply AlignClear(VerbCall c)
    {
        if (c.Args.Count != 1)
            return CommandReply.Fail("ALIGN CLEAR <output>|<group>");
        var p = Edits.Project;
        List<Output> targets;
        if (AlignNamed(p, c.Args[0]) is { } group)
            targets = [.. group.OutputIds.Select(p.FindOutput).OfType<Output>()];
        else if (AlignOutputNamed(p, c.Args[0]) is { } output)
            targets = [output];
        else
            return CommandReply.Fail($"no output or alignment group {c.Args[0]}");
        targets = [.. targets.Where(o => o.Measured is not null)];
        if (targets.Count == 0)
            return CommandReply.Fail($"{c.Args[0]} has no measured correction to clear");
        var who = c.Session.ClientName;
        var r = Edits.Apply($"clear the measured correction of {string.Join(", ", targets.Select(o => o.Name))}", project =>
        {
            foreach (var t in targets)
            {
                var o = project.FindOutput(t.Id)!;
                (project, var previous) = KeepAsItStands(project, o, who, Services.Wall.UnixMs);
                var snap = new CalibrationSnapshot(project.NewId(Ids.Calibration), "Cleared", CalibrationTarget.Output, o.Id, Services.Wall.UnixMs,
                    CalibrationSource.Manual, null, previous, AsItStandsScore, who, false);
                (project, _) = CalibrationLedger.Record(project, snap, apply: true);
                project = project with { Outputs = project.Outputs.Update(o.Id, x => x with { FallbackId = previous }) };
            }
            return project;
        });
        if (!r.Changed)
            return FromEdit(r);
        return CommandReply.Success(effect: $"cleared the measured correction of {string.Join(", ", targets.Select(o => o.Name))}; each falls back to what it had");
    }

    private CommandReply AlignCorners(VerbCall c)
    {
        const string syntax = "ALIGN CORNERS <group> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> | ALIGN CORNERS <group> CLEAR";
        var p = Edits.Project;
        if (c.Args.Count is not (2 or 9))
            return CommandReply.Fail(syntax);
        if (AlignNamed(p, c.Args[0]) is not { } group)
            return CommandReply.Fail($"no alignment group {c.Args[0]}");
        ImmutableArray<Vec2d> corners;
        if (c.Args.Count == 2)
        {
            if (!c.Args[1].Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
                return CommandReply.Fail(syntax);
            if (group.Target.CameraCorners.IsDefaultOrEmpty)
                return CommandReply.Fail($"{group.Name} has no corners to clear");
            corners = [];
        }
        else
        {
            var v = new double[8];
            for (var i = 0; i < 8; i++)
            {
                if (!double.TryParse(c.Args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || v[i] is < 0 or > 1)
                    return CommandReply.Fail($"{c.Args[i + 1]}: a corner is 0–1 of the camera picture");
            }
            corners = [new(v[0], v[1]), new(v[2], v[3]), new(v[4], v[5]), new(v[6], v[7])];
            for (var i = 0; i < 4; i++)
            {
                var a = corners[(i + 1) % 4] - corners[i];
                var b = corners[(i + 2) % 4] - corners[(i + 1) % 4];
                if (Vec2d.Cross(a, b) <= 0)
                    return CommandReply.Fail("the corners are not a quad top-left, top-right, bottom-right, bottom-left");
            }
            if (!group.Target.CameraCorners.IsDefaultOrEmpty && group.Target.CameraCorners.SequenceEqual(corners))
                return CommandReply.Fail($"{group.Name} has these corners already");
        }
        var target = group.Target with { CameraCorners = corners, Fit = corners.IsEmpty ? CanvasFit.Inside : CanvasFit.Corners };
        var r = Edits.Apply($"corners of {group.Name}", project =>
            project.WithStage(st => st with { Aligns = st.Aligns.Update(group.Id, g => g with { Target = target }) }));
        if (!r.Changed)
            return FromEdit(r);
        var words = corners.IsEmpty ? $"{group.Name}'s canvas is the largest the outputs cover again (FIT INSIDE)"
            : $"{group.Name}'s canvas is the screen's corners (FIT CORNERS) from the next ALIGN RUN {group.Id}";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("group", group.Id);
            w.WriteString("fit", target.Fit.ToString().ToLowerInvariant());
            w.WriteStartArray("cameraCorners");
            foreach (var k in corners)
            {
                w.WriteStartArray();
                w.WriteNumberValue(k.X);
                w.WriteNumberValue(k.Y);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    // ---- STATE, context, validation ------------------------------------------------------------------------------------------

    internal static void WriteAlignRun(Utf8JsonWriter w, AlignRunView? v)
    {
        if (v is null)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        w.WriteString("id", v.Id);
        w.WriteString("group", v.GroupId);
        w.WriteString("state", v.State);
        w.WriteString("light", v.State switch
        {
            "accepted" => "green",
            "running" or "staged" => "amber",
            "failed" => "red",
            _ => "grey",
        });
        w.WriteString("step", v.Step);
        w.WriteNumber("progress", Math.Round(v.Progress, 3));
        w.WriteNumber("etaS", Math.Round(v.EtaS, 1));
        w.WriteString("words", v.Words);
        w.WriteStartArray("outputs");
        foreach (var o in v.Outputs)
        {
            w.WriteStartObject();
            w.WriteString("id", o.Id);
            w.WriteString("name", o.Name);
            w.WriteString("step", o.Step);
            w.WriteNumber("progress", Math.Round(o.Progress, 3));
            w.WriteString("light", o.Light);
            w.WriteString("words", o.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (v.Score is { } s)
        {
            w.WriteStartObject("score");
            w.WriteNumber("value", Math.Round(s.Value, 1));
            w.WriteString("unit", s.Unit);
            w.WriteString("grade", s.Grade);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("score");
        }
        w.WriteEndObject();
    }

    /// <summary>A group as STATE, the context and INDEX read it: its accepted score (from the ledger result its outputs stand on: a
    /// run's, or a run's brought back by ALIGN REVERT or the ledger's own verbs), whether a run of it is staged or running, its light
    /// and words.</summary>
    internal (QualityScore? Score, string Staged, Light Light, string Words) AlignGroupState(Project p, AlignGroup g)
    {
        var view = AlignBackend.View;
        var mine = view is not null && view.GroupId == g.Id ? view : null;
        var staged = mine is { State: "staged" } ? mine.Id : "";
        QualityScore? score = null;
        foreach (var id in g.OutputIds)
        {
            if (p.FindOutput(id) is { CalibrationId: { } cid } o && p.FindCalibration(cid) is { Source: CalibrationSource.Align or CalibrationSource.Revert } s
                && s.Score.Source == "align" && s.Output is { RunId.Length: > 0 } run && o.Measured?.RunId == run.RunId)
            {
                score = s.Score;
                break;
            }
        }
        var faults = AlignMeasured.Faults;
        var missing = g.OutputIds.Where(faults.ContainsKey).Select(id => faults[id]).FirstOrDefault();
        if (missing is not null)
            return (score, staged, Light.Red, missing);
        if (mine is { State: "running" })
            return (score, staged, Light.Amber, $"measuring: {mine.Words}");
        if (mine is { State: "failed" } && score is null)
            return (score, staged, Light.Red, mine.Words);
        if (staged.Length > 0)
            return (score, staged, Light.Amber, string.Create(CultureInfo.InvariantCulture, $"a run waits: ALIGN ACCEPT {staged} ({mine!.Score?.Grade} {mine.Score?.Value:0})"));
        if (score is null)
            return (null, staged, Light.Grey, g.LastRunId.Length > 0 ? "its outputs no longer stand as its last run" : $"not measured yet — ALIGN RUN {g.Id}");
        var light = score.Grade switch
        {
            "A" or "B" => Light.Green,
            "C" => Light.Amber,
            _ => Light.Red,
        };
        return (score, staged, light, score.Words);
    }

    private void WriteAlign(Utf8JsonWriter w)
    {
        var p = _snapshot.Project.Project;
        w.WriteStartObject("align");
        w.WritePropertyName("run");
        WriteAlignRun(w, AlignBackend.View);
        w.WriteStartArray("groups");
        foreach (var g in p.Stage.Aligns)
        {
            var (score, staged, light, words) = AlignGroupState(p, g);
            w.WriteStartObject();
            w.WriteString("id", g.Id);
            w.WriteString("name", g.Name);
            w.WriteString("shape", g.Shape.ToString().ToLowerInvariant());
            w.WriteString("camera", g.CameraId);
            w.WriteStartArray("outputs");
            foreach (var id in g.OutputIds)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteString("canvas", g.CanvasId);
            w.WriteString("fit", g.Target.Fit.ToString().ToLowerInvariant());
            w.WriteBoolean("corners", !g.Target.CameraCorners.IsDefaultOrEmpty);
            w.WriteString("lastRun", g.LastRunId);
            w.WriteString("staged", staged);
            if (score is null)
            {
                w.WriteNull("score");
                w.WriteString("grade", "");
            }
            else
            {
                w.WriteNumber("score", Math.Round(score.Value, 1));
                w.WriteString("grade", score.Grade);
            }
            w.WriteString("light", light.ToString().ToLowerInvariant());
            w.WriteString("words", words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private void WriteOutputMeasured(Utf8JsonWriter w, Output o)
    {
        if (o.Measured is not { } m)
        {
            w.WriteNull("measured");
            return;
        }
        var set = AlignMeasured;
        w.WriteStartObject("measured");
        w.WriteString("run", m.RunId);
        w.WriteNumber("residualPx", Math.Round(m.ResidualPx, 3));
        if (m.Color is { Gamma: > 0 } colour)
            w.WriteNumber("gamma", Math.Round(colour.Gamma, 3));
        else
            w.WriteNull("gamma");
        w.WriteBoolean("colour", m.Color is { } k && (k.GainR != 1 || k.GainG != 1 || k.GainB != 1 || (k.Level > 0 && k.Level != 1)));
        if (m.Blend is null)
            w.WriteNull("blend");
        else
            w.WriteString("blend", "replace");
        var faulted = set.Faults.TryGetValue(o.Id, out var fault);
        var loaded = set.Loads.ContainsKey(o.Id);
        w.WriteString("files", faulted ? fault : loaded ? "ok" : "loading");
        w.WriteString("light", faulted ? "red" : loaded ? "green" : "grey");
        w.WriteEndObject();
    }

    private void AlignContext(List<ContextItem> items)
    {
        var p = _snapshot.Project.Project;
        foreach (var g in p.Stage.Aligns)
        {
            var (_, _, light, words) = AlignGroupState(p, g);
            items.Add(new ContextItem("align:" + g.Id, "align", g.Name, light, words));
        }
    }

    private static string? ValidateAlignGroup(Project p, AlignGroup g)
    {
        if (string.IsNullOrWhiteSpace(g.Name))
            return "an alignment group has a name";
        if (g.OutputIds.IsDefault || g.OutputIds.Distinct(StringComparer.Ordinal).Count() != g.OutputIds.Length)
            return "an output is named once in a group";
        if (g.OutputIds.FirstOrDefault(id => p.FindOutput(id) is null) is { } gone)
            return $"no output {gone}";
        var o = g.Options;
        if (o.GridColumns is < 2 or > 65 || o.GridRows is < 2 or > 65)
            return "a measured grid is 2–65 nodes each way";
        if (o.Uniformity is < 0 or > 1 || double.IsNaN(o.Uniformity))
            return "uniformity is 0–1";
        if (!(o.EdgeWidth is > 0 and <= 0.5))
            return "edgeWidth is a share of the raster side, over 0 and up to 0.5";
        var t = g.Target;
        if (t.MarginPct is < 0 or > 40 || double.IsNaN(t.MarginPct))
            return "marginPct is 0–40";
        if (t.Aspect < 0 || double.IsNaN(t.Aspect))
            return "aspect is 0 (the canvas's own) or more";
        if (!t.CameraCorners.IsDefaultOrEmpty && (t.CameraCorners.Length != 4 || t.CameraCorners.Any(c => c.X is < 0 or > 1 || c.Y is < 0 or > 1)))
            return "the corners are four points, 0–1 of the camera picture";
        return null;
    }
}
