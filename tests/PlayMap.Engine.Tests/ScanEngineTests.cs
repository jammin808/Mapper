using System.Text.Json;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests;

/// <summary>The scan through the engine's own verbs with no hardware: SCAN START on the simulated camera, progress in
/// STATE, the record and its files, SCAN APPLY onto the stage as one undoable edit — projector poses within a
/// stated tolerance of the simulator's truth, the mesh written beside the project, planes offered as surfaces.</summary>
public sealed class ScanEngineTests(ITestOutputHelper output)
{
    /// <summary>How close a simulated scan must place a projector: its centre within 5 mm, its orientation within
    /// 0.05° and its throw ratio within 0.2 % of the simulator's truth (a 256 × 160 projector seen by a 320 × 240
    /// calibrated camera from about 2.5 m; the priors were 12 % off in throw and decimetres off in place).</summary>
    private const double PositionToleranceM = 0.005;

    private const double RotationToleranceDeg = 0.05;

    private const double ThrowTolerance = 0.002;

    /// <summary>Two projectors priored as a datasheet would have them (throw ratio 1.4, the truth is 1.25) and placed
    /// roughly where they hang.</summary>
    private static Project TwoProjectors()
    {
        var p = Project.CreateDefault("Scan test");
        Projector Make(string id, string name, Vec3d eye) => new(id, name, Intrinsics.FromThrowRatio(256, 160, 1.4), LensDistortion.None,
            Pose.LookAt(eye, new Vec3d(0, 0.2, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0);
        return p.WithStage(s => s with
        {
            Projectors = [Make("pj00000001", "Left", new Vec3d(0.1, 1.5, 2.3)), Make("pj00000002", "Right", new Vec3d(-0.8, 1.6, 2.4))],
        });
    }

    /// <summary>A capture camera that will not open, as a missing device answers.</summary>
    private sealed class AbsentCamera(string problem) : IScanCamera
    {
        public string Problem => problem;

        public Task<bool> OpenAsync(CancellationToken ct) => Task.FromResult(false);

        public Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct) => Task.FromResult<CameraFrame?>(null);

        public void Dispose()
        {
            // Nothing was opened.
        }
    }

    [Fact]
    public async Task ALatencyBelowZeroIsTakenAsZeroAndJournalled()
    {
        var rig = new OpsRig();
        try
        {
            Assert.Equal(12_500_000, rig.Scanner.CaptureLatencyNs(12.5, "Left"));
            Assert.DoesNotContain(rig.Engine.Journal.Recent(20), r => r.Text.Contains("below zero", StringComparison.Ordinal));
            Assert.Equal(0, rig.Scanner.CaptureLatencyNs(-3.5, "Left"));
            Assert.Equal(0, rig.Scanner.CaptureLatencyNs(double.NaN, "Left"));
            var rows = rig.Engine.Journal.Recent(20).Where(r => r.Text.Contains("below zero", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => !r.Ok && r.Text.StartsWith("Left: the camera's latency came out at -3.5 ms", StringComparison.Ordinal));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task ACameraThatCannotOpenSaysWhyOnItsViewAndInState()
    {
        var opens = 0;
        var project = Project.CreateDefault("Cam") with
        {
            Stage = Stage.Empty with
            {
                Cameras = [new CameraRig("cm00000001", "Webcam", CameraSourceKind.Device, "/dev/video9", Intrinsics.FromThrowRatio(640, 480, 1.2), LensDistortion.None, Pose.Identity, false)],
            },
        };
        var rig = new OpsRig(project: project, scan: new ScanRunnerOptions
        {
            OpenCamera = _ =>
            {
                Interlocked.Increment(ref opens);
                return new AbsentCamera("/dev/video9: No such file or directory");
            },
        });
        try
        {
            Assert.True((await rig.Run("VIEW scan-camera SHOW camera:cm00000001 640x480")).Ok);

            // The view is not left blank: it carries a card with the reason, and STATE names it.
            Assert.True(await OpsRig.Eventually(() => rig.Picture("scan-camera") is not null), "the view stayed blank");
            JsonElement Row() => rig.State().GetProperty("cameras").EnumerateArray().Single();
            Assert.True(await OpsRig.Eventually(() => Row().GetProperty("state").GetString() == "failed"));
            Assert.Equal("cm00000001", Row().GetProperty("id").GetString());
            Assert.Equal(1, Row().GetProperty("views").GetInt32());
            Assert.Equal("Webcam did not open: /dev/video9: No such file or directory", Row().GetProperty("problem").GetString());

            // Shown again, it is tried again; once its last view goes, its problem is no longer news.
            Assert.True((await rig.Run("VIEW scan-camera-2 SHOW camera:cm00000001 320x240")).Ok);
            Assert.True(await OpsRig.Eventually(() => Volatile.Read(ref opens) >= 2));
            Assert.True(await OpsRig.Eventually(() => rig.Picture("scan-camera-2") is not null));
            await rig.Ok("VIEW scan-camera HIDE");
            await rig.Ok("VIEW scan-camera-2 HIDE");
            Assert.True(await OpsRig.Eventually(() => rig.State().GetProperty("cameras").GetArrayLength() == 0));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task EveryScanStepIsAlreadyInStateWhenItIsHeard()
    {
        var rig = new OpsRig(project: TwoProjectors());
        try
        {
            var heard = 0;
            var stale = new List<string>();
            // A pushing transport recomputes STATE the moment the node says it changed, as the wire and the web do.
            rig.Engine.StateChanged += _ => rig.Engine.State();
            rig.Scanner.Progressed += v =>
            {
                // A listener that reads STATE on hearing a step must read that step, never the one before it.
                var row = rig.State().GetProperty("scan");
                lock (stale)
                {
                    heard++;
                    if (row.ValueKind != JsonValueKind.Object || row.GetProperty("stage").GetString() != v.Stage
                        || row.GetProperty("pattern").GetInt32() != v.Pattern || row.GetProperty("progress").GetDouble() != v.Progress)
                        stale.Add($"{v.Stage} {v.Pattern}/{v.Patterns} heard, STATE said {row}");
                }
            };
            Assert.True((await rig.Run("SCAN START sim MODE FAST")).Ok);
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.Current?.State is "done" or "failed" or "cancelled", 120_000), "the scan never ended");
            lock (stale)
            {
                Assert.True(heard > 10, $"{heard} steps heard");
                Assert.Empty(stale);
            }
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task ASimulatedScanRunsThroughTheVerbsAndAppliesWithinToleranceOfTheTruth()
    {
        var rig = new OpsRig(project: TwoProjectors());
        try
        {
            var stages = new List<string>();
            var running = new List<JsonElement>();
            rig.Scanner.Progressed += p =>
            {
                lock (stages)
                {
                    stages.Add(p.Stage);
                    if (p.Stage == "scanning" && running.Count == 0)
                        running.Add(rig.State().GetProperty("scan"));
                }
            };
            // The desk's camera view: the simulated camera's scene before the scan, the scan's own frames during it.
            await rig.Ok("VIEW scan-camera SHOW camera:sim 960x540");
            Assert.True(await OpsRig.Eventually(() => rig.Picture("scan-camera") is { Sequence: 1 }), "the camera's still never came");
            var started = await rig.Run("SCAN START sim MODE ROBUST");
            Assert.True(started.Ok, started.Payload);
            Assert.False(started.Applied);
            var scanId = JsonDocument.Parse(started.Payload!).RootElement.GetProperty("scan").GetString()!;
            Assert.StartsWith(Ids.Scan, scanId, StringComparison.Ordinal);
            Assert.False((await rig.Run("SCAN START sim")).Ok);
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.Current?.State is "done" or "failed" or "cancelled", 120_000), "the scan never ended");
            Assert.True(rig.Scanner.Current!.State == "done", rig.Scanner.Current.Words);
            lock (stages)
            {
                Assert.Contains("scanning", stages);
                Assert.Contains("decoding", stages);
                Assert.Contains("calibrating", stages);
                var during = Assert.Single(running);
                Assert.Equal(scanId, during.GetProperty("id").GetString());
                Assert.Equal("running", during.GetProperty("state").GetString());
                Assert.True(during.GetProperty("patterns").GetInt32() > 50);
                Assert.True(during.GetProperty("progress").GetDouble() is > 0 and < 1);
                Assert.Equal(2, during.GetProperty("projectors").GetArrayLength());
            }

            var record = Assert.Single(rig.Engine.Store.Project.Stage.Scans);
            Assert.Equal(scanId, record.Id);
            output.WriteLine(record.Notes);
            Assert.Equal(SimulatedRig.CameraId, record.CameraId);
            Assert.Equal(["pj00000001", "pj00000002"], record.ProjectorIds.ToArray());
            Assert.True(record.Points > 20_000, $"{record.Points} points");
            Assert.True(record.Residual is > 0 and < 1, $"worst residual {record.Residual} px");
            Assert.True(File.Exists(Path.Combine(rig.Folder, record.DataPath, "scan.json")));
            Assert.True(File.Exists(Path.Combine(rig.Folder, record.DataPath, "mesh.ply")));
            Assert.True(File.Exists(Path.Combine(rig.Folder, record.DataPath, "smart-pj00000001.pgm")));
            var state = rig.State().GetProperty("scan");
            output.WriteLine(state.ToString());
            Assert.Equal("done", state.GetProperty("state").GetString());
            Assert.Equal(1, state.GetProperty("progress").GetDouble());
            Assert.Equal(state.GetProperty("patterns").GetInt32(), state.GetProperty("pattern").GetInt32());
            Assert.True(state.GetProperty("quality").GetDouble() is > 0.5 and <= 1);
            Assert.NotEmpty(state.GetProperty("qualityWords").GetString()!);
            Assert.All(state.GetProperty("projectors").EnumerateArray(), p => Assert.Equal("done", p.GetProperty("state").GetString()));
            var regions = state.GetProperty("regions").EnumerateArray().ToList();
            Assert.NotEmpty(regions);
            Assert.All(regions, r =>
            {
                var rect = r.GetProperty("rect").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                Assert.Equal(4, rect.Length);
                Assert.True(rect[0] >= 0 && rect[1] >= 0 && rect[0] + rect[2] <= 1.0001 && rect[1] + rect[3] <= 1.0001, string.Join(",", rect));
                Assert.True(r.GetProperty("confidence").GetDouble() is >= 0 and <= 1);
            });
            Assert.Equal(state.ToString(), JsonDocument.Parse((await rig.Run("SCAN STATUS")).Payload!).RootElement.ToString());

            // Scan frames reached the camera view (thinned in time), and the still came back after; the smart scan and
            // the confidence are drawn at the projector's raster when the desk asks.
            var seen = rig.Picture("scan-camera")!.Value;
            Assert.True(seen.Sequence > 2, $"{seen.Sequence} camera pictures");
            Assert.Equal((320, 240), (seen.Width, seen.Height));
            await rig.Ok($"VIEW review SHOW scan:{scanId}:smart 1280x720");
            Assert.True(await OpsRig.Eventually(() => rig.Picture("review") is { Width: 256, Height: 160 }), "the smart scan never came");
            await rig.Ok($"VIEW review SHOW scan:{scanId}:confidence 1280x720");
            Assert.True(await OpsRig.Eventually(() => rig.Picture("review") is { Sequence: 2 }), "the confidence never came");
            Assert.False((await rig.Run("VIEW nope SHOW scan:sc99999999:smart")).Ok);
            Assert.False((await rig.Run("VIEW nope SHOW camera:cm99999999")).Ok);

            // A click on the largest region of the smart scan makes a surface of it (and puts the scan's mesh on the
            // stage); a click off the picture is refused.
            var largest = regions.OrderByDescending(r => r.GetProperty("rect")[2].GetDouble() * r.GetProperty("rect")[3].GetDouble()).First();
            var rectOf = largest.GetProperty("rect").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var click = await rig.Run(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"SCAN SURFACE {scanId} {rectOf[0] + rectOf[2] / 2:0.###} {rectOf[1] + rectOf[3] / 2:0.###}"));
            Assert.True(click.Ok, click.Payload);
            output.WriteLine(click.Effect);
            var made = rig.Engine.Store.Project.FindSurface(click.Payload)!;
            Assert.True(made.Faces.Length > 100, $"{made.Faces.Length} faces");
            Assert.NotEmpty(made.Masks);
            Assert.All(made.Masks[0].Path.Nodes, n => Assert.True(n.Point.X is >= 0 and <= 1 && n.Point.Y is >= 0 and <= 1));
            Assert.Contains("scan", made.Tags);
            var surfaceMesh = Assert.Single(rig.Engine.Store.Project.Stage.Meshes);
            Assert.Contains("face", made.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(surfaceMesh.Id, made.MeshId);
            Assert.False((await rig.Run($"SCAN SURFACE {scanId} 1.5 0.5")).Ok);
            Assert.True((await rig.Run("UNDO")).Ok);
            Assert.Empty(rig.Engine.Store.Project.Stage.Meshes);
            // A suggested region by its id: the floor round the box, which its rectangle's centre (on the box) would miss.
            var floorRegion = regions.First(r => r.GetProperty("label").GetString() == "floor").GetProperty("id").GetString();
            var named = await rig.Run($"SCAN SURFACE {scanId} REGION {floorRegion} \"Stage floor\"");
            Assert.True(named.Ok, named.Payload);
            var floor = rig.Engine.Store.Project.FindSurface(named.Payload)!;
            Assert.Equal("Stage floor", floor.Name);
            output.WriteLine($"{floor.Name}: {floor.Faces.Length} faces, tags {string.Join(", ", floor.Tags)}");
            Assert.Contains("scan", floor.Tags);
            Assert.True(floor.Faces.Length > made.Faces.Length);
            surfaceMesh = Assert.Single(rig.Engine.Store.Project.Stage.Meshes);

            // The scan's folder reads back with what click-to-surface needs.
            var reread = ScanArchive.Load(rig.Folder, record.DataPath)!;
            Assert.NotNull(reread.Geometry);
            Assert.Equal(rig.Scanner.Find(scanId)!.Solution.Mesh!.TriangleCount, reread.Solution.Mesh!.TriangleCount);
            Assert.Equal(record.Points, reread.Geometry!.Cloud.Points.Count(PointCloud.IsValid));
            Assert.Contains(record.Id, rig.Engine.Journal.Recent(50).Select(r => r.Text).Last(t => t.StartsWith("scan ", StringComparison.Ordinal)), StringComparison.Ordinal);

            var revision = rig.Engine.Store.Current.Revision;
            var reply = await rig.Run($"SCAN APPLY {record.Id}");
            Assert.True(reply.Ok, reply.Payload);
            Assert.Equal(revision + 1, rig.Engine.Store.Current.Revision);
            using var doc = JsonDocument.Parse(reply.Payload!);
            output.WriteLine(doc.RootElement.GetProperty("words").GetString());

            var outcome = rig.Scanner.Find(record.Id)!;
            var p = rig.Engine.Store.Project;
            foreach (var pj in p.Stage.Projectors)
            {
                var truth = outcome.Truth.Single(t => t.Id == pj.Id);
                var (position, degrees) = ScanRunner.PoseError(pj.Pose, truth.Pose);
                output.WriteLine($"{pj.Name}: position {position * 1000:0.0} mm, rotation {degrees:0.000}°, throw {pj.Intrinsics.ThrowRatio:0.0000} vs {truth.Intrinsics.ThrowRatio:0.0000}, residual {pj.SolveResidualPx:0.000} px");
                Assert.True(position < PositionToleranceM, $"{pj.Name} is {position * 1000:0.0} mm from the truth");
                Assert.True(degrees < RotationToleranceDeg, $"{pj.Name} is turned {degrees:0.000}° from the truth");
                Assert.True(Math.Abs(pj.Intrinsics.ThrowRatio / truth.Intrinsics.ThrowRatio - 1) < ThrowTolerance, $"{pj.Name} throw {pj.Intrinsics.ThrowRatio}");
                Assert.True(pj.SolveResidualPx is > 0 and < 1);
            }

            var mesh = Assert.Single(p.Stage.Meshes);
            Assert.Equal(surfaceMesh.Id, mesh.Id);
            Assert.Equal(MeshOrigin.Scanned, mesh.Origin);
            var file = Path.Combine(rig.Folder, mesh.Path);
            Assert.True(File.Exists(file), file);
            var loaded = MeshIO.Load(file).Mesh!;
            Assert.Equal(mesh.Triangles, loaded.TriangleCount);
            Assert.True(loaded.TriangleCount > 10_000, $"{loaded.TriangleCount} triangles");
            Assert.StartsWith("sha256:", mesh.ContentHash, StringComparison.Ordinal);
            var camera = p.Stage.Cameras.Single();
            Assert.Equal(CameraSourceKind.Simulated, camera.SourceKind);
            Assert.True(camera.Calibrated);
            Assert.Equal(camera.Id, p.Stage.Scans[0].CameraId);
            Assert.Equal(mesh.Id, p.Stage.Scans[0].MeshId);
            Assert.Contains(p.Stage.Surfaces, s => s.Id == floor.Id);
            var whole = Assert.Single(p.Stage.Surfaces, s => s.Visible && s.Faces.IsEmpty);
            Assert.Equal(mesh.Id, whole.MeshId);
            var suggested = p.Stage.Surfaces.Where(s => s.Tags.Contains("suggested")).ToList();
            Assert.NotEmpty(suggested);
            Assert.All(suggested, s => Assert.False(s.Visible));
            Assert.Contains(suggested, s => s.Tags.Contains("floor"));
            Assert.Equal(suggested.Count, doc.RootElement.GetProperty("suggestions").GetArrayLength());

            Assert.False((await rig.Run($"SCAN APPLY {record.Id}")).Ok);
            Assert.True((await rig.Run("UNDO")).Ok);
            Assert.Single(rig.Engine.Store.Project.Stage.Meshes);
            Assert.Equal(floor, Assert.Single(rig.Engine.Store.Project.Stage.Surfaces));
            Assert.Equal(TwoProjectors().Stage.Projectors[0].Pose, rig.Engine.Store.Project.Stage.Projectors[0].Pose);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task AScanIsRefusedWithoutACameraAndCancelGivesTheOutputsBack()
    {
        var rig = new OpsRig(project: TwoProjectors());
        try
        {
            var none = await rig.Run("SCAN START cm00000009");
            Assert.False(none.Ok);
            Assert.Contains("sim", none.Payload, StringComparison.Ordinal);
            Assert.True((await rig.Run("SCAN START sim pj00000001 MODE FAST")).Ok);
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.View?.Stage == "scanning", 30_000));
            Assert.True((await rig.Run("SCAN CANCEL")).Ok);
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.Current?.State == "cancelled", 30_000), rig.Scanner.Current?.State);
            Assert.Empty(rig.Engine.Store.Project.Stage.Scans);
            Assert.False((await rig.Run("SCAN CANCEL")).Ok);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>Scan's decoder and Calibration's triangulation share one pixel convention (continuous, (0, 0) the
    /// raster's top-left corner, a pixel's centre at +0.5): triangulating the decode through the adapter with the
    /// simulator's true devices lands on the true surface within millimetres — and the same decode shifted by half a
    /// projector pixel, the error a convention mismatch would make, lands visibly off.</summary>
    [Fact]
    public void TheDecoderAndTheTriangulationAgreeOnWherePixelCentresAre()
    {
        var scene = SimScenes.FloorAndBox();
        var plan = ScanPlan.Create(256, 160, new ScanPlanOptions { Mode = ScanMode.Robust });
        var sim = new ScanSimulator(scene, plan);
        var map = new StructuredLightDecoder(plan).Decode(sim.CameraWidth, sim.CameraHeight, sim.RenderAll());
        var camera = new DeviceView(scene.Camera.Intrinsics, scene.Camera.Lens, scene.Camera.Pose);
        var projector = new DeviceView(scene.Projector.Intrinsics, scene.Projector.Lens, scene.Projector.Pose);

        double MedianError(PixelCorrespondenceMap pixels)
        {
            var points = Triangulation.Organised(camera, projector, pixels, maxResidualPx: 5).Cloud.Points;
            var errors = new List<double>();
            for (var k = 0; k < points.Length; k++)
            {
                if (PointCloud.IsValid(points[k]) && PointCloud.IsValid(sim.Truth.Points[k]) && sim.Truth.Lit[k])
                    errors.Add(Vec3d.Distance(points[k], sim.Truth.Points[k]));
            }
            errors.Sort();
            return errors[errors.Count / 2];
        }

        var adapted = CorrespondenceAdapter.ToPixelMap(map);
        Assert.Same(map.ProjectorX, adapted.ProjectorX);
        var agreed = MedianError(adapted);
        var shifted = MedianError(adapted with
        {
            ProjectorX = map.ProjectorX.Select(x => x + 0.5f).ToArray(),
            ProjectorY = map.ProjectorY.Select(y => y + 0.5f).ToArray(),
        });
        output.WriteLine($"median error {agreed * 1000:0.00} mm; half a pixel off: {shifted * 1000:0.00} mm");
        Assert.True(agreed < 0.003, $"{agreed * 1000:0.00} mm");
        Assert.True(shifted > 3 * agreed, $"{shifted * 1000:0.00} mm vs {agreed * 1000:0.00} mm");
    }
}
