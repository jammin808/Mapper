namespace PlayMap.Core.Numerics;

/// <summary>Dense linear solvers: Cholesky for the symmetric positive-definite normal equations the
/// Levenberg–Marquardt step makes, Householder QR for an over-determined least-squares system.</summary>
public static class LinearSolve
{
    /// <summary>Solves S·x = b for symmetric positive-definite S. Returns false when S is not
    /// positive-definite (a pivot at or below zero), leaving the caller to raise its damping.</summary>
    public static bool TryCholesky(DenseMatrix s, ReadOnlySpan<double> b, Span<double> x)
    {
        ArgumentNullException.ThrowIfNull(s);
        var n = s.Rows;
        if (s.Cols != n || b.Length != n || x.Length != n)
            throw new ArgumentException("The shapes do not fit.");
        var l = new double[n * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var sum = s[i, j];
                for (var k = 0; k < j; k++)
                    sum -= l[i * n + k] * l[j * n + k];
                if (i == j)
                {
                    if (sum <= 0 || double.IsNaN(sum))
                        return false;
                    l[i * n + i] = Math.Sqrt(sum);
                }
                else
                {
                    l[i * n + j] = sum / l[j * n + j];
                }
            }
        }
        // Forward: L·y = b, then back: Lᵀ·x = y.
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sum = b[i];
            for (var k = 0; k < i; k++)
                sum -= l[i * n + k] * y[k];
            y[i] = sum / l[i * n + i];
        }
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = y[i];
            for (var k = i + 1; k < n; k++)
                sum -= l[k * n + i] * x[k];
            x[i] = sum / l[i * n + i];
        }
        return true;
    }

    /// <summary>The least-squares solution of A·x ≈ b (rows ≥ cols) by Householder QR.</summary>
    public static double[] LeastSquares(DenseMatrix a, ReadOnlySpan<double> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        var m = a.Rows;
        var n = a.Cols;
        if (m < n)
            throw new ArgumentException("Fewer equations than unknowns.", nameof(a));
        if (b.Length != m)
            throw new ArgumentException("The vector does not fit.", nameof(b));
        var r = a.Clone();
        var qtb = b.ToArray();
        for (var k = 0; k < n; k++)
        {
            double norm = 0;
            for (var i = k; i < m; i++)
                norm += r[i, k] * r[i, k];
            norm = Math.Sqrt(norm);
            if (norm < 1e-300)
                continue;
            var alpha = r[k, k] > 0 ? -norm : norm;
            var v = new double[m - k];
            v[0] = r[k, k] - alpha;
            for (var i = k + 1; i < m; i++)
                v[i - k] = r[i, k];
            double vnorm2 = 0;
            foreach (var vi in v)
                vnorm2 += vi * vi;
            if (vnorm2 < 1e-300)
                continue;
            for (var j = k; j < n; j++)
            {
                double dot = 0;
                for (var i = k; i < m; i++)
                    dot += v[i - k] * r[i, j];
                var f = 2 * dot / vnorm2;
                for (var i = k; i < m; i++)
                    r[i, j] -= f * v[i - k];
            }
            double dotb = 0;
            for (var i = k; i < m; i++)
                dotb += v[i - k] * qtb[i];
            var fb = 2 * dotb / vnorm2;
            for (var i = k; i < m; i++)
                qtb[i] -= fb * v[i - k];
        }
        var x = new double[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = qtb[i];
            for (var j = i + 1; j < n; j++)
                sum -= r[i, j] * x[j];
            x[i] = Math.Abs(r[i, i]) > 1e-300 ? sum / r[i, i] : 0;
        }
        return x;
    }
}
