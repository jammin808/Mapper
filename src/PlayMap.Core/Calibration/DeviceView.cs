using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>A calibrated device — camera or projector — as the solvers see it: optics and pose. A projector
/// is a camera run backwards, so one shape serves both.</summary>
public sealed record DeviceView(Intrinsics Intrinsics, LensDistortion Lens, Pose Pose)
{
    public static DeviceView Of(Projector projector)
    {
        ArgumentNullException.ThrowIfNull(projector);
        return new DeviceView(projector.Intrinsics, projector.Lens, projector.Pose);
    }

    public static DeviceView Of(CameraRig camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return new DeviceView(camera.Intrinsics, camera.Lens, camera.Pose);
    }

    /// <summary>A world point → a pixel, or null behind the device.</summary>
    public Vec2d? Project(Vec3d world) => CameraModel.Project(world, Intrinsics, Lens, Pose);

    /// <summary>A pixel → its ray in the world.</summary>
    public Ray Ray(Vec2d pixel) => CameraModel.PixelRay(pixel, Intrinsics, Lens, Pose);

    /// <summary>The same device after a similarity of the world (re-expressed in another frame).</summary>
    public DeviceView Transformed(Similarity3 s) => this with { Pose = s.Apply(Pose) };
}

/// <summary>A projection with its derivative: the pinhole-plus-distortion model of <see cref="CameraModel"/>
/// written so the solvers get ∂pixel/∂(camera point) analytically.</summary>
internal static class Projection
{
    /// <summary>Projects a point in the device frame; false at or behind the centre. The Jacobian rows are
    /// ∂u/∂X and ∂v/∂X.</summary>
    public static bool ProjectCamera(Vec3d x, double fx, double fy, double cx, double cy, LensDistortion lens,
        out Vec2d pixel, out Vec3d du, out Vec3d dv)
    {
        if (x.Z <= 1e-12)
        {
            pixel = default;
            du = dv = default;
            return false;
        }
        var iz = 1 / x.Z;
        var n = new Vec2d(x.X * iz, x.Y * iz);
        var d = CameraModel.Distort(n, lens);
        pixel = new Vec2d(fx * d.X + cx, fy * d.Y + cy);
        var (a, b, c, e) = SolverMath.DistortJacobian(n, lens);
        // ∂n/∂X = [[1/z, 0, −x/z²], [0, 1/z, −y/z²]].
        var dnx = new Vec3d(iz, 0, -n.X * iz);
        var dny = new Vec3d(0, iz, -n.Y * iz);
        du = (dnx * a + dny * b) * fx;
        dv = (dnx * c + dny * e) * fy;
        return true;
    }
}
