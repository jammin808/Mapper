using System.Globalization;
using System.Text;
using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Tests.Model;

namespace PlayMap.Core.Tests.Facade;

/// <summary>With no priors the pose solver gives bit-identical results to the solver before priors existed: Golden/posesolver.txt
/// holds every number of eight seeded solves (round-trip formatted), written by this test from the solver as it stood before the
/// prior rows were added (PLAYMAP_WRITE_FIXTURES=1 rewrites it).</summary>
public sealed class PoseSolverGoldenTests
{
    private static string GoldenPath() => Path.Combine(RoundTwoSample.RepoRoot(), "tests", "PlayMap.Core.Tests", "Facade", "Golden", "posesolver.txt");

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Solve(string name, int pins, double relief, double oblique, SolveLocks locks, ulong seed, bool robust = true)
    {
        var (k, pose) = LongThrowScene.Projector(oblique == 0 ? 100 : 60, 3, oblique);
        var pairs = LongThrowScene.Pins(k, pose, pins, relief, 1, seed);
        var prior = Intrinsics.FromThrowRatio(k.Width, k.Height, 3.1);
        var s = PoseSolver.Solve(pairs, prior, LensDistortion.None, PoseSolveOptions.Default with { Locks = locks, Seed = seed, Robust = robust });
        var sb = new StringBuilder();
        sb.Append(name).Append(' ').Append(R(s.RmsPx)).Append(' ').Append(R(s.Intrinsics.Fx)).Append(' ').Append(R(s.Intrinsics.Fy))
            .Append(' ').Append(R(s.Intrinsics.Cx)).Append(' ').Append(R(s.Intrinsics.Cy)).Append(' ').Append(R(s.Lens.K1)).Append(' ').Append(R(s.Lens.K2))
            .Append(' ').Append(R(s.Pose.Position.X)).Append(' ').Append(R(s.Pose.Position.Y)).Append(' ').Append(R(s.Pose.Position.Z))
            .Append(' ').Append(R(s.Pose.Rotation.X)).Append(' ').Append(R(s.Pose.Rotation.Y)).Append(' ').Append(R(s.Pose.Rotation.Z))
            .Append(' ').Append(string.Join(',', s.Inliers.Select(x => x ? '1' : '0')));
        return sb.ToString();
    }

    private static string All() => string.Join('\n',
        Solve("relief12-shiftlocked", 12, 2, 0, SolveLocks.Shift, 1),
        Solve("relief8-free", 8, 2, 0, SolveLocks.None, 2),
        Solve("oblique12", 12, 2, 25, SolveLocks.Shift, 3),
        Solve("dense40-distortion", 40, 3, 15, SolveLocks.None, 4),
        Solve("flat6-focallocked", 6, 0, 0, SolveLocks.Focal | SolveLocks.Shift, 5),
        Solve("flat12-free", 12, 0, 10, SolveLocks.None, 6),
        Solve("relief20-nonrobust", 20, 2, 5, SolveLocks.Shift, 7, robust: false),
        Solve("relief5-locked", 5, 2, 0, SolveLocks.Focal | SolveLocks.Shift | SolveLocks.Distortion, 8)) + "\n";

    [Fact]
    public void WithoutPriorsTheSolverIsBitIdenticalToItsGolden()
    {
        var now = All();
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            File.WriteAllText(GoldenPath(), now);
        Assert.Equal(File.ReadAllText(GoldenPath()).ReplaceLineEndings("\n"), now);
    }
}
