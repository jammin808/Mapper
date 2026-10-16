using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Align;

/// <summary>ALIGN SCORE (autoalign M15, the terms this round): each term tripped by a simulator condition built for it — corners clicked
/// past what the outputs light (coverage), a projector that drifts 2 px after the scan, seen by the verify pass's dots (seam), and the
/// same in a stack (stack convergence); a clean run carries no penalty; every fix line is a catalogue line, and the coverage fix runs.</summary>
public sealed class AlignScoreTests(ITestOutputHelper log)
{
    private static JsonElement Term(JsonElement score, string code) =>
        score.GetProperty("terms").EnumerateArray().Single(t => t.GetProperty("code").GetString() == code);

    private static void FixesParse(JsonElement score)
    {
        foreach (var t in score.GetProperty("terms").EnumerateArray())
        {
            var fix = t.GetProperty("fix").GetString()!;
            if (fix.Length == 0)
                continue;
            Assert.True(WireGrammar.TryParse(fix, out var line, out var error), $"{fix}: {error?.Reason}");
            Assert.True(VerbCatalog.Match(line) is not null, $"{fix} is not a catalogue line");
        }
    }

    private async Task<JsonElement> Scored(AlignRig rig, string group, string run)
    {
        var score = AlignRig.Json(await rig.Ok($"ALIGN SCORE {run}"));
        log.WriteLine(score.GetProperty("words").GetString());
        foreach (var t in score.GetProperty("terms").EnumerateArray())
            log.WriteLine($"  {t.GetProperty("code").GetString()}: {t.GetProperty("words").GetString()} — {t.GetProperty("penalty").GetDouble():0.0} · {t.GetProperty("fix").GetString()}");
        FixesParse(score);
        // The group's name answers the same run while it is staged.
        Assert.Equal(run, AlignRig.Json(await rig.Ok($"ALIGN SCORE {group}")).GetProperty("run").GetString());
        return score;
    }

    [Fact]
    public async Task ACleanRunCarriesNoPenaltyAndItsVerifyPassSeesTheSeams()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, "flat-screen:tile2x1"));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        var score = await Scored(rig, group, run);
        Assert.Contains("verified: the seams agree", rig.Runner.View!.Words, StringComparison.Ordinal);
        Assert.Equal(0, Term(score, "seam").GetProperty("penalty").GetDouble());
        Assert.True(Term(score, "seam").GetProperty("value").GetDouble() <= 0.5);
        Assert.Contains("no run or alignment group", await rig.Refused("ALIGN SCORE nothing"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectorThatDriftsAfterTheScanTripsTheSeamTerm()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, "flat-screen:tile2x1:drift2=2"));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        var seam = Term(await Scored(rig, group, run), "seam");
        Assert.InRange(seam.GetProperty("value").GetDouble(), 1.2, 3.5);
        Assert.True(seam.GetProperty("penalty").GetDouble() > 10);
        Assert.Equal($"ALIGN RUN {group}", seam.GetProperty("fix").GetString());
    }

    [Fact]
    public async Task AStackMemberThatDriftsTripsTheStackTerm()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, "flat-screen:stack2:drift2=2"));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        var score = await Scored(rig, group, run);
        var stack = Term(score, "stack");
        Assert.InRange(stack.GetProperty("value").GetDouble(), 1.2, 3.5);
        Assert.True(stack.GetProperty("penalty").GetDouble() > 10);
        Assert.DoesNotContain(score.GetProperty("terms").EnumerateArray(), t => t.GetProperty("code").GetString() == "seam");
    }

    [Fact]
    public async Task CornersPastWhatTheOutputsLightTripTheCoverageTermAndItsFixRuns()
    {
        const string layout = "flat-screen:tile2x1";
        var sim = AlignRig.Truth(2, layout);
        // A rectangle wider and taller than the two tiles cover (3.6 m × 1.1 m), clicked on the camera picture.
        var screen = new[] { new Vec2d(-2.0, -0.75), new Vec2d(2.0, -0.75), new Vec2d(2.0, 0.75), new Vec2d(-2.0, 0.75) };
        var clicks = screen.Select(s => sim.ScreenToCamera(new Vec3d(s.X, -s.Y, 0))!.Value).Select(c => new Vec2d(c.X / sim.CameraWidth, c.Y / sim.CameraHeight)).ToList();
        await using var rig = new AlignRig(AlignRig.Project(2, layout));
        var group = await rig.NewGroup();
        await rig.Ok($"ALIGN CORNERS {group} " + string.Join(' ', clicks.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.X:0.000000} {c.Y:0.000000}"))));
        await rig.Ok(string.Create(CultureInfo.InvariantCulture, $"SET {group} target.aspect {4.0 / 1.5:R}"));
        var run = await rig.RunToEnd(group, "FIT CORNERS");
        var coverage = Term(await Scored(rig, group, run), "coverage");
        Assert.True(coverage.GetProperty("value").GetDouble() < 90, coverage.ToString());
        Assert.True(coverage.GetProperty("penalty").GetDouble() > 5);
        var fix = coverage.GetProperty("fix").GetString()!;
        Assert.Equal($"ALIGN CORNERS {group} CLEAR", fix);
        await rig.Ok(fix);
    }
}
