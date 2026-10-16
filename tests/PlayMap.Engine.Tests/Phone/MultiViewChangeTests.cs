using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// A three-view scan changed through the verbs (wp2-multiscan): a fourth view taken with the phone knocked two pixels between its
/// projectors' runs raises the residual, is named as the view that disagrees, and SCAN APPLY's score says so — its dense fit's fix
/// drops that view; SCAN DROP of it brings the residual back under half a pixel and the score's fix no longer names it. Regions are
/// measured per projector and joined: a face both projectors light is one region listing both, and SCAN SURFACE … ON a projector makes
/// a surface of what that projector lights of it.
/// </summary>
public sealed class MultiViewChangeTests(ThreeViewScan scan, ITestOutputHelper output) : IClassFixture<ThreeViewScan>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static ScoreTerm DenseRms(Project p, string projectorId) =>
        p.Stage.Calibrations.Last(c => c.TargetId == projectorId && c.Source == CalibrationSource.Scan).Score.Terms.Single(t => t.Code == ScoreTermCodes.DenseRms);

    [Fact]
    public async Task AKnockedViewRaisesTheDenseFitItsFixDropsItAndDroppingItBringsTheResidualBack()
    {
        var rig = scan.Rig;
        var id = scan.ScanId;
        var clean = scan.ThreeViews.Multi!.Result.RmsPx;
        scan.KnockView = "v4";
        try
        {
            Assert.Equal(id, await MultiViewScanKit.ScanAsync(rig, $"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2} INTO {id}"));
        }
        finally
        {
            scan.KnockView = "";
        }
        var knocked = rig.Scanner.Find(id)!;
        var bad = knocked.Multi!;
        output.WriteLine(bad.Result.Words);
        output.WriteLine(string.Join("; ", bad.Influence.Select(i => string.Create(Inv, $"without {i.ViewId}: {i.RmsWithoutPx:0.000} px"))));
        Assert.True(bad.Result.RmsPx > 10 * clean, string.Create(Inv, $"{bad.Result.RmsPx:0.000} px against {clean:0.000} px"));
        Assert.Equal("v4", bad.Disagreeing);
        var views = JsonDocument.Parse(await rig.Ok("SCAN VIEWS " + id)).RootElement;
        Assert.Equal("v4", views.GetProperty("disagreeing").GetString());

        // SCAN APPLY records each projector's score: the dense fit, the decoded share and the views, each with its words; the knocked
        // view's fit names SCAN DROP as its fix.
        await rig.Ok("SCAN APPLY " + id);
        var p = rig.Engine.Store.Project;
        var snapshot = p.Stage.Calibrations.Last(c => c.TargetId == MultiViewScanKit.Pj2 && c.Source == CalibrationSource.Scan);
        Assert.Equal(id, snapshot.ScanId);
        Assert.Equal("scan", snapshot.Score.Source);
        Assert.Equal([ScoreTermCodes.DenseRms, ScoreTermCodes.Decoded, ScoreTermCodes.Views], snapshot.Score.Terms.Select(t => t.Code));
        Assert.All(snapshot.Score.Terms, t => Assert.False(string.IsNullOrWhiteSpace(t.Words)));
        Assert.Equal("4 views", snapshot.Score.Terms.Single(t => t.Code == ScoreTermCodes.Views).Words);
        var raised = DenseRms(p, MultiViewScanKit.Pj2);
        output.WriteLine($"knocked: {raised.Words} — {raised.Fix}");
        Assert.Equal($"SCAN DROP {id} v4", raised.Fix);
        Assert.Equal("v4", raised.Where);
        await rig.Ok("UNDO");

        // SCAN DROP: the residual falls back under half a pixel, near the clean scan's, and v4 stays in the scan as dropped.
        var dropped = JsonDocument.Parse(await rig.Ok($"SCAN DROP {id} v4")).RootElement;
        var after = rig.Scanner.Find(id)!;
        output.WriteLine(after.Multi!.Result.Words);
        Assert.True(after.Multi.Result.RmsPx < 0.5);
        Assert.True(after.Multi.Result.RmsPx < 2 * clean + 0.01, string.Create(Inv, $"{after.Multi.Result.RmsPx:0.000} px against the clean {clean:0.000} px"));
        Assert.Equal(["v1", "v2", "v3"], after.Multi.ViewIds.ToArray());
        Assert.Equal("dropped", after.Captured.Single(v => v.Id == "v4").State);
        Assert.Equal("dropped", dropped.GetProperty("views").EnumerateArray().Single(v => v.GetProperty("id").GetString() == "v4").GetProperty("state").GetString());
        Assert.Null(after.Multi.Disagreeing);
        // The warm drop keeps the frame: the first view stays where it stood.
        Assert.True((bad.Result.View("v1").Pose.Position - after.Multi.Result.View("v1").Pose.Position).Length < 1e-9);
        Assert.True(MultiViewScanKit.AngleDeg(bad.Result.View("v1").Pose, after.Multi.Result.View("v1").Pose) < 1e-6);

        await rig.Ok("SCAN APPLY " + id);
        var lowered = DenseRms(rig.Engine.Store.Project, MultiViewScanKit.Pj2);
        output.WriteLine($"dropped: {lowered.Words} — {lowered.Fix}");
        Assert.True(lowered.Value < raised.Value, $"{lowered.Value} against {raised.Value}");
        Assert.DoesNotContain("SCAN DROP", lowered.Fix, StringComparison.Ordinal);
        await rig.Ok("UNDO");

        // A dropped view cannot be dropped again; nor can the last views go.
        var again = await rig.Run($"SCAN DROP {id} v4");
        Assert.False(again.Ok);
        Assert.Contains("dropped already", again.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFaceBothProjectorsLightIsOneRegionAndOnMakesASurfaceOfWhatOneProjectorLights()
    {
        var rig = scan.Rig;
        var outcome = scan.ThreeViews;
        var multi = outcome.Multi!;
        // The wall is lit by both projectors: one region, both listed, measured flat.
        var both = multi.Regions.Where(r => r.Projectors.Length == 2 && MultiViewScan.Cells(r) >= MultiViewScan.MinRegionCells).ToList();
        Assert.NotEmpty(both);
        var wall = both.Where(r => r.Planar).MaxBy(MultiViewScan.Cells)!;
        Assert.Equal([MultiViewScanKit.Pj1, MultiViewScanKit.Pj2], wall.Projectors.ToArray());
        Assert.Equal("wall", MultiViewScan.Kind(wall.Normal));
        // Each surface once: no other region overlaps the wall's place.
        Assert.Single(multi.Regions, r => r.Planar && MultiViewScan.Kind(r.Normal) == "wall" && Math.Abs(r.Centroid.Z - wall.Centroid.Z) < 0.05
            && MultiViewScan.Cells(r) >= MultiViewScan.MinRegionCells);

        // STATE lists it with every projector that lights it.
        var regions = scan.StateAfter.GetProperty("scan").GetProperty("regions").EnumerateArray().ToList();
        var row = regions.Single(r => r.GetProperty("id").GetString() == wall.Id);
        Assert.Equal([MultiViewScanKit.Pj1, MultiViewScanKit.Pj2], MultiViewScanKit.Ids(row.GetProperty("projectors")));
        Assert.Contains(row.GetProperty("projector").GetString(), wall.Projectors);

        // The surface ON pj2 holds the wall's faces Right lights; the one without ON all of the wall's; Right's are fewer, and each is
        // lit by Right (its centre in Right's picture).
        var all = MultiViewScan.Faces(multi, wall, null);
        var right = MultiViewScan.Faces(multi, wall, MultiViewScanKit.Pj2);
        Assert.NotEmpty(right);
        Assert.True(right.Length < all.Length, $"{right.Length} of {all.Length}");
        Assert.Subset(all.ToHashSet(), right.ToHashSet());
        var device = multi.Result.Projector(MultiViewScanKit.Pj2).View;
        var mesh = multi.Merged.Mesh;
        Assert.All(right, t =>
        {
            var (a, b, c) = mesh.Triangle(t);
            var q = device.Project((mesh.Position(a) + mesh.Position(b) + mesh.Position(c)) / 3);
            Assert.NotNull(q);
            Assert.InRange(q.Value.X, 0, device.Intrinsics.Width);
            Assert.InRange(q.Value.Y, 0, device.Intrinsics.Height);
        });

        // Made through the verb, on the scan as it stands now (another test may have solved it again): faces of the mesh the stage holds.
        var current = rig.Scanner.Find(scan.ScanId)!.Multi!;
        var wallNow = current.Regions.Where(r => r.Planar && r.Projectors.Length == 2 && MultiViewScan.Kind(r.Normal) == "wall").MaxBy(MultiViewScan.Cells)!;
        var made = await rig.Run($"SCAN SURFACE {scan.ScanId} REGION {wallNow.Id} ON {MultiViewScanKit.Pj2} Right wall");
        Assert.True(made.Ok, made.Payload);
        var p = rig.Engine.Store.Project;
        var surface = p.Stage.Surfaces.Single(s => s.Id == made.Payload);
        Assert.Equal("Right wall", surface.Name);
        Assert.Contains("scan", surface.Tags);
        Assert.Equal(MultiViewScan.Faces(current, wallNow, MultiViewScanKit.Pj2), surface.Faces.ToArray());
        Assert.Equal(p.Stage.Scans.Single().MeshId, surface.MeshId);
        Assert.Equal(current.Merged.Mesh.TriangleCount, p.FindMesh(surface.MeshId)!.Triangles);

        var refused = await rig.Run($"SCAN SURFACE {scan.ScanId} REGION rg999 ON {MultiViewScanKit.Pj2}");
        Assert.False(refused.Ok);
        Assert.Contains("has no region rg999", refused.Payload, StringComparison.Ordinal);
        await rig.Ok("UNDO");
    }
}
