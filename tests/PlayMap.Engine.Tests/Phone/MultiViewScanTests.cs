using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>The three-view scan the tests share: SCAN START, then two views INTO it, on the multi-view scene, through one engine. The
/// outcome after each view is kept as it was (immutable), so a test that changes the scan later reads the three-view scan as made.</summary>
public sealed class ThreeViewScan : IAsyncLifetime
{
    internal OpsRig Rig { get; private set; } = null!;

    public string ScanId { get; private set; } = "";

    /// <summary>The scan after its first view (single-view solve) and after the third (all three solved together).</summary>
    public ScanOutcome OneView { get; private set; } = null!;

    public ScanOutcome ThreeViews { get; private set; } = null!;

    public JsonElement StateAfter { get; private set; }

    /// <summary>The view whose run of the second projector is taken with the phone knocked by <see cref="KnockPx"/> (none by default).</summary>
    public string KnockView { get; set; } = "";

    public double KnockPx { get; set; } = 2;

    public async Task InitializeAsync()
    {
        Rig = new OpsRig(project: MultiViewScanKit.Project(), scan: new ScanRunnerOptions
        {
            SimulatedNudge = (view, projector, camera) => view == KnockView && projector == MultiViewScanKit.Pj2 ? MultiViewScanKit.Knocked(camera, KnockPx) : camera,
        });
        var line = $"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2}";
        ScanId = await MultiViewScanKit.ScanAsync(Rig, line);
        OneView = Rig.Scanner.Find(ScanId)!;
        Assert.Equal(ScanId, await MultiViewScanKit.ScanAsync(Rig, line + " INTO " + ScanId));
        Assert.Equal(ScanId, await MultiViewScanKit.ScanAsync(Rig, line + " INTO " + ScanId));
        ThreeViews = Rig.Scanner.Find(ScanId)!;
        StateAfter = Rig.State();
    }

    public async Task DisposeAsync()
    {
        await Rig.DisposeAsync();
        Rig.DeleteFolder();
    }
}

/// <summary>
/// Many views into one scan through the engine (wp2-multiscan): a scan's first view solved on its own, two more views added with
/// SCAN START … INTO and every view solved together through Core's projector-anchored solve — every projector within Core's multi-view
/// bar (5 mm, 0.05°, 0.2 % of throw) and the phone's focal length within 0.5 %; coverage per projector; the views in STATE and SCAN
/// VIEWS; the frame and scale carried from the first view; SCAN SOLVE refusing a solve that would change nothing.
/// </summary>
public sealed class MultiViewScanTests(ThreeViewScan scan, ITestOutputHelper output) : IClassFixture<ThreeViewScan>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void ThreeViewsIntoOneScanPlaceEveryProjectorWithinTheMultiViewBar()
    {
        var multi = scan.ThreeViews.Multi;
        Assert.NotNull(multi);
        var result = multi.Result;
        Assert.Equal(["v1", "v2", "v3"], result.Views.Select(v => v.Id));
        Assert.Equal([MultiViewScanKit.Pj1, MultiViewScanKit.Pj2], result.Projectors.Select(p => p.Id));
        output.WriteLine(result.Words);
        var toWorld = MultiViewScanKit.Registration(result);
        foreach (var p in result.Projectors)
        {
            var (position, angle, throwError) = MultiViewScanKit.Errors(p, toWorld);
            output.WriteLine(string.Create(Inv, $"{p.Id}: {position * 1000:0.00} mm, {angle:0.0000}°, throw {throwError:P3}"));
            Assert.True(position < 0.005, $"{p.Id} centre {position * 1000:0.00} mm");
            Assert.True(angle < 0.05, $"{p.Id} orientation {angle:0.0000}°");
            Assert.True(throwError < 0.002, $"{p.Id} throw {throwError:P3}");
        }
        var phone = result.Rig(MultiViewScanKit.Camera);
        var focal = Math.Abs(phone.Intrinsics.Fx / MultiViewScanKit.Scene.Phone.Fx - 1);
        output.WriteLine(string.Create(Inv, $"phone focal {focal:P3} (prior 8 % off)"));
        Assert.True(focal < 0.005, $"phone focal {focal:P3}");
        Assert.True(result.RmsPx < 0.5, $"{result.RmsPx:0.000} px");
    }

    [Fact]
    public void TheJointSolveKeepsTheFirstViewsFrameAndScale()
    {
        // The first view's own solve placed the camera and its projectors; the joint solve is carried onto that frame: the first view
        // stays where it stood, and its distance to the projectors is the one the first solve measured.
        var first = scan.OneView.Captured.Single();
        Assert.NotNull(first.Pose);
        Assert.True(first.Baseline > 0);
        var v1 = scan.ThreeViews.Multi!.Result.View("v1");
        Assert.True((v1.Pose.Position - first.Pose!.Position).Length < 1e-6, "v1 stays where the first solve put it");
        Assert.True(MultiViewScanKit.AngleDeg(v1.Pose, first.Pose) < 1e-6);
        var again = scan.ThreeViews.Captured.Single(v => v.Id == "v1");
        Assert.Equal(first.Baseline, again.Baseline, 9);
        // The solution SCAN APPLY reads is the joint one: its camera is the first view's, its mesh the merged one.
        var solution = scan.ThreeViews.Solution;
        Assert.Equal("multi-view", solution.Method);
        Assert.Equal(v1.Pose, solution.Camera.Pose);
        Assert.Same(scan.ThreeViews.Multi!.Merged.Mesh, solution.Mesh);
        Assert.All(solution.Projectors, p => Assert.True(p.Solved, p.Words));
    }

    [Fact]
    public void CoverageGrowsWithViewsAndStateShowsEveryView()
    {
        var coverage = scan.ThreeViews.Multi!.Coverage;
        Assert.Equal([MultiViewScanKit.Pj1, MultiViewScanKit.Pj2], coverage.Select(c => c.ProjectorId));
        foreach (var c in coverage)
        {
            output.WriteLine(string.Create(Inv, $"{c.ProjectorId}: seen {c.Seen:P1}, twice {c.Twice:P1}"));
            Assert.True(c.Seen > 0.9, $"{c.ProjectorId} seen {c.Seen:P1}");
        }
        // One view alone sees less of each projector than the three together.
        foreach (var p in new[] { MultiViewScanKit.Pj1, MultiViewScanKit.Pj2 })
        {
            var alone = Core.Scan.MultiView.CoverageMap.Build(p, [scan.OneView.Captured.Single().Maps[p]]);
            Assert.True(alone.Seen < coverage.Single(c => c.ProjectorId == p).Seen, $"{p} alone {alone.Seen:P1}");
        }

        var s = scan.StateAfter.GetProperty("scan");
        Assert.Equal(scan.ScanId, s.GetProperty("id").GetString());
        Assert.Equal("done", s.GetProperty("state").GetString());
        Assert.Equal("v3", s.GetProperty("view").GetString());
        var views = s.GetProperty("views").EnumerateArray().ToList();
        Assert.Equal(["v1", "v2", "v3"], views.Select(v => v.GetProperty("id").GetString()));
        Assert.All(views, v =>
        {
            Assert.Equal(MultiViewScanKit.Camera, v.GetProperty("camera").GetString());
            Assert.Equal([MultiViewScanKit.Pj1, MultiViewScanKit.Pj2], MultiViewScanKit.Ids(v.GetProperty("projectors")));
            Assert.Equal("done", v.GetProperty("state").GetString());
            Assert.Equal("simulated", v.GetProperty("pairing").GetString());
            Assert.True(v.GetProperty("tracks").GetInt32() > 100);
            Assert.InRange(v.GetProperty("rmsPx").GetDouble(), 0, 0.5);
        });
        var rows = s.GetProperty("coverage").EnumerateArray().ToList();
        Assert.Equal(coverage.Select(c => c.ProjectorId), rows.Select(r => r.GetProperty("projector").GetString()));
        Assert.Equal(coverage.Select(c => Math.Round(c.Seen, 4)), rows.Select(r => r.GetProperty("seen").GetDouble()));
        // The record is the scan's one row, updated in place.
        var record = Assert.Single(scan.Rig.Engine.Store.Project.Stage.Scans);
        Assert.Equal(scan.ScanId, record.Id);
        Assert.Null(record.MeshId);
    }

    [Fact]
    public async Task ScanViewsAnswersTheViewsAndASolveThatWouldChangeNothingIsRefused()
    {
        var answer = JsonDocument.Parse(await scan.Rig.Ok("SCAN VIEWS " + scan.ScanId)).RootElement;
        Assert.Equal(scan.ScanId, answer.GetProperty("scan").GetString());
        Assert.Equal(3, answer.GetProperty("views").GetArrayLength());
        Assert.Equal(2, answer.GetProperty("coverage").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("disagreeing").ValueKind);

        var refused = await scan.Rig.Run("SCAN SOLVE " + scan.ScanId);
        Assert.False(refused.Ok);
        Assert.StartsWith("nothing would change: every view of", refused.Payload, StringComparison.Ordinal);
        foreach (var (line, words) in new[]
        {
            ("SCAN SOLVE sc00000000", "no scan sc00000000"),
            ($"SCAN SOLVE {scan.ScanId} HOLD", "SCAN SOLVE <scan> [HOLD <projector>…]"),
            ($"SCAN SOLVE {scan.ScanId} HOLD pj0000ffff", "no projector pj0000ffff"),
            ($"SCAN DROP {scan.ScanId} v9", "has no view v9"),
            ("SCAN VIEWS sc00000000", "no scan sc00000000"),
        })
        {
            var r = await scan.Rig.Run(line);
            Assert.False(r.Ok, line);
            Assert.Contains(words, r.Payload, StringComparison.Ordinal);
        }
    }
}
