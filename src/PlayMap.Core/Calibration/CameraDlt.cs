using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>A 3×4 projection matrix taken apart: intrinsics (positive focal lengths, the skew the DLT
/// found and the pose discards), the world→camera rotation and the optical centre in the world.</summary>
public readonly record struct CameraDecomposition(double Fx, double Fy, double Cx, double Cy, double Skew, Mat3d WorldToCamera, Vec3d Centre)
{
    /// <summary>The pose in PlayMap's convention (camera→world rotation vector, centre).</summary>
    public Pose Pose => new(Centre, WorldToCamera.Transposed().ToRotationVector());

    /// <summary>The intrinsics on a raster of the given size (skew dropped).</summary>
    public Intrinsics Intrinsics(int width, int height) => new(Fx, Fy, Cx, Cy, width, height);
}

/// <summary>The direct linear transform for a camera or projector: six or more world↔pixel pairs, not all on
/// one plane, give the 3×4 projection P (11 degrees of freedom, so focal, lens shift and pose at once); RQ
/// takes it apart into K·R·[I | −C].</summary>
public static class CameraDlt
{
    /// <summary>P (row-major, 12 values, scaled so the rotation block has det(K) &gt; 0 and |P₃,₀..₂| = 1)
    /// from the pairs by the normalised DLT; null when there are fewer than six or they are degenerate.</summary>
    public static double[]? Solve(IReadOnlyList<Vec3d> world, IReadOnlyList<Vec2d> pixels)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(pixels);
        if (world.Count != pixels.Count)
            throw new ArgumentException("The lists differ in length.", nameof(pixels));
        return Solve(world.ToArray(), pixels.ToArray());
    }

    internal static double[]? Solve(ReadOnlySpan<Vec3d> world, ReadOnlySpan<Vec2d> pixels)
    {
        var n = world.Length;
        if (n < 6)
            return null;
        var t = Conditioning.Of(pixels);
        var (c, s) = Conditioning.Of(world);
        var a = new DenseMatrix(2 * n, 12);
        for (var i = 0; i < n; i++)
        {
            var x = (world[i] - c) * s;
            var p = t.TransformPoint(pixels[i]);
            var r0 = a.RowSpan(2 * i);
            r0[0] = x.X; r0[1] = x.Y; r0[2] = x.Z; r0[3] = 1;
            r0[8] = -p.X * x.X; r0[9] = -p.X * x.Y; r0[10] = -p.X * x.Z; r0[11] = -p.X;
            var r1 = a.RowSpan(2 * i + 1);
            r1[4] = x.X; r1[5] = x.Y; r1[6] = x.Z; r1[7] = 1;
            r1[8] = -p.Y * x.X; r1[9] = -p.Y * x.Y; r1[10] = -p.Y * x.Z; r1[11] = -p.Y;
        }
        var pn = SolverMath.NullVector(a);
        if (!t.TryInverse(out var tInv))
            return null;
        // P = T⁻¹ · Pn · U with U = [sI | −s·c; 0 1].
        var result = new double[12];
        for (var r = 0; r < 3; r++)
        {
            for (var col = 0; col < 4; col++)
            {
                double v = 0;
                for (var k = 0; k < 3; k++)
                    v += tInv[r, k] * pn[k * 4 + col];
                result[r * 4 + col] = v;
            }
        }
        for (var r = 0; r < 3; r++)
        {
            var m0 = result[r * 4];
            var m1 = result[r * 4 + 1];
            var m2 = result[r * 4 + 2];
            result[r * 4 + 3] = result[r * 4 + 3] - s * (m0 * c.X + m1 * c.Y + m2 * c.Z);
            result[r * 4] = m0 * s;
            result[r * 4 + 1] = m1 * s;
            result[r * 4 + 2] = m2 * s;
        }
        var m = new Mat3d(result[0], result[1], result[2], result[4], result[5], result[6], result[8], result[9], result[10]);
        var det = m.Determinant;
        var norm3 = Math.Sqrt(result[8] * result[8] + result[9] * result[9] + result[10] * result[10]);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-300 || norm3 < 1e-300)
            return null;
        var scale = (det > 0 ? 1 : -1) / norm3;
        for (var i = 0; i < 12; i++)
            result[i] *= scale;
        return result;
    }

    /// <summary>RQ-decomposes P = K·R·[I | −C]: K upper triangular with positive diagonal (normalised to
    /// K₂₂ = 1), R a proper rotation, C the centre. Null when the rotation block is singular.</summary>
    public static CameraDecomposition? Decompose(ReadOnlySpan<double> p)
    {
        if (p.Length != 12)
            throw new ArgumentException("A projection matrix has 12 values.", nameof(p));
        var m = new Mat3d(p[0], p[1], p[2], p[4], p[5], p[6], p[8], p[9], p[10]);
        var p4 = new Vec3d(p[3], p[7], p[11]);
        var sign = m.Determinant < 0 ? -1.0 : 1.0;
        m *= sign;
        p4 *= sign;
        // Rows of M = K·R from the bottom up (Gram–Schmidt on the rows): m₃ = k₃₃ r₃, m₂ = k₂₂ r₂ + k₂₃ r₃,
        // m₁ = k₁₁ r₁ + k₁₂ r₂ + k₁₃ r₃ — the diagonal comes out positive by construction.
        var m1 = m.Row(0);
        var m2 = m.Row(1);
        var m3 = m.Row(2);
        var k33 = m3.Length;
        if (k33 < 1e-300)
            return null;
        var e3 = m3 / k33;
        var k23 = Vec3d.Dot(m2, e3);
        var q2 = m2 - e3 * k23;
        var k22 = q2.Length;
        if (k22 < 1e-300)
            return null;
        var e2 = q2 / k22;
        var k13 = Vec3d.Dot(m1, e3);
        var k12 = Vec3d.Dot(m1, e2);
        var q1 = m1 - e2 * k12 - e3 * k13;
        var k11 = q1.Length;
        if (k11 < 1e-300)
            return null;
        var e1 = q1 / k11;
        var rot = Mat3d.FromRows(e1, e2, e3);
        if (rot.Determinant < 0)
            return null;
        if (!m.TryInverse(out var mInv))
            return null;
        var centre = mInv * p4 * -1;
        return new CameraDecomposition(k11 / k33, k22 / k33, k13 / k33, k23 / k33, k12 / k33, rot.NearestRotation(), centre);
    }

    /// <summary>Where P sends a world point (pixel), or null when the point is at or behind the centre.</summary>
    public static Vec2d? Project(ReadOnlySpan<double> p, Vec3d x)
    {
        var w = p[8] * x.X + p[9] * x.Y + p[10] * x.Z + p[11];
        if (w <= 1e-300)
            return null;
        return new Vec2d(
            (p[0] * x.X + p[1] * x.Y + p[2] * x.Z + p[3]) / w,
            (p[4] * x.X + p[5] * x.Y + p[6] * x.Z + p[7]) / w);
    }

    /// <summary>The pose that best fits P when the intrinsics are already known: R̃ = K⁻¹·M scaled to unit
    /// determinant, snapped to the nearest rotation. Null when P is degenerate.</summary>
    public static Pose? PoseWithKnownIntrinsics(ReadOnlySpan<double> p, Intrinsics k)
    {
        ArgumentNullException.ThrowIfNull(k);
        var m = new Mat3d(p[0], p[1], p[2], p[4], p[5], p[6], p[8], p[9], p[10]);
        var p4 = new Vec3d(p[3], p[7], p[11]);
        var kInv = k.K.Inverse();
        var r = kInv * m;
        var det = r.Determinant;
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-300)
            return null;
        var lambda = Math.Cbrt(det);
        var rot = (r * (1 / lambda)).NearestRotation();
        var t = kInv * p4 * (1 / lambda);
        var centre = rot.Transposed() * t * -1;
        return new Pose(centre, rot.Transposed().ToRotationVector());
    }
}
