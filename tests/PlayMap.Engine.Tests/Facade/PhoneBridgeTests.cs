using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Phone;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>A phone scan bridges into the stage as placed: the scene's model, its file 3 m off and its surface placed back by its
/// transform, takes a scan through a calibrated phone's rig into the stage's frame — the projector found within a real camera's
/// tolerance of the truth, where the file's raw coordinates would put it metres away.</summary>
public sealed class PhoneBridgeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task APhoneScanBridgesIntoTheModelAsItIsPlaced()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync();
        var offset = new Vec3d(3, 0, 0);
        var scene = SimScenes.FloorAndBox(256, 160, 320, 240).Mesh;
        var moved = new float[scene.Positions.Length];
        for (var i = 0; i < moved.Length; i += 3)
        {
            moved[i] = (float)(scene.Positions[i] - offset.X);
            moved[i + 1] = (float)(scene.Positions[i + 1] - offset.Y);
            moved[i + 2] = (float)(scene.Positions[i + 2] - offset.Z);
        }
        Directory.CreateDirectory(Path.Combine(rig.Folder, "meshes"));
        await using (var s = File.Create(Path.Combine(rig.Folder, "meshes", "scene.ply")))
            MeshIO.WritePly(s, new MeshData(moved, [], [], scene.Indices));
        var r = await rig.Engine.EditAsync(PhoneScanRig.Desk, "the surveyed scene", p => p.WithStage(st => st with
        {
            Meshes = st.Meshes.Add(new MeshAsset("ms_scene01", "Scene", MeshOrigin.Imported, "meshes/scene.ply", "", "", ParamSet.Empty,
                scene.VertexCount, scene.TriangleCount)),
            Surfaces = st.Surfaces.Add(new Surface("sf_scene01", "Scene", "ms_scene01", Transform3.Identity with { Position = offset }, [],
                SurfaceContent.Empty, 1, [], true, false, "#FFFFFF", [])),
        }));
        Assert.True(r.Changed, r.Effect);

        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST")).Ok);
        var view = await rig.EndedAsync();
        Assert.True(view.State == "done", view.Message);
        var record = Assert.Single(rig.Engine.Store.Project.Stage.Scans);
        var solution = rig.Runner.Find(record.Id)!.Solution;
        Assert.Equal("bridged", solution.Method);
        var solved = Assert.Single(solution.Projectors);
        Assert.True(solved.Solved, solved.Words);
        var (position, degrees) = ScanRunner.PoseError(solved.Pose, rig.Truth.Projector(0).Pose);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bridged: {position * 1000:0.0} mm, {degrees:0.000}° from the truth; {solution.Words}"));
        Assert.True(position < 0.01, $"{position * 1000:0.0} mm");
        Assert.True(degrees < 0.25, $"{degrees:0.000}°");
    }
}
