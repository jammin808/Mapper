using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// Scans survive a restart (wp2-multiscan): a scan's views (each projector's decode), its solve and its depth maps are written under
/// scans/&lt;id&gt;/; after the node stops and a new one starts on the same project folder, SCAN START … INTO the scan adds a view and solves
/// every view together, SCAN SURFACE … ON works on it, SCAN SOLVE … HOLD keeps the held projectors exactly at their stage poses, and the
/// preflight row says whether a scan the stage depends on is still on disk (amber with the line that scans again when it is not).
/// </summary>
public sealed class ScanPersistenceTests(ITestOutputHelper output)
{
    private static string Line(string? into = null) =>
        $"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2}" + (into is null ? "" : " INTO " + into);

    private static void CopyFolder(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
    }

    [Fact]
    public async Task AScanTakesAViewAfterARestartAndHoldsWhatItHolds()
    {
        // The first node: one view, with a surface made of what Right lights of a region.
        var first = new OpsRig(project: MultiViewScanKit.Project());
        string id;
        Project project;
        try
        {
            id = await MultiViewScanKit.ScanAsync(first, Line());
            var outcome = first.Scanner.Find(id)!;
            var lit = outcome.Regions.First(r => r.Projectors.Contains(MultiViewScanKit.Pj2));
            var made = await first.Run($"SCAN SURFACE {id} REGION {lit.Id} ON {MultiViewScanKit.Pj2}");
            Assert.True(made.Ok, made.Payload);
            Assert.NotEmpty(first.Engine.Store.Project.Stage.Surfaces.Single(s => s.Id == made.Payload).Faces);
            await first.Ok("UNDO");
            var folder = Path.Combine(first.Folder, "scans", id);
            Assert.True(File.Exists(Path.Combine(folder, "views", "v1", "view.json")));
            Assert.True(File.Exists(Path.Combine(folder, "views", "v1", MultiViewScanKit.Pj1 + ".pmcm")));
            Assert.True(File.Exists(Path.Combine(folder, "views", "v1", MultiViewScanKit.Pj2 + ".pmcm")));
            project = first.Engine.Store.Project;
        }
        finally
        {
            await first.DisposeAsync();
        }

        // The node stops; a new one opens the same project and folder: nothing of the scan is in its memory.
        var second = new OpsRig(project: project);
        try
        {
            CopyFolder(Path.Combine(first.Folder, "scans"), Path.Combine(second.Folder, "scans"));
            first.DeleteFolder();
            Assert.Null(second.Scanner.Find(id));
            var views = JsonDocument.Parse(await second.Ok("SCAN VIEWS " + id)).RootElement;
            Assert.Equal(["v1"], views.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("id").GetString()));
            // SCAN SURFACE … ON on the scan read back from its folder alone: its faces index the mesh it puts on the stage.
            var reloaded = second.Scanner.Find(id)!;
            var region = reloaded.Regions.First(r => r.Projectors.Contains(MultiViewScanKit.Pj2));
            var fromDisk = await second.Run($"SCAN SURFACE {id} REGION {region.Id} ON {MultiViewScanKit.Pj2} From disk");
            Assert.True(fromDisk.Ok, fromDisk.Payload);
            var made = second.Engine.Store.Project.Stage.Surfaces.Single(s => s.Id == fromDisk.Payload);
            var meshAsset = second.Engine.Store.Project.FindMesh(made.MeshId)!;
            Assert.Equal(reloaded.Solution.Mesh!.TriangleCount, meshAsset.Triangles);
            Assert.All(made.Faces, f => Assert.InRange(f, 0, meshAsset.Triangles - 1));
            await second.Ok("UNDO");

            Assert.Equal(id, await MultiViewScanKit.ScanAsync(second, Line(id)));
            var two = second.Scanner.Find(id)!;
            Assert.Equal(["v1", "v2"], two.Multi!.ViewIds.ToArray());
            output.WriteLine(two.Solution.Words);
            Assert.True(two.Multi.Result.RmsPx < 0.5);
            var dir = Path.Combine(second.Folder, "scans", id);
            Assert.True(File.Exists(Path.Combine(dir, "multiview.json")));
            Assert.True(File.Exists(Path.Combine(dir, "depth", MultiViewScanKit.Pj1 + ".bin")));
            Assert.True(File.Exists(Path.Combine(dir, "views", "v2", "view.json")));

            // Read back alone, the folder gives the same solve, depth, mesh and regions.
            var back = ScanArchive.Load(second.Folder, ScanArchive.DataPath(id))!;
            Assert.Equal(two.Multi.ViewIds.ToArray(), back.Multi!.ViewIds.ToArray());
            Assert.Equal(two.Multi.Result.RmsPx, back.Multi.Result.RmsPx);
            Assert.Equal(two.Multi.Depth.Select(d => d.Depth), back.Multi.Depth.Select(d => d.Depth));
            Assert.Equal(two.Solution.Mesh!.Indices, back.Solution.Mesh!.Indices);
            Assert.Equal(two.Regions.Select(r => (r.Id, r.Label)), back.Regions.Select(r => (r.Id, r.Label)));
            Assert.Equal(two.Captured.Single(v => v.Id == "v1").Maps[MultiViewScanKit.Pj1].ProjectorX,
                back.Captured.Single(v => v.Id == "v1").Maps[MultiViewScanKit.Pj1].ProjectorX);

            // SCAN SURFACE works on the scan after the restart.
            var wall = two.Multi.Regions.Where(r => r.Planar && r.Projectors.Contains(MultiViewScanKit.Pj2)).MaxBy(MultiViewScan.Cells)!;
            var surface = await second.Run($"SCAN SURFACE {id} REGION {wall.Id} ON {MultiViewScanKit.Pj2} Wall");
            Assert.True(surface.Ok, surface.Payload);

            // Applied, the scan is on disk: preflight's row is green. With its folder gone, it is amber with the line that scans again.
            await second.Ok("SCAN APPLY " + id);
            var check = new ScanDataPreflight();
            var row = Assert.Single(await check.RunAsync(new PreflightContext(second.Engine, PreflightDepth.Quick, false), CancellationToken.None));
            Assert.Equal((Core.Bus.Light.Green, "on disk"), (row.Light, row.Value));
            Directory.Move(dir, dir + ".gone");
            row = Assert.Single(await check.RunAsync(new PreflightContext(second.Engine, PreflightDepth.Quick, false), CancellationToken.None));
            Assert.Equal((Core.Bus.Light.Amber, "not on disk"), (row.Light, row.Value));
            Assert.Equal($"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2}", row.Fix);
            Directory.Move(dir + ".gone", dir);

            // HOLD: both projectors held at their stage poses (the scan just applied them): each held projector's result is its stage
            // pose and optics bit for bit, and the solve is in the stage's frame.
            var stage = second.Engine.Store.Project;
            var held = JsonDocument.Parse(await second.Ok($"SCAN SOLVE {id} HOLD {MultiViewScanKit.Pj1} Right")).RootElement;
            Assert.Equal([MultiViewScanKit.Pj1, MultiViewScanKit.Pj2], held.GetProperty("held").EnumerateArray().Select(e => e.GetString()));
            var solved = second.Scanner.Find(id)!.Multi!;
            foreach (var p in solved.Result.Projectors)
            {
                var model = stage.FindProjector(p.Id)!;
                Assert.True(p.Held);
                Assert.Equal(model.Pose, p.Pose);
                Assert.Equal(model.Intrinsics, p.Intrinsics);
            }
            // HOLD names projectors the scan's views saw; the same HOLD again would change nothing.
            var refused = await second.Run($"SCAN SOLVE {id} HOLD pj0000ffff");
            Assert.False(refused.Ok);
            var same = await second.Run($"SCAN SOLVE {id} HOLD {MultiViewScanKit.Pj2} {MultiViewScanKit.Pj1}");
            Assert.False(same.Ok);
            Assert.StartsWith("nothing would change", same.Payload, StringComparison.Ordinal);

            // Solved again, the scan's new mesh is off the stage until SCAN APPLY, but the projectors placed from it still depend on it:
            // preflight keeps checking it, and the record keeps the camera the apply placed.
            var record = second.Engine.Store.Project.Stage.Scans.Single();
            Assert.Null(record.MeshId);
            Assert.Equal(MultiViewScanKit.Camera, record.CameraId);
            row = Assert.Single(await check.RunAsync(new PreflightContext(second.Engine, PreflightDepth.Quick, false), CancellationToken.None));
            Assert.Equal((Core.Bus.Light.Green, "on disk"), (row.Light, row.Value));

            // The last view cannot go.
            Assert.True((await second.Run($"SCAN DROP {id} v2")).Ok);
            var last = await second.Run($"SCAN DROP {id} v1");
            Assert.False(last.Ok);
            Assert.Contains("only view", last.Payload, StringComparison.Ordinal);
            Assert.Equal("dropped", second.Scanner.Find(id)!.Captured.Single(v => v.Id == "v2").State);
        }
        finally
        {
            await second.DisposeAsync();
            second.DeleteFolder();
        }
    }
}
