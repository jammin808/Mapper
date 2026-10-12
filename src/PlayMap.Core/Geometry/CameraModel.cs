using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>Pinhole projection with Brown–Conrady distortion — the one model every camera and projector in
/// PlayMap is solved, simulated and rendered with (OpenCV conventions: pixel centres at integer + 0.5 are
/// not assumed; a pixel coordinate is continuous with (0,0) the top-left corner of the raster).</summary>
public static class CameraModel
{
    /// <summary>Normalised image coordinates (x/z, y/z) → distorted normalised coordinates.</summary>
    public static Vec2d Distort(Vec2d n, LensDistortion lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        if (lens.IsNone)
            return n;
        var x = n.X;
        var y = n.Y;
        var r2 = x * x + y * y;
        var radial = 1 + lens.K1 * r2 + lens.K2 * r2 * r2 + lens.K3 * r2 * r2 * r2;
        var dx = 2 * lens.P1 * x * y + lens.P2 * (r2 + 2 * x * x);
        var dy = lens.P1 * (r2 + 2 * y * y) + 2 * lens.P2 * x * y;
        return new Vec2d(x * radial + dx, y * radial + dy);
    }

    /// <summary>Inverts <see cref="Distort"/> by fixed-point iteration (converges for the distortion a
    /// real lens has; 20 iterations reach well under 1e-9).</summary>
    public static Vec2d Undistort(Vec2d d, LensDistortion lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        if (lens.IsNone)
            return d;
        var x = d.X;
        var y = d.Y;
        for (var i = 0; i < 20; i++)
        {
            var r2 = x * x + y * y;
            var radial = 1 + lens.K1 * r2 + lens.K2 * r2 * r2 + lens.K3 * r2 * r2 * r2;
            var dx = 2 * lens.P1 * x * y + lens.P2 * (r2 + 2 * x * x);
            var dy = lens.P1 * (r2 + 2 * y * y) + 2 * lens.P2 * x * y;
            x = (d.X - dx) / radial;
            y = (d.Y - dy) / radial;
        }
        return new Vec2d(x, y);
    }

    /// <summary>A point in the device's own frame → a pixel, or null behind the device.</summary>
    public static Vec2d? ProjectCamera(Vec3d cam, Intrinsics k, LensDistortion lens)
    {
        ArgumentNullException.ThrowIfNull(k);
        if (cam.Z <= 1e-12)
            return null;
        var d = Distort(new Vec2d(cam.X / cam.Z, cam.Y / cam.Z), lens);
        return new Vec2d(k.Fx * d.X + k.Cx, k.Fy * d.Y + k.Cy);
    }

    /// <summary>A world point → a pixel of a device at a pose, or null behind it.</summary>
    public static Vec2d? Project(Vec3d world, Intrinsics k, LensDistortion lens, Pose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        return ProjectCamera(pose.WorldToCamera(world), k, lens);
    }

    /// <summary>A pixel → the unit ray direction in the device's own frame.</summary>
    public static Vec3d PixelRayCamera(Vec2d pixel, Intrinsics k, LensDistortion lens)
    {
        ArgumentNullException.ThrowIfNull(k);
        var d = new Vec2d((pixel.X - k.Cx) / k.Fx, (pixel.Y - k.Cy) / k.Fy);
        var n = Undistort(d, lens);
        return new Vec3d(n.X, n.Y, 1).Normalized();
    }

    /// <summary>A pixel → the ray in the world.</summary>
    public static Ray PixelRay(Vec2d pixel, Intrinsics k, LensDistortion lens, Pose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        var dir = pose.CameraToWorld * PixelRayCamera(pixel, k, lens);
        return new Ray(pose.Position, dir.Normalized());
    }

    /// <summary>True when the pixel is inside the raster.</summary>
    public static bool InRaster(Vec2d p, Intrinsics k) => p.X >= 0 && p.Y >= 0 && p.X < k.Width && p.Y < k.Height;

    /// <summary>The 3×4 projection matrix K·[Rᵀ | −Rᵀ·C] (world → homogeneous pixel), row-major 12 values,
    /// without distortion.</summary>
    public static double[] ProjectionMatrix(Intrinsics k, Pose pose)
    {
        ArgumentNullException.ThrowIfNull(k);
        ArgumentNullException.ThrowIfNull(pose);
        var rt = pose.CameraToWorld.Transposed();
        var t = rt * pose.Position * -1;
        var m = k.K * rt;
        var kt = k.K * t;
        return [m.M00, m.M01, m.M02, kt.X, m.M10, m.M11, m.M12, kt.Y, m.M20, m.M21, m.M22, kt.Z];
    }

    /// <summary>The OpenGL-style view and projection matrices (column-vector convention, clip z in [−1, 1],
    /// y up in clip space) that make the GPU rasterise exactly what <see cref="Project"/> computes for an
    /// undistorted device: pixel (u, v) of a W×H raster lands at clip x = 2u/W − 1, y = 1 − 2v/H.</summary>
    public static (System.Numerics.Matrix4x4 View, System.Numerics.Matrix4x4 Projection) GlMatrices(Intrinsics k, Pose pose, double near, double far)
    {
        ArgumentNullException.ThrowIfNull(k);
        ArgumentNullException.ThrowIfNull(pose);
        var rt = pose.CameraToWorld.Transposed();
        var t = rt * pose.Position * -1;
        // View: world → camera (OpenCV frame: x right, y down, z forward). Stored row-major for
        // System.Numerics' row-vector convention, so these are the transposes of the column forms.
        var view = new System.Numerics.Matrix4x4(
            (float)rt.M00, (float)rt.M10, (float)rt.M20, 0,
            (float)rt.M01, (float)rt.M11, (float)rt.M21, 0,
            (float)rt.M02, (float)rt.M12, (float)rt.M22, 0,
            (float)t.X, (float)t.Y, (float)t.Z, 1);
        double w = k.Width, h = k.Height;
        double n = near, f = far;
        // Camera (x, y, z) with z forward → clip: x_c = (2fx/W)x + (2cx/W − 1)z, y_c = −(2fy/H)y − (2cy/H − 1)z,
        // z_c = ((f+n)/(f−n))z − 2fn/(f−n), w_c = z.
        var proj = new System.Numerics.Matrix4x4(
            (float)(2 * k.Fx / w), 0, 0, 0,
            0, (float)(-2 * k.Fy / h), 0, 0,
            (float)(2 * k.Cx / w - 1), (float)(1 - 2 * k.Cy / h), (float)((f + n) / (f - n)), 1,
            0, 0, (float)(-2 * f * n / (f - n)), 0);
        return (view, proj);
    }
}
