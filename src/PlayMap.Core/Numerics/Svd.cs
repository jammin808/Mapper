namespace PlayMap.Core.Numerics;

/// <summary>A singular value decomposition A = U·diag(S)·Vᵀ with the singular values sorted descending.
/// U is m × n (thin), S has n values, V is n × n.</summary>
public sealed record SvdResult(DenseMatrix U, double[] S, DenseMatrix V)
{
    /// <summary>The right singular vector of the smallest singular value: the least-squares solution of
    /// A·x = 0 with |x| = 1 — what every DLT asks for.</summary>
    public double[] NullVector() => V.Column(V.Cols - 1);

    /// <summary>The ratio of the largest to the smallest singular value; infinity when singular.</summary>
    public double Condition => S[^1] > 0 ? S[0] / S[^1] : double.PositiveInfinity;
}

/// <summary>One-sided Jacobi SVD (Hestenes). Accurate to the last bits on the small dense systems
/// calibration uses, with no pivoting subtleties; a matrix with fewer rows than columns is padded with
/// zero rows so the null space of a DLT system (8 × 9 for four points) is found exactly.</summary>
public static class Svd
{
    public static SvdResult Decompose(DenseMatrix a, int maxSweeps = 60)
    {
        ArgumentNullException.ThrowIfNull(a);
        var n = a.Cols;
        var m = Math.Max(a.Rows, n);
        var u = new DenseMatrix(m, n);
        Array.Copy(a.Data, u.Data, a.Data.Length);
        var v = DenseMatrix.Identity(n);

        for (var sweep = 0; sweep < maxSweeps; sweep++)
        {
            var rotated = false;
            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    double alpha = 0, beta = 0, gamma = 0;
                    for (var i = 0; i < m; i++)
                    {
                        var up = u.Data[i * n + p];
                        var uq = u.Data[i * n + q];
                        alpha += up * up;
                        beta += uq * uq;
                        gamma += up * uq;
                    }
                    if (Math.Abs(gamma) <= 1e-15 * Math.Sqrt(alpha * beta) || gamma == 0)
                        continue;
                    rotated = true;
                    var zeta = (beta - alpha) / (2 * gamma);
                    var t = Math.Sign(zeta) / (Math.Abs(zeta) + Math.Sqrt(1 + zeta * zeta));
                    if (zeta == 0)
                        t = 1;
                    var c = 1 / Math.Sqrt(1 + t * t);
                    var s = c * t;
                    for (var i = 0; i < m; i++)
                    {
                        var up = u.Data[i * n + p];
                        var uq = u.Data[i * n + q];
                        u.Data[i * n + p] = c * up - s * uq;
                        u.Data[i * n + q] = s * up + c * uq;
                    }
                    for (var i = 0; i < n; i++)
                    {
                        var vp = v.Data[i * n + p];
                        var vq = v.Data[i * n + q];
                        v.Data[i * n + p] = c * vp - s * vq;
                        v.Data[i * n + q] = s * vp + c * vq;
                    }
                }
            }
            if (!rotated)
                break;
        }

        // The singular values are the column norms; normalise U's columns and sort descending.
        var sv = new double[n];
        for (var j = 0; j < n; j++)
        {
            double norm = 0;
            for (var i = 0; i < m; i++)
                norm += u.Data[i * n + j] * u.Data[i * n + j];
            sv[j] = Math.Sqrt(norm);
        }
        var order = Enumerable.Range(0, n).OrderByDescending(j => sv[j]).ToArray();
        var uo = new DenseMatrix(a.Rows, n);
        var vo = new DenseMatrix(n, n);
        var so = new double[n];
        for (var k = 0; k < n; k++)
        {
            var j = order[k];
            so[k] = sv[j];
            var inv = sv[j] > 1e-300 ? 1 / sv[j] : 0;
            for (var i = 0; i < a.Rows; i++)
                uo[i, k] = u.Data[i * n + j] * inv;
            for (var i = 0; i < n; i++)
                vo[i, k] = v.Data[i * n + j];
        }
        return new SvdResult(uo, so, vo);
    }
}

/// <summary>Eigen-decomposition of a symmetric matrix by cyclic Jacobi rotations; eigenvalues sorted
/// ascending with their eigenvectors as the columns of <c>Vectors</c>.</summary>
public static class SymmetricEigen
{
    public static (double[] Values, DenseMatrix Vectors) Decompose(DenseMatrix s, int maxSweeps = 100)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Rows != s.Cols)
            throw new ArgumentException("The matrix is not square.", nameof(s));
        var n = s.Rows;
        var a = s.Clone();
        var v = DenseMatrix.Identity(n);
        for (var sweep = 0; sweep < maxSweeps; sweep++)
        {
            double off = 0;
            for (var p = 0; p < n; p++)
                for (var q = p + 1; q < n; q++)
                    off += a[p, q] * a[p, q];
            if (off < 1e-30)
                break;
            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    var apq = a[p, q];
                    if (Math.Abs(apq) < 1e-300)
                        continue;
                    var theta = (a[q, q] - a[p, p]) / (2 * apq);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0)
                        t = 1;
                    var c = 1 / Math.Sqrt(t * t + 1);
                    var sn = t * c;
                    for (var k = 0; k < n; k++)
                    {
                        var akp = a[k, p];
                        var akq = a[k, q];
                        a[k, p] = c * akp - sn * akq;
                        a[k, q] = sn * akp + c * akq;
                    }
                    for (var k = 0; k < n; k++)
                    {
                        var apk = a[p, k];
                        var aqk = a[q, k];
                        a[p, k] = c * apk - sn * aqk;
                        a[q, k] = sn * apk + c * aqk;
                    }
                    for (var k = 0; k < n; k++)
                    {
                        var vkp = v[k, p];
                        var vkq = v[k, q];
                        v[k, p] = c * vkp - sn * vkq;
                        v[k, q] = sn * vkp + c * vkq;
                    }
                }
            }
        }
        var values = new double[n];
        for (var i = 0; i < n; i++)
            values[i] = a[i, i];
        var order = Enumerable.Range(0, n).OrderBy(i => values[i]).ToArray();
        var vs = new double[n];
        var vecs = new DenseMatrix(n, n);
        for (var k = 0; k < n; k++)
        {
            vs[k] = values[order[k]];
            for (var i = 0; i < n; i++)
                vecs[i, k] = v[i, order[k]];
        }
        return (vs, vecs);
    }
}
