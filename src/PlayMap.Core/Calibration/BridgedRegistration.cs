using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>How a bridged registration runs.</summary>
public sealed record BridgeOptions
{
    public static readonly BridgeOptions Default = new();

    /// <summary>At most this many camera pixels are cast (a seeded subset of the decoded ones).</summary>
    public int MaxPairs { get; init; } = 20000;

    /// <summary>Hits more grazing than this (degrees between the ray and the surface normal) are dropped: a
    /// small pose error moves them far along the surface.</summary>
    public double MaxIncidenceDegrees { get; init; } = 80;

    /// <summary>The projector solve (its locks, outlier threshold, seed).</summary>
    public PoseSolveOptions Projector { get; init; } = new() { Noun = "point", OutlierThresholdPx = 3 };

    /// <summary>The camera solve from picked pins, when the camera's pose is not known.</summary>
    public PoseSolveOptions Camera { get; init; } = new() { Locks = SolveLocks.Shift | SolveLocks.Distortion };

    public ulong Seed { get; init; } = 1;
}

/// <summary>A bridged registration's outcome: the projector's solve in the model's frame, the camera's solve
/// when it was found from pins, how many camera rays met the model, the 3D↔projector pairs used, words.</summary>
public sealed record BridgeResult(PoseSolution Projector, PoseSolution? Camera, int RaysCast, int RaysHit,
    IReadOnlyList<Correspondence> Pairs, string Words);

/// <summary>Structured light as the bridge between a model and a projector, with no hand-placed pins: a camera
/// whose pose in the model's frame is known (or found from a few picked camera-pixel↔model points) casts each
/// decoded camera pixel's ray into the mesh; the hit is the 3D point the projector pixel decoded there lit.
/// Thousands of such pairs feed <see cref="PoseSolver"/>, which gives the projector's optics and pose in the
/// model's frame.</summary>
public static class BridgedRegistration
{
    /// <summary>The projector's pose in the mesh's frame from a camera at a known pose there and the
    /// camera↔projector pairs decoded by a scan.</summary>
    public static BridgeResult SolveProjector(MeshRaycaster mesh, DeviceView camera, IReadOnlyList<Vec2d> cameraPixels,
        IReadOnlyList<Vec2d> projectorPixels, Intrinsics projectorPrior, LensDistortion projectorLens, BridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(cameraPixels);
        ArgumentNullException.ThrowIfNull(projectorPixels);
        if (cameraPixels.Count != projectorPixels.Count)
            throw new ArgumentException("The pixel lists differ in length.", nameof(projectorPixels));
        options ??= BridgeOptions.Default;
        var pick = Ransac.Subset(cameraPixels.Count, Math.Max(1, options.MaxPairs), options.Seed);
        var hits = new Correspondence?[pick.Length];
        var minCos = Math.Cos(Math.Clamp(options.MaxIncidenceDegrees, 0, 90) * Math.PI / 180);
        Parallel.For(0, pick.Length, k =>
        {
            var i = pick[k];
            var ray = camera.Ray(cameraPixels[i]);
            if (mesh.Cast(ray) is not { } hit)
                return;
            var (a, b, c) = mesh.Mesh.Triangle(hit.Triangle);
            var n = Vec3d.Cross(mesh.Mesh.Position(b) - mesh.Mesh.Position(a), mesh.Mesh.Position(c) - mesh.Mesh.Position(a)).Normalized();
            if (Math.Abs(Vec3d.Dot(n, ray.Direction)) < minCos)
                return;
            hits[k] = new Correspondence(hit.Point, projectorPixels[i]);
        });
        var pairs = hits.Where(h => h.HasValue).Select(h => h!.Value).ToArray();
        var solution = PoseSolver.Solve(pairs, projectorPrior, projectorLens, options.Projector);
        var words = string.Create(CultureInfo.InvariantCulture,
            $"{pairs.Length:N0} of {pick.Length:N0} camera pixels landed on the model; {solution.Words}");
        return new BridgeResult(solution, null, pick.Length, pairs.Length, pairs, words);
    }

    /// <summary>As <see cref="SolveProjector"/>, finding the camera's pose first from picked pins (model point
    /// ↔ camera pixel) with the camera's intrinsics as given (locked per <see cref="BridgeOptions.Camera"/>).</summary>
    public static BridgeResult SolveCameraThenProjector(MeshRaycaster mesh, Intrinsics cameraIntrinsics, LensDistortion cameraLens,
        IReadOnlyList<Correspondence> cameraPins, IReadOnlyList<Vec2d> cameraPixels, IReadOnlyList<Vec2d> projectorPixels,
        Intrinsics projectorPrior, LensDistortion projectorLens, BridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cameraPins);
        options ??= BridgeOptions.Default;
        var cam = PoseSolver.Solve(cameraPins, cameraIntrinsics, cameraLens, options.Camera);
        if (double.IsPositiveInfinity(cam.RmsPx))
        {
            var empty = new PoseSolution(projectorPrior, projectorLens, Pose.Identity, double.PositiveInfinity, [], [],
                "not solved: the camera's pose is not found — " + cam.Words);
            return new BridgeResult(empty, cam, 0, 0, [], empty.Words);
        }
        var view = new DeviceView(cam.Intrinsics, cam.Lens, cam.Pose);
        var bridged = SolveProjector(mesh, view, cameraPixels, projectorPixels, projectorPrior, projectorLens, options);
        return bridged with { Camera = cam, Words = "camera: " + cam.Words + "; projector: " + bridged.Words };
    }
}
