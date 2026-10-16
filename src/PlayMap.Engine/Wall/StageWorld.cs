using PlayMap.Core.Align;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Wall;

/// <summary>The stage as one world mesh: every visible 3D surface's mesh (its own faces only) placed by its transform. The camera
/// re-solve takes the camera's rays onto it and the projectors' rays onto it; null when the stage has no surface that loads.</summary>
internal static class StageWorld
{
    public static MeshRaycaster? Of(Project p, StageMeshes meshes)
    {
        var positions = new List<float>();
        var indices = new List<uint>();
        foreach (var s in p.Stage.Surfaces)
        {
            if (s.Pixel is not null || !s.Visible || s.MeshId.Length == 0 || p.FindMesh(s.MeshId) is not { } asset || meshes.Get(asset) is not { } mesh)
                continue;
            var part = s.Faces.IsDefaultOrEmpty ? mesh : mesh.Subset(s.Faces);
            var first = (uint)(positions.Count / 3);
            for (var i = 0; i < part.VertexCount; i++)
            {
                var v = s.Transform.Apply(part.Position(i));
                positions.Add((float)v.X);
                positions.Add((float)v.Y);
                positions.Add((float)v.Z);
            }
            foreach (var index in part.Indices)
                indices.Add(first + index);
        }
        return indices.Count == 0 ? null : new MeshRaycaster(new MeshData([.. positions], [], [], [.. indices]));
    }
}

/// <summary>The stage simulator as the dot check's shooter: each shot is the simulated camera's picture of one projector's frame
/// (every other projector black); a null frame shows every projector black. Counts what each projector was shown.</summary>
internal sealed class StageShooter(StageSimulator sim) : IAlignShooter
{
    private readonly int[] _shown = new int[sim.Projectors.Count];

    public IReadOnlyList<int> Shown => _shown;

    public Task<CameraShot> ShootAsync(int output, byte[]? frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var frames = new byte[]?[sim.Projectors.Count];
        if (output >= 0 && frame is not null)
        {
            frames[output] = frame;
            _shown[output]++;
        }
        return Task.FromResult(new CameraShot(sim.Capture(frames), sim.CameraWidth, sim.CameraHeight));
    }
}

/// <summary>Where a simulated camera's projectors really stand: as the project says, unless knocked (the re-solve's tests and the
/// "knock a projector" demo). A knock is in the projector's own axes and is kept as the projector's true device from then on, so a
/// re-solve that moves the project's pose does not move the truth.</summary>
public sealed class SimulatedStage
{
    private readonly Dictionary<string, SimDevice> _truth = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The projector knocked: turned by <paramref name="rotation"/> (rotation vector, radians) and moved by
    /// <paramref name="translation"/> (metres), in its own axes, from where it truly stands now.</summary>
    public void Knock(Projector projector, Vec3d rotation, Vec3d translation)
    {
        ArgumentNullException.ThrowIfNull(projector);
        lock (_gate)
            _truth[projector.Id] = ScanSimulator.Bump(TruthOf(projector), rotation, translation);
    }

    /// <summary>The camera knocked, in its own axes, from where it truly stands now.</summary>
    public void Knock(CameraRig camera, Vec3d rotation, Vec3d translation)
    {
        ArgumentNullException.ThrowIfNull(camera);
        lock (_gate)
            _truth["camera:" + camera.Id] = ScanSimulator.Bump(TruthOf(camera), rotation, translation);
    }

    /// <summary>The camera's true device: its knocked one, else as the project says.</summary>
    public SimDevice TruthOf(CameraRig camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        lock (_gate)
            return _truth.GetValueOrDefault("camera:" + camera.Id) ?? new SimDevice(camera.Intrinsics, camera.Lens, camera.Pose);
    }

    /// <summary>The projector's true device: its knocked one, else as the project says.</summary>
    public SimDevice TruthOf(Projector projector)
    {
        ArgumentNullException.ThrowIfNull(projector);
        lock (_gate)
            return _truth.GetValueOrDefault(projector.Id) ?? new SimDevice(projector.Intrinsics, projector.Lens, projector.Pose);
    }
}
