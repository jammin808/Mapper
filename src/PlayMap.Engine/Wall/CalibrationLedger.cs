using System.Globalization;
using PlayMap.Core.Calibration;
using PlayMap.Core.Calibration.Scoring;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Engine.Wall;

/// <summary>The calibration ledger as pure functions on a project, used inside the caller's edit so a result and what it changes
/// are one undo step. A result (<see cref="CalibrationSnapshot"/>) is never edited after it is made: recording one adds it, applies
/// it (the projector's optics, pose, locks and pins, or the output's measured correction, and the target's current id) or stages
/// it, and prunes the target's history to the project's CalibrationKeep besides its current, fallback, staged and pinned results.
/// The first result applied to a target is also its fallback.</summary>
internal static class CalibrationLedger
{
    /// <summary>A projector's state as a result, its parent the projector's current one.</summary>
    public static CalibrationSnapshot Of(Projector pj, string id, string name, QualityScore score, CalibrationSource source, string who,
        long createdUnixMs, bool pinned = false, string? scanId = null) =>
        new(id, name, CalibrationTarget.Projector, pj.Id, createdUnixMs, source, scanId, pj.CalibrationId, score, who, pinned)
        {
            Projector = new Core.Model.ProjectorCalibration(pj.Intrinsics, pj.Lens, pj.Pose, pj.Locks, pj.Pins),
        };

    /// <summary>A projector with a result's optics, pose, locks and pins put back exactly.</summary>
    public static Projector ApplyTo(Projector pj, CalibrationSnapshot s)
    {
        var c = s.Projector ?? throw new ArgumentException($"{s.Id} is not a projector's result", nameof(s));
        return pj with
        {
            Intrinsics = c.Intrinsics,
            Lens = c.Lens,
            Pose = c.Pose,
            Locks = c.Locks,
            Pins = c.Pins,
            // A solve's (or a recalled solve's) residual; a result kept as it stood leaves the projector's own.
            SolveResidualPx = s.Source != CalibrationSource.Manual && s.Score.RmsPx is >= 0 and < 99 ? Math.Max(s.Score.RmsPx, 1e-6) : pj.SolveResidualPx,
        };
    }

    /// <summary>A kept or staged result made the target's current one: its state applied, its id current, no longer staged.</summary>
    public static Project MakeCurrent(Project p, CalibrationSnapshot s) => s.Target == CalibrationTarget.Projector
        ? p.WithStage(st => st with
        {
            Projectors = st.Projectors.Update(s.TargetId, pj => ApplyTo(pj, s) with { CalibrationId = s.Id, StagedId = pj.StagedId == s.Id ? null : pj.StagedId }),
        })
        : p with { Outputs = p.Outputs.Update(s.TargetId, o => o with { Measured = s.Output, CalibrationId = s.Id, StagedId = o.StagedId == s.Id ? null : o.StagedId }) };

    /// <summary>True when the projector stands exactly as the result has it.</summary>
    public static bool Matches(Projector pj, CalibrationSnapshot s) =>
        s.Projector is { } c && c.Intrinsics == pj.Intrinsics && c.Lens == pj.Lens && c.Pose == pj.Pose && c.Locks == pj.Locks
        && c.Pins.SequenceEqual(pj.Pins);

    /// <summary>Adds a result, applies or stages it, and prunes its target's history. Answers how many results were dropped.</summary>
    public static (Project Project, int Dropped) Record(Project p, CalibrationSnapshot s, bool apply)
    {
        p = p.WithStage(st => st with { Calibrations = st.Calibrations.Add(s) });
        p = s.Target == CalibrationTarget.Projector
            ? p.WithStage(st => st with
            {
                Projectors = st.Projectors.Update(s.TargetId, pj => apply
                    ? ApplyTo(pj, s) with { CalibrationId = s.Id, FallbackId = pj.FallbackId ?? s.Id, StagedId = pj.StagedId == s.Id ? null : pj.StagedId }
                    : pj with { StagedId = s.Id }),
            })
            : p with
            {
                Outputs = p.Outputs.Update(s.TargetId, o => apply
                    ? o with { Measured = s.Output, CalibrationId = s.Id, FallbackId = o.FallbackId ?? s.Id, StagedId = o.StagedId == s.Id ? null : o.StagedId }
                    : o with { StagedId = s.Id }),
            };
        return Prune(p, s.TargetId);
    }

    /// <summary>Makes a kept result current again exactly, as a new result (source revert, its parent the replaced current, its
    /// score the recalled one's) so the history stays append-only in meaning.</summary>
    public static (Project Project, string Recorded) Recall(Project p, CalibrationSnapshot s, string newId, string who, long nowUnixMs)
    {
        var current = s.Target == CalibrationTarget.Projector ? p.FindProjector(s.TargetId)?.CalibrationId : p.FindOutput(s.TargetId)?.CalibrationId;
        var copy = s with
        {
            Id = newId,
            Name = "Back to " + s.Name,
            CreatedUnixMs = nowUnixMs,
            Source = CalibrationSource.Revert,
            ParentId = current,
            Who = who,
            Pinned = false,
        };
        var (next, _) = Record(p, copy, apply: true);
        return (next, newId);
    }

    /// <summary>The target's results beyond its current, fallback, staged and pinned ones, kept newest first up to the project's
    /// CalibrationKeep; the rest are dropped.</summary>
    public static (Project Project, int Dropped) Prune(Project p, string targetId)
    {
        var keep = Math.Clamp(p.Settings.CalibrationKeep, 1, 100);
        var (current, fallback, staged) = Ids(p, targetId);
        var mine = p.Stage.Calibrations.Where(c => c.TargetId == targetId).ToList();
        var loose = mine.Where(c => !c.Pinned && c.Id != current && c.Id != fallback && c.Id != staged)
            .Select((c, i) => (c, i)).OrderByDescending(x => x.c.CreatedUnixMs).ThenByDescending(x => x.i).Select(x => x.c).ToList();
        if (loose.Count <= keep)
            return (p, 0);
        var drop = loose.Skip(keep).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return (p.WithStage(st => st with { Calibrations = [.. st.Calibrations.Where(c => !drop.Contains(c.Id))] }), drop.Count);
    }

    /// <summary>A target's current, fallback and staged result ids.</summary>
    public static (string? Current, string? Fallback, string? Staged) Ids(Project p, string targetId) =>
        p.FindProjector(targetId) is { } pj ? (pj.CalibrationId, pj.FallbackId, pj.StagedId)
        : p.FindOutput(targetId) is { } o ? (o.CalibrationId, o.FallbackId, o.StagedId)
        : (null, null, null);

    /// <summary>A target's results, newest first.</summary>
    public static List<CalibrationSnapshot> History(Project p, string targetId) =>
        [.. p.Stage.Calibrations.Select((c, i) => (c, i)).Where(x => x.c.TargetId == targetId)
            .OrderByDescending(x => x.c.CreatedUnixMs).ThenByDescending(x => x.i).Select(x => x.c)];

    /// <summary>The score of a projector as it stands: its pins re-projected with its optics and pose (inliers within 6 px).</summary>
    public static QualityScore ScoreAsItStands(Projector pj, string source = "pins")
    {
        var residuals = new double[pj.Pins.Length];
        var inliers = new bool[pj.Pins.Length];
        double sum = 0;
        var n = 0;
        for (var i = 0; i < pj.Pins.Length; i++)
        {
            residuals[i] = PoseSolver.ReprojectionError(new Correspondence(pj.Pins[i].World, pj.Pins[i].Pixel), pj.Intrinsics, pj.Lens, pj.Pose);
            inliers[i] = pj.Pins[i].Enabled && residuals[i] <= 6;
            if (inliers[i])
            {
                sum += residuals[i] * residuals[i];
                n++;
            }
        }
        var rms = n > 0 ? Math.Sqrt(sum / n) : double.PositiveInfinity;
        var solution = new PoseSolution(pj.Intrinsics, pj.Lens, pj.Pose, rms, residuals, inliers, "");
        return CalibrationScoring.ForPins(pj, solution, source).Score;
    }

    /// <summary>How far one result moved the picture from another: the position (mm), the turn (degrees) and the wall (p95 over a
    /// grid of the raster at the pins' median depth, px; −1 when it cannot be told).</summary>
    public static (double Mm, double Deg, double WallPx) Delta(CalibrationSnapshot from, CalibrationSnapshot to)
    {
        if (from.Projector is not { } a || to.Projector is not { } b)
            return (-1, -1, -1);
        var mm = (b.Pose.Position - a.Pose.Position).Length * 1000;
        var deg = (a.Pose.CameraToWorld.Transposed() * b.Pose.CameraToWorld).ToRotationVector().Length * 180 / Math.PI;
        return (mm, deg, WallMove(a, b));
    }

    /// <summary>p95 of how far a grid of the raster moves on the wall from one optics and pose to another, at the pins' median depth.</summary>
    public static double WallMove(Core.Model.ProjectorCalibration a, Core.Model.ProjectorCalibration b)
    {
        var depths = a.Pins.Where(p => p.Enabled).Select(p => a.Pose.WorldToCamera(p.World).Z).Where(z => z > 0).ToArray();
        var depth = depths.Length > 0 ? Quantile(depths, 0.5) : 4.0;
        var k = a.Intrinsics;
        var moves = new List<double>(24 * 14);
        for (var y = 0; y < 14; y++)
            for (var x = 0; x < 24; x++)
            {
                var px = new Vec2d((x + 0.5) / 24 * k.Width, (y + 0.5) / 14 * k.Height);
                var dir = CameraModel.PixelRayCamera(px, k, a.Lens);
                var world = a.Pose.CameraToWorldPoint(dir * (depth / dir.Z));
                if (CameraModel.Project(world, b.Intrinsics, b.Lens, b.Pose) is { } q)
                    moves.Add((q - px).Length);
            }
        return moves.Count == 0 ? -1 : Quantile(moves.ToArray(), 0.95);
    }

    /// <summary>The q-quantile of the values (sorted, nearest rank between neighbours by linear interpolation).</summary>
    private static double Quantile(double[] values, double q)
    {
        Array.Sort(values);
        var at = q * (values.Length - 1);
        var lo = (int)Math.Floor(at);
        var hi = Math.Min(values.Length - 1, lo + 1);
        return values[lo] + (values[hi] - values[lo]) * (at - lo);
    }

    /// <summary>A short name for a new result ("Solve 3", "Re-solve 4").</summary>
    public static string NameFor(Project p, string targetId, CalibrationSource source)
    {
        var n = p.Stage.Calibrations.Count(c => c.TargetId == targetId) + 1;
        var word = source switch
        {
            CalibrationSource.Pins => "Solve",
            CalibrationSource.Scan => "Scan",
            CalibrationSource.Resolve => "Re-solve",
            CalibrationSource.Revert => "Back",
            CalibrationSource.Align => "Align",
            CalibrationSource.Stack => "Stack",
            _ => "Saved",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{word} {n}");
    }
}
