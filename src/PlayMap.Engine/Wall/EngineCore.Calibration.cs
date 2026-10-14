using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Calibration;
using PlayMap.Core.Calibration.Scoring;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Wall;

namespace PlayMap.Engine;

/// <summary>A calibration task as STATE shows it: a re-solve under way or its end.</summary>
internal sealed record CalibrationTask(string Id, string Kind, string State, string Projector, double Progress, string Words);

/// <summary>The calibration ledger's verbs (CALIBRATION STATUS, HISTORY, SAVE, RECALL, ACCEPT, FALLBACK, FORGET), the re-solve of
/// named projectors from their pins (CALIBRATION RESOLVE, CANCEL), STATE.calibration, the context's calibration items and the
/// ledger as align and facade use it. Every result is scored in one vocabulary; a re-solve applies itself only when its score is
/// within the project's gate (max(CalibrationGate, 1.25 × the fallback's)), and is staged for a person otherwise.</summary>
public sealed partial class EngineCore
{
    private volatile CalibrationTask? _calibrationTask;
    private Action? _cancelCalibration;
    private readonly Lock _calibrationGate = new();

    /// <summary>The ledger as align, facade and the scan record results in (bound to this engine).</summary>
    public ICalibrationLedger CalibrationLedgerService => _ledger ??= new EngineLedger(this);

    private EngineLedger? _ledger;

    private void RouteCalibration(VerbRoutes routes)
    {
        if (Services.Ledger is LateCalibrationLedger late)
            late.Bind(CalibrationLedgerService);
        routes.Lane("CALIBRATION STATUS", CalibrationStatus);
        routes.Lane("CALIBRATION HISTORY", CalibrationHistory);
        routes.Lane("CALIBRATION SAVE", Guarded(CalibrationSave));
        routes.Lane("CALIBRATION RECALL", Guarded(CalibrationRecall));
        routes.Lane("CALIBRATION ACCEPT", Guarded(CalibrationAccept));
        routes.Lane("CALIBRATION FALLBACK", Guarded(CalibrationFallback));
        routes.Lane("CALIBRATION FORGET", Guarded(CalibrationForget));
        routes.OffLane("CALIBRATION RESOLVE", c => WallGate(c) is { } refused ? Task.FromResult(refused) : ResolveAsync(c));
        routes.Lane("CALIBRATION CANCEL", CalibrationCancel);
        routes.Index(new CalibrationIndex(this));
    }

    // ---- targets ---------------------------------------------------------------------------------------------------------

    /// <summary>A projector or output by id or name: its id, name and kind.</summary>
    private static (string Id, string Name, CalibrationTarget Kind)? TargetNamed(Project p, string word)
    {
        if ((p.FindProjector(word) ?? p.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(word, StringComparison.OrdinalIgnoreCase))) is { } pj)
            return (pj.Id, pj.Name, CalibrationTarget.Projector);
        if (OutputNamed(p, word) is { } o)
            return (o.Id, o.Name, CalibrationTarget.Output);
        return null;
    }

    private static IEnumerable<(string Id, string Name, CalibrationTarget Kind)> AllTargets(Project p)
    {
        foreach (var pj in p.Stage.Projectors)
            yield return (pj.Id, pj.Name, CalibrationTarget.Projector);
        foreach (var o in p.Outputs)
            if (o.CalibrationId is not null || o.StagedId is not null || o.FallbackId is not null)
                yield return (o.Id, o.Name, CalibrationTarget.Output);
    }

    /// <summary>The targets a verb's word names: one, or every one for ALL; null with the words when none.</summary>
    private static (List<(string Id, string Name, CalibrationTarget Kind)>? Targets, string? Why) TargetsNamed(Project p, string word)
    {
        if (word.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var all = AllTargets(p).ToList();
            return all.Count == 0 ? (null, "there is no projector to calibrate") : (all, null);
        }
        return TargetNamed(p, word) is { } t ? ([t], null) : (null, $"no projector or output {word}");
    }

    private static string NameOf(Project p, string targetId) =>
        p.FindProjector(targetId)?.Name ?? p.FindOutput(targetId)?.Name ?? targetId;

    /// <summary>A target's current result, or null.</summary>
    private static CalibrationSnapshot? CurrentOf(Project p, string targetId) =>
        CalibrationLedger.Ids(p, targetId).Current is { } id ? p.FindCalibration(id) : null;

    /// <summary>The grade of a projector's current result ("" when it has none).</summary>
    private static string GradeOf(Project p, string targetId) => CurrentOf(p, targetId)?.Score.Grade ?? "";

    private static bool StandsAs(Project p, CalibrationSnapshot s) => s.Target == CalibrationTarget.Projector
        ? p.FindProjector(s.TargetId) is { } pj && CalibrationLedger.Matches(pj, s)
        : p.FindOutput(s.TargetId) is { } o && Equals(o.Measured, s.Output);

    // ---- recording -------------------------------------------------------------------------------------------------------

    /// <summary>The projector as it stands, recorded as a result (scored as it stands) and applied: it becomes current, and the
    /// fallback when it has none. Inside an edit.</summary>
    private Project RecordAsItStands(Project p, string projectorId, string who, bool pinned, string? name, out string id)
    {
        var pj = p.FindProjector(projectorId) ?? throw new EditRefusedException($"no projector {projectorId}");
        id = p.NewId(Ids.Calibration);
        var snap = CalibrationLedger.Of(pj, id, name ?? CalibrationLedger.NameFor(p, pj.Id, CalibrationSource.Manual),
            CalibrationLedger.ScoreAsItStands(pj, "manual"), CalibrationSource.Manual, who, Services.Wall.UnixMs, pinned);
        return CalibrationLedger.Record(p, snap, apply: true).Project;
    }

    /// <summary>PROJECTOR SOLVE's result as one edit: the projector takes the solve's optics and pose, and the ledger records it
    /// (scored) as current — the first result is the fallback too. Answers the snapshot's id and its score.</summary>
    private (EditResult Result, string SnapshotId, QualityScore Score, int Dropped) RecordSolve(Projector pj, SolveLocks locks, PoseSolution solution, string who)
    {
        var solved = pj with { Intrinsics = solution.Intrinsics, Lens = solution.Lens, Pose = solution.Pose, Locks = locks };
        var (score, uncertainty) = CalibrationScoring.ForPins(solved, solution);
        var id = "";
        var dropped = 0;
        var r = Edits.Apply($"solve {pj.Name}", p =>
        {
            id = p.NewId(Ids.Calibration);
            var current = p.FindProjector(pj.Id) ?? throw new EditRefusedException($"no projector {pj.Id}");
            var withSolve = current with { Intrinsics = solution.Intrinsics, Lens = solution.Lens, Pose = solution.Pose, Locks = locks, Uncertainty = uncertainty };
            var snap = CalibrationLedger.Of(withSolve, id, CalibrationLedger.NameFor(p, pj.Id, CalibrationSource.Pins), score, CalibrationSource.Pins, who,
                Services.Wall.UnixMs);
            var project = p.WithStage(st => st with { Projectors = st.Projectors.Update(pj.Id, _ => withSolve) });
            (project, dropped) = CalibrationLedger.Record(project, snap, apply: true);
            return project;
        });
        return (r, id, score, dropped);
    }

    // ---- STATUS, HISTORY -------------------------------------------------------------------------------------------------

    private void WriteTarget(Utf8JsonWriter w, Project p, string targetId, string name, CalibrationTarget kind)
    {
        var (currentId, fallbackId, stagedId) = CalibrationLedger.Ids(p, targetId);
        var current = currentId is null ? null : p.FindCalibration(currentId);
        var score = current?.Score;
        w.WriteStartObject();
        w.WriteString("id", targetId);
        w.WriteString("kind", kind == CalibrationTarget.Projector ? "projector" : "output");
        w.WriteString("name", name);
        w.WriteNumber("score", score is null ? -1 : score.Value);
        w.WriteString("unit", score?.Unit ?? "px");
        w.WriteString("grade", score?.Grade ?? "");
        w.WriteString("source", score?.Source ?? "");
        w.WriteString("words", score is null
            ? (kind == CalibrationTarget.Projector ? $"{name} has no calibration result — PROJECTOR SOLVE {targetId}, or CALIBRATION SAVE {targetId}" : $"{name} has no result")
            : score.Words);
        w.WriteString("worst", score is null ? "" : CalibrationScoring.Worst(score));
        w.WriteNumber("confidence", score?.Confidence ?? -1);
        WriteNullable(w, "snapshot", currentId);
        WriteNullable(w, "fallback", fallbackId);
        WriteNullable(w, "staged", stagedId);
        w.WriteNumber("history", p.Stage.Calibrations.Count(c => c.TargetId == targetId));
        w.WriteNumber("verifiedP95", current is { VerifiedP95Px: > 0 } v ? v.VerifiedP95Px : -1);
        w.WriteStartArray("terms");
        foreach (var t in score?.Terms ?? [])
        {
            w.WriteStartObject();
            w.WriteString("code", t.Code);
            w.WriteString("words", t.Words);
            w.WriteNumber("value", t.Value);
            w.WriteString("unit", t.Unit);
            w.WriteNumber("penalty", t.Penalty);
            w.WriteString("light", t.Light);
            w.WriteString("where", t.Where);
            w.WriteString("fix", t.Fix);
            w.WriteString("hint", t.Hint);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
            w.WriteNull(name);
        else
            w.WriteString(name, value);
    }

    private void WriteTask(Utf8JsonWriter w)
    {
        if (_calibrationTask is not { } t)
        {
            w.WriteNull("task");
            return;
        }
        w.WriteStartObject("task");
        w.WriteString("id", t.Id);
        w.WriteString("kind", t.Kind);
        w.WriteString("state", t.State);
        w.WriteString("projector", t.Projector);
        w.WriteNumber("progress", Math.Round(t.Progress, 2));
        w.WriteString("words", t.Words);
        w.WriteEndObject();
    }

    /// <summary>STATE.calibration: the projectors a check found moved (none until the camera check lands), the running or last task,
    /// and every target's score with its terms, current, fallback and staged results.</summary>
    private void WriteCalibration(Utf8JsonWriter w)
    {
        var p = _snapshot.Project.Project;
        w.WriteStartObject("calibration");
        w.WriteStartArray("moved");
        w.WriteEndArray();
        WriteTask(w);
        w.WriteStartArray("targets");
        foreach (var (id, name, kind) in AllTargets(p))
            WriteTarget(w, p, id, name, kind);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>CALIBRATION STATUS [&lt;projector&gt;|&lt;output&gt;|ALL]: as STATE.calibration, for one target or all.</summary>
    private CommandReply CalibrationStatus(VerbCall c)
    {
        var p = Store.Project;
        var (targets, why) = c.Args.Count == 0 ? TargetsNamed(p, "ALL") : c.Args.Count == 1 ? TargetsNamed(p, c.Args[0]) : (null, "CALIBRATION STATUS [<projector>|<output>|ALL]");
        if (targets is null)
            return CommandReply.Fail(why!);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            WriteTask(w);
            w.WriteStartArray("targets");
            foreach (var (id, name, kind) in targets)
                WriteTarget(w, p, id, name, kind);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    /// <summary>CALIBRATION HISTORY &lt;projector&gt;|&lt;output&gt; [&lt;n&gt;]: its results newest first, each with its score and how far it moved
    /// the picture from the one before (mm, degrees, wall px).</summary>
    private CommandReply CalibrationHistory(VerbCall c)
    {
        const string Syntax = "CALIBRATION HISTORY <projector>|<output> [<n>]";
        var n = 20;
        if (c.Args.Count is < 1 or > 2 || (c.Args.Count == 2 && (!int.TryParse(c.Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 1)))
            return CommandReply.Fail(Syntax);
        var p = Store.Project;
        if (TargetNamed(p, c.Args[0]) is not { } t)
            return CommandReply.Fail($"no projector or output {c.Args[0]}");
        var all = CalibrationLedger.History(p, t.Id);
        var (cur, fb, staged) = CalibrationLedger.Ids(p, t.Id);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("target", t.Id);
            w.WriteString("name", t.Name);
            w.WriteNumber("count", all.Count);
            w.WriteStartArray("snapshots");
            for (var i = 0; i < Math.Min(n, all.Count); i++)
            {
                var s = all[i];
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                w.WriteNumber("at", s.CreatedUnixMs);
                w.WriteString("source", s.Source.ToString().ToLowerInvariant());
                w.WriteNumber("score", s.Score.Value);
                w.WriteString("grade", s.Score.Grade);
                w.WriteString("words", s.Score.Words);
                w.WriteNumber("verifiedP95", s.VerifiedP95Px > 0 ? s.VerifiedP95Px : -1);
                w.WriteString("who", s.Who);
                WriteNullable(w, "parent", s.ParentId);
                WriteNullable(w, "scan", s.ScanId);
                w.WriteStartObject("delta");
                var (mm, deg, wallPx) = i + 1 < all.Count ? CalibrationLedger.Delta(all[i + 1], s) : (-1, -1, -1);
                w.WriteNumber("mm", Math.Round(mm, 2));
                w.WriteNumber("deg", Math.Round(deg, 4));
                w.WriteNumber("wallPx", Math.Round(wallPx, 2));
                w.WriteEndObject();
                w.WriteBoolean("current", s.Id == cur);
                w.WriteBoolean("fallback", s.Id == fb);
                w.WriteBoolean("staged", s.Id == staged);
                w.WriteBoolean("pinned", s.Pinned);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    // ---- SAVE, RECALL, ACCEPT, FALLBACK, FORGET --------------------------------------------------------------------------

    /// <summary>CALIBRATION SAVE &lt;projector&gt;|ALL [AS &lt;name&gt;]: the calibration as it stands kept as a pinned result (never pruned).</summary>
    private CommandReply CalibrationSave(VerbCall c)
    {
        var args = new List<string>(c.Args);
        string? name = null;
        var ai = args.FindIndex(a => a.Equals("AS", StringComparison.OrdinalIgnoreCase));
        if (ai >= 0)
        {
            name = string.Join(' ', args.Skip(ai + 1)).Trim();
            args.RemoveRange(ai, args.Count - ai);
            if (name.Length == 0)
                return CommandReply.Fail("AS takes a name");
        }
        if (args.Count != 1)
            return CommandReply.Fail("CALIBRATION SAVE <projector>|<output>|ALL [AS <name>]");
        var p = Edits.Project;
        var (targets, why) = TargetsNamed(p, args[0]);
        if (targets is null)
            return CommandReply.Fail(why!);
        var save = new List<string>();
        var refused = new List<string>();
        foreach (var t in targets)
        {
            if (t.Kind != CalibrationTarget.Projector)
            {
                var cur = CurrentOf(p, t.Id);
                if (cur is null)
                    refused.Add($"{t.Name} has no result to keep");
                else if (cur.Pinned)
                    refused.Add($"{t.Name}'s result {cur.Id} is kept already");
                else
                    save.Add(t.Id);
                continue;
            }
            if (CurrentOf(p, t.Id) is { Pinned: true } kept && StandsAs(p, kept) && name is null)
                refused.Add($"{t.Name} is kept already as {kept.Name} ({kept.Id})");
            else
                save.Add(t.Id);
        }
        if (save.Count == 0)
            return CommandReply.Fail(string.Join("; ", refused));
        var ids = new List<string>();
        var r = Edits.Apply($"keep the calibration of {string.Join(", ", save.Select(id => NameOf(p, id)))}", project =>
        {
            ids.Clear();
            foreach (var id in save)
            {
                if (project.FindProjector(id) is not null)
                {
                    project = RecordAsItStands(project, id, c.Session.ClientName, pinned: true, name, out var sid);
                    ids.Add(sid);
                }
                else
                {
                    var cur = CurrentOf(project, id)!;
                    var sid = project.NewId(Ids.Calibration);
                    (project, _) = CalibrationLedger.Record(project, cur with { Id = sid, Name = name ?? "Kept " + cur.Name, Pinned = true,
                        CreatedUnixMs = Services.Wall.UnixMs, ParentId = cur.Id, Source = CalibrationSource.Manual, Who = c.Session.ClientName }, apply: true);
                    ids.Add(sid);
                }
            }
            return project;
        });
        if (!r.Changed)
            return FromEdit(r);
        var words = $"kept {string.Join(", ", save.Select((id, i) => $"{NameOf(Store.Project, id)} as {ids[i]}"))}"
            + (refused.Count > 0 ? "; " + string.Join("; ", refused) : "");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("snapshots");
            foreach (var id in ids)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>CALIBRATION RECALL &lt;snapshot&gt;: a kept result put back exactly, recorded as a new result.</summary>
    private CommandReply CalibrationRecall(VerbCall c)
    {
        if (c.Args.Count != 1)
            return CommandReply.Fail("CALIBRATION RECALL <snapshot>");
        var p = Edits.Project;
        if (p.FindCalibration(c.Args[0]) is not { } s)
            return CommandReply.Fail($"no calibration result {c.Args[0]} — CALIBRATION HISTORY lists them");
        return Recall(s, c.Session.ClientName, "recall");
    }

    private CommandReply Recall(CalibrationSnapshot s, string who, string verb)
    {
        var p = Edits.Project;
        var name = NameOf(p, s.TargetId);
        if (TargetNamed(p, s.TargetId) is null)
            return CommandReply.Fail($"{s.Id}'s {s.Target.ToString().ToLowerInvariant()} {s.TargetId} is gone");
        if (StandsAs(p, s))
            return CommandReply.Fail($"{name} stands as {s.Name} ({s.Id}) already");
        var before = CurrentOf(p, s.TargetId);
        var recorded = "";
        var r = Edits.Apply($"{verb} {name} to {s.Name}", project =>
        {
            (project, recorded) = CalibrationLedger.Recall(project, s, project.NewId(Ids.Calibration), who, Services.Wall.UnixMs);
            return project;
        });
        if (!r.Changed)
            return FromEdit(r);
        var wallPx = before is null ? -1 : CalibrationLedger.Delta(before, s).WallPx;
        var words = string.Create(CultureInfo.InvariantCulture, $"{name} is back to {s.Name} ({s.Id}, {s.Score.Grade} {s.Score.Value:0.0}) as {recorded}")
            + (wallPx >= 0 ? string.Create(CultureInfo.InvariantCulture, $"; the picture moved {wallPx:0.0} px") : "");
        RaiseEvent("calibration.recalled", LeaderNowNs, w =>
        {
            w.WriteString("target", s.TargetId);
            w.WriteString("snapshot", recorded);
            w.WriteString("from", s.Id);
        });
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("target", s.TargetId);
            w.WriteString("applied", s.Id);
            w.WriteString("recorded", recorded);
            w.WriteNumber("deltaWallPx", Math.Round(wallPx, 2));
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>CALIBRATION ACCEPT &lt;projector&gt;|&lt;output&gt;|ALL|&lt;snapshot&gt;: a staged result applied (when one waits) and made the
    /// fallback; otherwise the current one made the fallback (a projector with none records itself as it stands first).</summary>
    private CommandReply CalibrationAccept(VerbCall c)
    {
        if (c.Args.Count != 1)
            return CommandReply.Fail("CALIBRATION ACCEPT <projector>|<output>|ALL|<snapshot>");
        var (r, words, fallbacks) = Accept(c.Args[0], c.Session.ClientName);
        if (r is null)
            return CommandReply.Fail(words);
        if (!r.Changed)
            return FromEdit(r);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("fallbacks");
            foreach (var (target, snap) in fallbacks)
            {
                w.WriteStartObject();
                w.WriteString("target", target);
                w.WriteString("fallback", snap);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>ACCEPT's work on the lane, for the verb and the ledger seam: the edit, its words, and each target's new fallback.</summary>
    private (EditResult? Result, string Words, List<(string Target, string Snapshot)> Fallbacks) Accept(string word, string who)
    {
        var p = Edits.Project;
        var plan = new List<(string Target, string? Snapshot)>();
        var refused = new List<string>();
        if (p.FindCalibration(word) is { } snap)
        {
            var (cur, fb, staged) = CalibrationLedger.Ids(p, snap.TargetId);
            if (snap.Id == fb)
                return (null, $"{snap.Id} is {NameOf(p, snap.TargetId)}'s fallback already", []);
            if (snap.Id != cur && snap.Id != staged)
                return (null, $"{snap.Id} is neither current nor staged — CALIBRATION RECALL {snap.Id} first", []);
            plan.Add((snap.TargetId, snap.Id));
        }
        else
        {
            var (targets, why) = TargetsNamed(p, word);
            if (targets is null)
                return (null, why!, []);
            foreach (var t in targets)
            {
                var (cur, fb, staged) = CalibrationLedger.Ids(p, t.Id);
                if (staged is not null)
                    plan.Add((t.Id, staged));
                else if (cur is not null && cur == fb)
                    refused.Add($"{t.Name}'s current result is its fallback already");
                else if (cur is null && t.Kind == CalibrationTarget.Output)
                    refused.Add($"{t.Name} has no result to accept");
                else
                    plan.Add((t.Id, cur));
            }
        }
        if (plan.Count == 0)
            return (null, string.Join("; ", refused), []);
        var fallbacks = new List<(string Target, string Snapshot)>();
        var r = Edits.Apply($"accept the calibration of {string.Join(", ", plan.Select(x => NameOf(p, x.Target)))}", project =>
        {
            fallbacks.Clear();
            foreach (var (target, snapshot) in plan)
            {
                var id = snapshot;
                if (id is null)
                {
                    project = RecordAsItStands(project, target, who, pinned: false, null, out var made);
                    id = made;
                }
                var s = project.FindCalibration(id) ?? throw new EditRefusedException($"no calibration result {id}");
                var (cur, _, staged) = CalibrationLedger.Ids(project, target);
                if (s.Id == staged && s.Id != cur)
                    project = CalibrationLedger.MakeCurrent(project, s);
                project = s.Target == CalibrationTarget.Projector
                    ? project.WithStage(st => st with { Projectors = st.Projectors.Update(target, pj => pj with { FallbackId = s.Id, StagedId = pj.StagedId == s.Id ? null : pj.StagedId }) })
                    : project with { Outputs = project.Outputs.Update(target, o => o with { FallbackId = s.Id, StagedId = o.StagedId == s.Id ? null : o.StagedId }) };
                fallbacks.Add((target, s.Id));
            }
            return project;
        });
        var words = r.Changed
            ? string.Join("; ", fallbacks.Select(f => $"{NameOf(Store.Project, f.Target)} falls back to {f.Snapshot}")) + (refused.Count > 0 ? "; " + string.Join("; ", refused) : "")
            : r.Effect;
        return (r, words, fallbacks);
    }

    /// <summary>CALIBRATION FALLBACK &lt;projector&gt;|&lt;output&gt;|ALL: back to the fallback result exactly (recorded as a new result).</summary>
    private CommandReply CalibrationFallback(VerbCall c)
    {
        if (c.Args.Count != 1)
            return CommandReply.Fail("CALIBRATION FALLBACK <projector>|<output>|ALL");
        var p = Edits.Project;
        var (targets, why) = TargetsNamed(p, c.Args[0]);
        if (targets is null)
            return CommandReply.Fail(why!);
        if (targets.Count == 1)
        {
            var t = targets[0];
            if (CalibrationLedger.Ids(p, t.Id).Fallback is not { } fb || p.FindCalibration(fb) is not { } s)
                return CommandReply.Fail($"{t.Name} has no fallback — CALIBRATION ACCEPT {t.Id} makes its current calibration the fallback");
            return Recall(s, c.Session.ClientName, "fall back");
        }
        var back = targets.Select(t => CalibrationLedger.Ids(p, t.Id).Fallback is { } fb && p.FindCalibration(fb) is { } s && !StandsAs(p, s) ? s : null)
            .OfType<CalibrationSnapshot>().ToList();
        if (back.Count == 0)
            return CommandReply.Fail("every projector stands on its fallback (or has none)");
        var recorded = new List<string>();
        var r = Edits.Apply("fall back " + string.Join(", ", back.Select(s => NameOf(p, s.TargetId))), project =>
        {
            recorded.Clear();
            foreach (var s in back)
            {
                (project, var id) = CalibrationLedger.Recall(project, s, project.NewId(Ids.Calibration), c.Session.ClientName, Services.Wall.UnixMs);
                recorded.Add(id);
            }
            return project;
        });
        if (!r.Changed)
            return FromEdit(r);
        var words = "back to the fallback: " + string.Join(", ", back.Select(s => $"{NameOf(Store.Project, s.TargetId)} ({s.Id})"));
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("recorded");
            foreach (var id in recorded)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>CALIBRATION FORGET &lt;snapshot&gt;: a kept result removed (never the current, fallback or staged one).</summary>
    private CommandReply CalibrationForget(VerbCall c)
    {
        if (c.Args.Count != 1)
            return CommandReply.Fail("CALIBRATION FORGET <snapshot>");
        var p = Edits.Project;
        if (p.FindCalibration(c.Args[0]) is not { } s)
            return CommandReply.Fail($"no calibration result {c.Args[0]}");
        var (cur, fb, staged) = CalibrationLedger.Ids(p, s.TargetId);
        var role = s.Id == cur ? "current" : s.Id == fb ? "fallback" : s.Id == staged ? "staged" : null;
        if (role is not null)
            return CommandReply.Fail($"{s.Id} is {NameOf(p, s.TargetId)}'s {role} result — it cannot be forgotten");
        var r = Edits.Apply($"forget {s.Name}", project => project.WithStage(st => st with { Calibrations = st.Calibrations.RemoveId(s.Id) }));
        if (!r.Changed)
            return FromEdit(r);
        var words = $"forgot {s.Name} ({s.Id}) of {NameOf(p, s.TargetId)}";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("removed", s.Id);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    // ---- RESOLVE, CANCEL -------------------------------------------------------------------------------------------------

    private void SetTask(CalibrationTask? task)
    {
        _calibrationTask = task;
        FactsChanged();
    }

    /// <summary>CALIBRATION RESOLVE &lt;projector&gt;… [STAGE]: each named projector solved again from its pins as they stand (the operator
    /// dragged them onto the wall's features), against the stage, scored, and in one edit applied — or staged when STAGE says so,
    /// when an assistant asked, or when its score fails the gate. A projector with no fallback keeps its result from before as one.
    /// No other projector, result or surface moves.</summary>
    private async Task<CommandReply> ResolveAsync(VerbCall c)
    {
        const string Syntax = "CALIBRATION RESOLVE <projector>… [STAGE]";
        var args = new List<string>(c.Args);
        var stage = args.RemoveAll(a => a.Equals("STAGE", StringComparison.OrdinalIgnoreCase)) > 0;
        if (args.Count == 0)
            return CommandReply.Fail(Syntax);
        if (args.Any(a => a.Equals("CAMERA", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail("a re-solve from a fresh camera capture is not on this node yet — drag the pins onto the wall's features, then CALIBRATION RESOLVE <projector>");
        var p = Store.Project;
        var named = new List<Projector>();
        foreach (var word in args)
        {
            if (ProjectorNamed(p, word) is not { } pj)
                return CommandReply.Fail($"no projector {word}");
            if (pj.Locked)
                return CommandReply.Fail($"{pj.Name} is locked — unlock it to solve it again");
            var pins = pj.Pins.Count(x => x.Enabled);
            if (pins < 4)
                return CommandReply.Fail($"{pj.Name} has {pins} pin{(pins == 1 ? "" : "s")} — a solve needs six or more spread out (four with the optics locked)");
            if (!named.Any(x => x.Id == pj.Id))
                named.Add(pj);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Ct);
        var taskId = Ids.New("kt");
        var task = new CalibrationTask(taskId, "resolve", "solving", named[0].Id, 0, $"solving {string.Join(", ", named.Select(x => x.Name))} from their pins");
        lock (_calibrationGate)
        {
            if (_calibrationTask is { State: "solving" } running)
                return CommandReply.Fail($"a calibration task is running ({running.Id}) — CALIBRATION CANCEL first");
            _calibrationTask = task;
            _cancelCalibration = cts.Cancel;
        }
        FactsChanged();
        var ai = c.Session.Transport is "mcp" or "assistant";
        try
        {
            var solved = new List<(Projector Before, PoseSolution Solution, Projector After, QualityScore Score, SolveUncertainty? Uncertainty)>();
            for (var i = 0; i < named.Count; i++)
            {
                var pj = named[i];
                var solution = await Task.Run(() => PoseSolver.Solve(pj, PoseSolveOptions.Default with { Locks = pj.Locks, InitialPose = pj.Pose }), cts.Token)
                    .ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();
                var after = pj with { Intrinsics = solution.Intrinsics, Lens = solution.Lens, Pose = solution.Pose };
                var (score, uncertainty) = CalibrationScoring.ForPins(after, solution, "resolve");
                solved.Add((pj, solution, after, score, uncertainty));
                task = task with { Projector = pj.Id, Progress = (i + 1.0) / named.Count };
                SetTask(task);
            }
            var gate = p.Settings.CalibrationGate;
            var results = new List<(string Projector, string Snapshot, bool Applied, QualityScore Score, string Why)>();
            var r = await EditAsync(c.Session, $"re-solve {string.Join(", ", named.Select(x => x.Name))}", project =>
            {
                results.Clear();
                foreach (var (before, _, after, score, uncertainty) in solved)
                {
                    var now = project.FindProjector(before.Id) ?? throw new EditRefusedException($"no projector {before.Id}");
                    if (now.FallbackId is null)
                        project = RecordAsItStands(project, now.Id, c.Session.ClientName, pinned: false, null, out _);
                    now = project.FindProjector(before.Id)!;
                    var fallback = now.FallbackId is { } fid ? project.FindCalibration(fid) : null;
                    var limit = Math.Max(gate, 1.25 * (fallback?.Score.Value ?? 0));
                    var why = stage ? "STAGE asked for it" : ai ? "an assistant asked: a person accepts it"
                        : score.Grade == "F" ? $"it grades F ({score.Words})"
                        : score.Value > limit ? string.Create(CultureInfo.InvariantCulture, $"its score {score.Value:0.0} is over the gate {limit:0.0}")
                        : "";
                    var apply = why.Length == 0;
                    var id = project.NewId(Ids.Calibration);
                    var result = now with { Intrinsics = after.Intrinsics, Lens = after.Lens, Pose = after.Pose, Uncertainty = apply ? uncertainty : now.Uncertainty };
                    var snap = CalibrationLedger.Of(result, id, CalibrationLedger.NameFor(project, now.Id, CalibrationSource.Resolve), score,
                        CalibrationSource.Resolve, c.Session.ClientName, Services.Wall.UnixMs);
                    if (apply)
                        project = project.WithStage(st => st with { Projectors = st.Projectors.Update(now.Id, _ => result) });
                    (project, _) = CalibrationLedger.Record(project, snap, apply);
                    results.Add((now.Id, id, apply, score, why));
                }
                return project;
            }).ConfigureAwait(false);
            if (!r.Changed)
            {
                SetTask(task with { State = "failed", Words = r.Effect });
                return FromEdit(r);
            }
            var after2 = Store.Project;
            foreach (var x in results.Where(x => !x.Applied))
                RaiseEvent("calibration.staged", LeaderNowNs, w =>
                {
                    w.WriteString("projector", x.Projector);
                    w.WriteString("snapshot", x.Snapshot);
                    w.WriteString("words", x.Why);
                });
            var words = string.Join("; ", results.Select(x => string.Create(CultureInfo.InvariantCulture,
                $"{NameOf(after2, x.Projector)} {(x.Applied ? "re-solved" : "staged")}: {x.Score.Words}{(x.Applied ? "" : $" — {x.Why}; CALIBRATION ACCEPT {x.Snapshot} applies it")}")));
            SetTask(task with { State = "done", Progress = 1, Words = words });
            return CommandReply.Success(JsonOut(w =>
            {
                w.WriteStartObject();
                w.WriteString("task", taskId);
                w.WriteStartArray("projectors");
                foreach (var x in results)
                {
                    w.WriteStartObject();
                    w.WriteString("id", x.Projector);
                    w.WriteString("snapshot", x.Snapshot);
                    w.WriteBoolean("applied", x.Applied);
                    w.WriteBoolean("staged", !x.Applied);
                    w.WriteString("why", x.Why);
                    w.WriteStartObject("score");
                    w.WriteNumber("value", x.Score.Value);
                    w.WriteString("grade", x.Score.Grade);
                    w.WriteNumber("rmsPx", x.Score.RmsPx);
                    w.WriteString("words", x.Score.Words);
                    w.WriteEndObject();
                    var before = solved.First(s => s.Before.Id == x.Projector).Before;
                    var snap = after2.FindCalibration(x.Snapshot);
                    var (mm, deg, wallPx) = snap is { Projector: not null }
                        ? CalibrationLedger.Delta(CalibrationLedger.Of(before, "", "", QualityScore.None, CalibrationSource.Manual, "", 0), snap)
                        : (-1, -1, -1);
                    w.WriteStartObject("delta");
                    w.WriteNumber("mm", Math.Round(mm, 2));
                    w.WriteNumber("deg", Math.Round(deg, 4));
                    w.WriteNumber("wallPx", Math.Round(wallPx, 2));
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("words", words);
                w.WriteEndObject();
            }), words);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetTask(task with { State = "cancelled", Words = "cancelled — nothing was applied" });
            return CommandReply.Fail("cancelled — nothing was applied");
        }
    }

    /// <summary>CALIBRATION CANCEL: stops the running calibration task before it applies anything.</summary>
    private CommandReply CalibrationCancel(VerbCall c)
    {
        if (c.Args.Count != 0)
            return CommandReply.Fail("CALIBRATION CANCEL");
        lock (_calibrationGate)
        {
            if (_calibrationTask is not { State: "solving" } t || _cancelCalibration is not { } cancel)
                return CommandReply.Fail("no calibration task is running");
            cancel();
            return CommandReply.Success(effect: $"cancelling {t.Id}");
        }
    }

    // ---- context ---------------------------------------------------------------------------------------------------------

    /// <summary>One context item per projector with a result (and output with one): red F, amber C or staged, green A/B.</summary>
    private void ContextCalibration(List<ContextItem> items)
    {
        var p = _snapshot.Project.Project;
        foreach (var (id, name, _) in AllTargets(p))
        {
            var (_, _, staged) = CalibrationLedger.Ids(p, id);
            if (CurrentOf(p, id) is not { } cur)
            {
                if (staged is not null)
                    items.Add(new ContextItem("calibration:" + id, "calibration", name, Light.Amber, $"a result waits: CALIBRATION ACCEPT {staged}"));
                continue;
            }
            var light = cur.Score.Grade switch
            {
                "F" => Light.Red,
                "C" => Light.Amber,
                _ when staged is not null => Light.Amber,
                _ => Light.Green,
            };
            var words = cur.Score.Words + (staged is not null ? $" · a result waits: CALIBRATION ACCEPT {staged}" : "");
            items.Add(new ContextItem("calibration:" + id, "calibration", name, light, words));
        }
    }

    /// <summary>The ledger as the packages that make results use it: every call is an edit on the lane.</summary>
    private sealed class EngineLedger(EngineCore engine) : ICalibrationLedger
    {
        public async Task<(EditResult Result, string SnapshotId)> RecordAsync(SessionInfo session, CalibrationSnapshot snapshot, bool apply, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(snapshot);
            ct.ThrowIfCancellationRequested();
            var id = "";
            var r = await engine.EditAsync(session, $"{(apply ? "apply" : "stage")} {snapshot.Name}", p =>
            {
                if (TargetNamed(p, snapshot.TargetId) is null)
                    throw new EditRefusedException($"no projector or output {snapshot.TargetId}");
                id = snapshot.Id.Length > 0 && p.FindCalibration(snapshot.Id) is null ? snapshot.Id : p.NewId(Ids.Calibration);
                return CalibrationLedger.Record(p, snapshot with { Id = id, ParentId = snapshot.ParentId ?? CalibrationLedger.Ids(p, snapshot.TargetId).Current }, apply).Project;
            }).ConfigureAwait(false);
            if (r.Changed && !apply)
                engine.RaiseEvent("calibration.staged", engine.LeaderNowNs, w =>
                {
                    w.WriteString("target", snapshot.TargetId);
                    w.WriteString("snapshot", id);
                });
            return (r, r.Changed ? id : "");
        }

        public Task<EditResult> AcceptAsync(SessionInfo session, string targetOrSnapshotId, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(session);
            return engine._lane.RunAsync(() => engine.EditsInto(session, () =>
            {
                var (r, words, _) = engine.Accept(targetOrSnapshotId, session.ClientName);
                return r ?? new EditResult(false, words, engine.Store.Current.Revision);
            }));
        }

        public Task<EditResult> FallbackAsync(SessionInfo session, string targetId, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(session);
            return engine._lane.RunAsync(() => engine.EditsInto(session, () =>
            {
                var p = engine.Edits.Project;
                if (CalibrationLedger.Ids(p, targetId).Fallback is not { } fb || p.FindCalibration(fb) is not { } s)
                    return new EditResult(false, $"{NameOf(p, targetId)} has no fallback", engine.Store.Current.Revision);
                if (StandsAs(p, s))
                    return new EditResult(false, $"{NameOf(p, targetId)} stands on its fallback already", engine.Store.Current.Revision);
                return engine.Edits.Apply($"fall back {NameOf(p, targetId)}",
                    project => CalibrationLedger.Recall(project, s, project.NewId(Ids.Calibration), session.ClientName, engine.Services.Wall.UnixMs).Project);
            }));
        }

        public QualityScore ScoreOf(Project project, string targetId)
        {
            ArgumentNullException.ThrowIfNull(project);
            return CurrentOf(project, targetId)?.Score ?? QualityScore.None;
        }
    }
}
