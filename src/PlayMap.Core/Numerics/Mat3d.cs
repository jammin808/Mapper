namespace PlayMap.Core.Numerics;

/// <summary>A 3×3 matrix in double precision, row-major (M[row, col] = M{row}{col}). Homographies,
/// rotations and camera intrinsics are all 3×3.</summary>
public readonly record struct Mat3d(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    public static readonly Mat3d Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public double this[int r, int c] => (r * 3 + c) switch
    {
        0 => M00, 1 => M01, 2 => M02,
        3 => M10, 4 => M11, 5 => M12,
        6 => M20, 7 => M21, 8 => M22,
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    public static Mat3d FromRows(Vec3d r0, Vec3d r1, Vec3d r2) =>
        new(r0.X, r0.Y, r0.Z, r1.X, r1.Y, r1.Z, r2.X, r2.Y, r2.Z);

    public static Mat3d FromColumns(Vec3d c0, Vec3d c1, Vec3d c2) =>
        new(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);

    /// <summary>Nine values, row-major.</summary>
    public static Mat3d FromArray(ReadOnlySpan<double> v) => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);

    public double[] ToArray() => [M00, M01, M02, M10, M11, M12, M20, M21, M22];

    public Vec3d Row(int r) => r switch
    {
        0 => new Vec3d(M00, M01, M02),
        1 => new Vec3d(M10, M11, M12),
        2 => new Vec3d(M20, M21, M22),
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    public Vec3d Column(int c) => c switch
    {
        0 => new Vec3d(M00, M10, M20),
        1 => new Vec3d(M01, M11, M21),
        2 => new Vec3d(M02, M12, M22),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public Mat3d Transposed() => FromColumns(Row(0), Row(1), Row(2));

    public double Determinant =>
        M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

    public double Trace => M00 + M11 + M22;

    /// <summary>The inverse; throws when the matrix is singular (|det| below 1e-300).</summary>
    public Mat3d Inverse()
    {
        if (!TryInverse(out var inv))
            throw new InvalidOperationException("The matrix is singular.");
        return inv;
    }

    public bool TryInverse(out Mat3d inverse)
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-300 || double.IsNaN(det))
        {
            inverse = default;
            return false;
        }
        var id = 1.0 / det;
        inverse = new Mat3d(
            (M11 * M22 - M12 * M21) * id, (M02 * M21 - M01 * M22) * id, (M01 * M12 - M02 * M11) * id,
            (M12 * M20 - M10 * M22) * id, (M00 * M22 - M02 * M20) * id, (M02 * M10 - M00 * M12) * id,
            (M10 * M21 - M11 * M20) * id, (M01 * M20 - M00 * M21) * id, (M00 * M11 - M01 * M10) * id);
        return true;
    }

    public static Mat3d operator *(Mat3d a, Mat3d b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    public static Vec3d operator *(Mat3d m, Vec3d v) => new(
        m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
        m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
        m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

    public static Mat3d operator *(Mat3d m, double s) => new(
        m.M00 * s, m.M01 * s, m.M02 * s, m.M10 * s, m.M11 * s, m.M12 * s, m.M20 * s, m.M21 * s, m.M22 * s);

    public static Mat3d operator +(Mat3d a, Mat3d b) => new(
        a.M00 + b.M00, a.M01 + b.M01, a.M02 + b.M02, a.M10 + b.M10, a.M11 + b.M11, a.M12 + b.M12,
        a.M20 + b.M20, a.M21 + b.M21, a.M22 + b.M22);

    /// <summary>Applies the matrix as a homography to a 2D point (x, y, 1) and divides by w.</summary>
    public Vec2d TransformPoint(Vec2d p)
    {
        var w = M20 * p.X + M21 * p.Y + M22;
        return new Vec2d((M00 * p.X + M01 * p.Y + M02) / w, (M10 * p.X + M11 * p.Y + M12) / w);
    }

    /// <summary>The skew-symmetric matrix [v]× with [v]× u = v × u.</summary>
    public static Mat3d Skew(Vec3d v) => new(0, -v.Z, v.Y, v.Z, 0, -v.X, -v.Y, v.X, 0);

    /// <summary>Rodrigues: a rotation vector (axis × angle in radians) to a rotation matrix.</summary>
    public static Mat3d FromRotationVector(Vec3d r)
    {
        var theta = r.Length;
        if (theta < 1e-12)
            return Identity + Skew(r);
        var k = r / theta;
        var kx = Skew(k);
        return Identity + kx * Math.Sin(theta) + kx * kx * (1 - Math.Cos(theta));
    }

    /// <summary>The rotation vector of a rotation matrix (inverse Rodrigues), angle in [0, π].</summary>
    public Vec3d ToRotationVector()
    {
        var cos = Math.Clamp((Trace - 1) / 2, -1.0, 1.0);
        var theta = Math.Acos(cos);
        if (theta < 1e-9)
            return new Vec3d((M21 - M12) / 2, (M02 - M20) / 2, (M10 - M01) / 2);
        if (Math.PI - theta < 1e-6)
        {
            // Near π the antisymmetric part vanishes; the axis is the column of (R + I) with the largest norm.
            var b = this + Identity;
            var c0 = b.Column(0); var c1 = b.Column(1); var c2 = b.Column(2);
            var axis = c0.LengthSquared >= c1.LengthSquared && c0.LengthSquared >= c2.LengthSquared ? c0
                : c1.LengthSquared >= c2.LengthSquared ? c1 : c2;
            return axis.Normalized() * theta;
        }
        var s = 2 * Math.Sin(theta);
        return new Vec3d((M21 - M12) / s, (M02 - M20) / s, (M10 - M01) / s) * theta;
    }

    /// <summary>The nearest rotation (orthonormal, det +1) to this matrix, by SVD.</summary>
    public Mat3d NearestRotation()
    {
        var a = DenseMatrix.FromMat3(this);
        var svd = Svd.Decompose(a);
        var r = DenseMatrix.Multiply(svd.U, svd.V.Transposed());
        var rot = r.ToMat3();
        if (rot.Determinant < 0)
        {
            var v = svd.V.Clone();
            for (var i = 0; i < 3; i++)
                v[i, 2] = -v[i, 2];
            rot = DenseMatrix.Multiply(svd.U, v.Transposed()).ToMat3();
        }
        return rot;
    }
}
