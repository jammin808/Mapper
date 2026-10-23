using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>A far projector solved from pins on a facade, as PROJECTOR SOLVE and CALIBRATION RESOLVE run it.</summary>
internal static class FarProjector
{
    public const string Id = "pj00000001";

    public static readonly Intrinsics Truth = Intrinsics.FromThrowRatio(1920, 1080, 1.8);

    public static readonly Pose Pose = Pose.LookAt(new Vec3d(0.3, 2.2, 20), new Vec3d(0, 2, 0), Vec3d.UnitY);

    /// <summary>Points on the flat front (a 3 × 3 grid across the picture), and, with <paramref name="deep"/>, six more standing out
    /// of it by 2 to 6 m (a canopy, a tower): the front alone cannot tell zoom from distance, the deep points can.</summary>
    public static IReadOnlyList<Vec3d> Points(bool deep)
    {
        var points = new List<Vec3d>();
        for (var j = 0; j < 3; j++)
            for (var i = 0; i < 3; i++)
                points.Add(new Vec3d(-4 + 4 * i, 0.5 + 1.5 * j, 0));
        if (deep)
        {
            points.Add(new Vec3d(-3.5, 0.8, 2));
            points.Add(new Vec3d(3.2, 1.2, 3));
            points.Add(new Vec3d(-2.5, 3.6, 4));
            points.Add(new Vec3d(2.6, 3.3, 6));
            points.Add(new Vec3d(0.4, 0.6, 5));
            points.Add(new Vec3d(-0.8, 3.9, 2.5));
        }
        return points;
    }

    /// <summary>Each point's pin, its pixel where the true optics put it.</summary>
    public static IEnumerable<CalibrationPin> Pins(IReadOnlyList<Vec3d> points, int first = 0) =>
        points.Select((w, i) => new CalibrationPin("pn" + (first + i + 1).ToString("00000000", CultureInfo.InvariantCulture), w,
            CameraModel.Project(w, Truth, LensDistortion.None, Pose)!.Value, true));

    /// <summary>The projector modelled with the given optics and the pins of <paramref name="deep"/>'s points, on an empty project.</summary>
    public static Project Project(Intrinsics optics, bool deep)
    {
        var pj = new Projector(Id, "Far", optics, LensDistortion.None, Pose, 20_000, "#FFFFFF", false, [.. Pins(Points(deep))],
            SolveLocks.Shift | SolveLocks.Distortion, 0);
        var p = Core.Model.Project.CreateDefault("Facade");
        return p.WithStage(st => st with { Projectors = [pj] });
    }
}

/// <summary>Who put a projector's focal lock there (<see cref="Projector.FocalLock"/>): the long-throw solve's lock from the lens is
/// recorded as the lens's and asked again at the next solve, and let go when the pins carry the zoom; the operator's LOCK FOCAL holds at
/// every later solve and re-solve even where its focal sits exactly at the lens prior; a SET of the locks (the desk's buttons) makes the
/// lock the operator's; the ledger puts it back with the result.</summary>
public sealed class FocalLockProvenanceTests
{
    private static Projector Far(SiteRig rig) => rig.Engine.Store.Project.FindProjector(FarProjector.Id)!;

    [Fact]
    public async Task TheLensLockIsAskedAgainAndLetGoWhenThePinsCarryTheZoom()
    {
        await using var rig = new SiteRig(FarProjector.Project(FarProjector.Truth with { Fx = FarProjector.Truth.Fx * 1.04, Fy = FarProjector.Truth.Fy * 1.04 }, deep: false));
        await rig.Ok($"PROJECTOR PRIOR {FarProjector.Id} THROW 1.8 ±2");
        var first = SiteRig.Json(await rig.Ok($"PROJECTOR SOLVE {FarProjector.Id}"));
        Assert.Equal(["focal"], first.GetProperty("locked").EnumerateArray().Select(x => x.GetString()));
        Assert.True((Far(rig).Locks & SolveLocks.Focal) != 0);
        Assert.Equal(FocalLockSource.Lens, Far(rig).FocalLock);
        var lensResult = first.GetProperty("snapshot").GetString()!;
        Assert.Equal(FocalLockSource.Lens, rig.Engine.Store.Project.FindCalibration(lensResult)!.Projector!.FocalLock);

        // Pins standing out of the front: the next solve asks the lens's lock again, and the pins now carry the zoom.
        foreach (var pin in FarProjector.Pins(FarProjector.Points(deep: true).Skip(9).ToList(), first: 100))
            await rig.Ok(string.Create(CultureInfo.InvariantCulture,
                $"ADD pin {FarProjector.Id} {{\"world\":{{\"x\":{pin.World.X:R},\"y\":{pin.World.Y:R},\"z\":{pin.World.Z:R}}},\"pixel\":{{\"x\":{pin.Pixel.X:R},\"y\":{pin.Pixel.Y:R}}},\"enabled\":true}}"));
        var second = SiteRig.Json(await rig.Ok($"PROJECTOR SOLVE {FarProjector.Id}"));
        Assert.Equal(0, second.GetProperty("locked").GetArrayLength());
        Assert.Equal(SolveLocks.None, Far(rig).Locks & SolveLocks.Focal);
        Assert.Equal(FocalLockSource.Operator, Far(rig).FocalLock);
        Assert.Equal(FarProjector.Truth.Fx, Far(rig).Intrinsics.Fx, FarProjector.Truth.Fx * 1e-4);

        // The ledger puts the lock back with its result, and who put it there with it.
        await rig.Ok($"CALIBRATION RECALL {lensResult}");
        Assert.True((Far(rig).Locks & SolveLocks.Focal) != 0);
        Assert.Equal(FocalLockSource.Lens, Far(rig).FocalLock);
    }

    [Fact]
    public async Task AnOperatorsFocalLockAtTheLensPriorHoldsThroughEverySolve()
    {
        // The projector's focal is the lens prior's exactly (the throw the operator typed), and the pins could carry the zoom.
        await using var rig = new SiteRig(FarProjector.Project(FarProjector.Truth, deep: true));
        await rig.Ok($"PROJECTOR PRIOR {FarProjector.Id} THROW 1.8 ±2");
        Assert.Equal(1.8 * 1920, Far(rig).Intrinsics.Fx, 1e-9);
        var locked = SiteRig.Json(await rig.Ok($"PROJECTOR SOLVE {FarProjector.Id} LOCK SHIFT FOCAL DISTORTION"));
        Assert.Equal(0, locked.GetProperty("locked").GetArrayLength());
        Assert.True((Far(rig).Locks & SolveLocks.Focal) != 0);
        Assert.Equal(FocalLockSource.Operator, Far(rig).FocalLock);

        // A solve with no LOCK word, and a re-solve from the pins: the operator's lock is not asked again.
        await rig.Ok($"PROJECTOR SOLVE {FarProjector.Id}");
        Assert.True((Far(rig).Locks & SolveLocks.Focal) != 0, "PROJECTOR SOLVE let the operator's lock go");
        Assert.Equal(FocalLockSource.Operator, Far(rig).FocalLock);
        Assert.Equal(1.8 * 1920, Far(rig).Intrinsics.Fx, 1e-9);
        await rig.Ok($"CALIBRATION RESOLVE {FarProjector.Id}");
        Assert.True((Far(rig).Locks & SolveLocks.Focal) != 0, "CALIBRATION RESOLVE let the operator's lock go");
        Assert.Equal(FocalLockSource.Operator, Far(rig).FocalLock);
    }

    [Fact]
    public async Task ASetOfTheLocksMakesTheFocalLockTheOperators()
    {
        await using var rig = new SiteRig(FarProjector.Project(FarProjector.Truth with { Fx = FarProjector.Truth.Fx * 1.04, Fy = FarProjector.Truth.Fy * 1.04 }, deep: false));
        await rig.Ok($"PROJECTOR PRIOR {FarProjector.Id} THROW 1.8 ±2");
        await rig.Ok($"PROJECTOR SOLVE {FarProjector.Id}");
        Assert.Equal(FocalLockSource.Lens, Far(rig).FocalLock);
        await rig.Ok($"SET {FarProjector.Id} locks {ProjectJson.SerializeEntity(SolveLocks.Focal)}");
        Assert.Equal((SolveLocks.Focal, FocalLockSource.Operator), (Far(rig).Locks, Far(rig).FocalLock));
        // Naming who holds it in the same field set is kept: the file's own word.
        await rig.Ok($"SET {FarProjector.Id} focalLock \"lens\"");
        Assert.Equal(FocalLockSource.Lens, Far(rig).FocalLock);
    }
}

/// <summary>PROJECTOR SOLVE holds the control lane only to read the projector and to apply its result: the solve and its score (one
/// uncertainty analysis, the long-throw solve's, which the score takes as it is) run off it. The lane's 10 ms slice is a budget on real
/// work, measured as the longest a line waits for the lane while solves run beside it, on the lane itself (<see cref="LaneTimes"/>):
/// the regular test records it and holds it to its 4× ceiling; the Perf twin, run with the Perf tests at integration, holds it to
/// 10 ms. Both run in the serial collection, after the parallel tests. A projector changed while it is solved is not overwritten.</summary>
[Collection(SurveyPerfCollection.Name)]
public sealed class ProjectorSolveLaneTests(ITestOutputHelper log)
{
    /// <summary>The longest a verb may hold the control lane in one slice (ms).</summary>
    internal const double LaneSliceBudgetMs = 10;

    /// <summary>The longest the lane kept a line waiting during each of seven PROJECTOR SOLVEs of a far projector with fifteen pins and
    /// its lens prior (after one that warms the code): their median and the worst (ms), each wait read on the lane
    /// (<see cref="LaneProbe"/>), never with the test's own continuation in it.</summary>
    internal static async Task<(double Median, double Worst)> LaneTimes(ITestOutputHelper log)
    {
        await using var rig = new SiteRig(FarProjector.Project(FarProjector.Truth with { Fx = FarProjector.Truth.Fx * 1.02, Fy = FarProjector.Truth.Fy * 1.02 }, deep: true));
        await rig.Ok($"PROJECTOR PRIOR {FarProjector.Id} THROW 1.8 ±2");
        await rig.Ok($"PROJECTOR SOLVE {FarProjector.Id}");
        var held = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var probe = new LaneProbe(rig.Engine.Lane);
            var solve = rig.Ok($"PROJECTOR SOLVE {FarProjector.Id}");
            var worst = 0.0;
            while (!solve.IsCompleted)
                worst = Math.Max(worst, await probe.WaitedMsAsync());
            await solve;
            held.Add(worst);
        }
        held.Sort();
        var (median, most) = (held[held.Count / 2], held[^1]);
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"the lane during PROJECTOR SOLVE: median {median:0.00} ms, worst {most:0.00} ms over {held.Count} solves"));
        return (median, most);
    }

    [Fact]
    public async Task ASolveHoldsTheLaneWithinFourTimesItsSlice()
    {
        var (median, _) = await LaneTimes(log);
        Assert.True(median <= 4 * LaneSliceBudgetMs, string.Create(CultureInfo.InvariantCulture, $"PROJECTOR SOLVE held the lane {median:0.00} ms"));
    }

    [Fact]
    public async Task AProjectorChangedWhileItIsSolvedIsNotOverwritten()
    {
        await using var rig = new SiteRig(FarProjector.Project(FarProjector.Truth with { Fx = FarProjector.Truth.Fx * 1.02, Fy = FarProjector.Truth.Fy * 1.02 }, deep: true));
        // The lane is held while the solve is under way off it: a pin moved before its result lands refuses the result.
        var gate = new TaskCompletionSource();
        var held = rig.Engine.Lane.RunAsync(() => gate.Task.Wait());
        var solve = rig.Run($"PROJECTOR SOLVE {FarProjector.Id}");
        var pin = rig.Engine.Store.Project.FindProjector(FarProjector.Id)!.Pins[0];
        var moved = rig.Run(string.Create(CultureInfo.InvariantCulture, $"SET {pin.Id} pixel {{\"x\":{pin.Pixel.X + 3:R},\"y\":{pin.Pixel.Y:R}}}"));
        gate.SetResult();
        await held;
        Assert.True((await moved).Ok);
        var r = await solve;
        Assert.False(r.Ok, r.Payload);
        Assert.Contains("was changed while it was being solved", r.Payload, StringComparison.Ordinal);
        Assert.Null(rig.Engine.Store.Project.FindProjector(FarProjector.Id)!.CalibrationId);
    }

    [Fact]
    public void TheScoreTakesTheSolvesOwnUncertaintyAnalysis()
    {
        var pj = FarProjector.Project(FarProjector.Truth with { Fx = FarProjector.Truth.Fx * 1.02, Fy = FarProjector.Truth.Fy * 1.02 }, deep: true)
            .Stage.Projectors[0] with { Priors = SolvePriors.None with { ThrowRatio = 1.8, ThrowRatioSigmaPct = 2 } };
        var solve = Core.Calibration.LongThrowSolve.Solve(pj, Core.Calibration.PoseSolveOptions.Default with { Locks = pj.Locks, InitialPose = pj.Pose }, retestLock: true);
        Assert.NotNull(solve.Analysis);
        var solved = pj with { Intrinsics = solve.Solution.Intrinsics, Lens = solve.Solution.Lens, Pose = solve.Solution.Pose, Locks = solve.LocksUsed };
        // The analysis the solve made is the one the score would make: the same uncertainty, the same confidence.
        var (fresh, freshU) = Core.Calibration.Scoring.CalibrationScoring.ForPins(solved, solve.Solution);
        var (reused, reusedU) = Core.Calibration.Scoring.CalibrationScoring.ForPins(solved, solve.Solution, analysed: solve.Analysis);
        Assert.Equal(freshU, reusedU);
        Assert.Equal(fresh.Confidence, reused.Confidence);
        Assert.Equal(fresh.Value, reused.Value);
        Assert.Equal(fresh.Words, reused.Words);
        Assert.Equal(freshU!.WallPx, solve.Uncertainty!.WallPx);
    }
}

/// <summary>PROJECTOR SOLVE's lane budget, run serially at integration: 10 ms in one slice.</summary>
[Trait("Category", "Perf")]
[Collection(SurveyPerfCollection.Name)]
public sealed class ProjectorSolveLanePerfTests(ITestOutputHelper log)
{
    [Fact]
    public async Task ASolveHoldsTheLaneWithinItsSlice()
    {
        var (median, _) = await ProjectorSolveLaneTests.LaneTimes(log);
        Assert.True(median <= ProjectorSolveLaneTests.LaneSliceBudgetMs,
            string.Create(CultureInfo.InvariantCulture, $"PROJECTOR SOLVE held the lane {median:0.00} ms"));
    }
}

