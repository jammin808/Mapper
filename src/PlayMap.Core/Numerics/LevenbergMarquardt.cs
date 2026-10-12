namespace PlayMap.Core.Numerics;

/// <summary>A non-linear least-squares problem: n parameters, m residuals. The Jacobian is optional —
/// without one the solver differentiates numerically (central differences).</summary>
public interface ILeastSquaresProblem
{
    int ParameterCount { get; }
    int ResidualCount { get; }

    /// <summary>Writes the m residuals for the parameters.</summary>
    void Residuals(ReadOnlySpan<double> parameters, Span<double> residuals);

    /// <summary>Writes the m × n Jacobian (row-major) and returns true, or returns false to have the
    /// solver differentiate numerically.</summary>
    bool Jacobian(ReadOnlySpan<double> parameters, DenseMatrix jacobian);
}

/// <summary>How a solve ended and how good it is: the RMS of the residuals before and after.</summary>
public sealed record LmResult(double[] Parameters, double InitialRms, double FinalRms, int Iterations, bool Converged, string Reason);

/// <summary>Levenberg–Marquardt with Nielsen's damping update and Marquardt's diagonal scaling — the
/// refinement step after every closed-form estimate (projector pose and lens, homographies, ICP).</summary>
public static class LevenbergMarquardt
{
    public static LmResult Solve(ILeastSquaresProblem problem, ReadOnlySpan<double> initial,
        int maxIterations = 200, double tolerance = 1e-12)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var n = problem.ParameterCount;
        var m = problem.ResidualCount;
        if (initial.Length != n)
            throw new ArgumentException("The initial guess does not fit the problem.", nameof(initial));

        var p = initial.ToArray();
        var r = new double[m];
        problem.Residuals(p, r);
        var cost = SumSquares(r);
        var initialRms = Math.Sqrt(cost / Math.Max(1, m));
        var j = new DenseMatrix(m, n);
        var lambda = -1.0;
        var nu = 2.0;
        var step = new double[n];
        var trial = new double[n];
        var rTrial = new double[m];

        for (var iter = 0; iter < maxIterations; iter++)
        {
            if (!problem.Jacobian(p, j))
                NumericJacobian(problem, p, r, j);
            var jtj = j.NormalMatrix();
            var g = j.TransposeMultiply(r);
            var gmax = g.Max(Math.Abs);
            if (gmax < tolerance)
                return new LmResult(p, initialRms, Math.Sqrt(cost / Math.Max(1, m)), iter, true, "gradient vanished");
            // Marquardt's scaling already multiplies λ by each diagonal of JᵀJ, so λ itself starts small and
            // scale-free; the diagonal must not be folded in a second time.
            if (lambda < 0)
                lambda = 1e-3;

            var improved = false;
            for (var attempt = 0; attempt < 30 && !improved; attempt++)
            {
                var damped = jtj.Clone();
                for (var i = 0; i < n; i++)
                    damped[i, i] += lambda * Math.Max(jtj[i, i], 1e-12);
                for (var i = 0; i < n; i++)
                    g[i] = -g[i];
                var solved = LinearSolve.TryCholesky(damped, g, step);
                for (var i = 0; i < n; i++)
                    g[i] = -g[i];
                if (!solved)
                {
                    lambda *= nu;
                    nu *= 2;
                    continue;
                }
                var stepNorm = Math.Sqrt(SumSquares(step));
                var pNorm = Math.Sqrt(SumSquares(p));
                if (stepNorm <= tolerance * (pNorm + tolerance))
                    return new LmResult(p, initialRms, Math.Sqrt(cost / Math.Max(1, m)), iter, true, "step vanished");
                for (var i = 0; i < n; i++)
                    trial[i] = p[i] + step[i];
                problem.Residuals(trial, rTrial);
                var trialCost = SumSquares(rTrial);
                // Gain ratio: actual over predicted reduction (predicted = stepᵀ(λ·D·step − g)).
                double predicted = 0;
                for (var i = 0; i < n; i++)
                    predicted += step[i] * (lambda * Math.Max(jtj[i, i], 1e-12) * step[i] - g[i]);
                var rho = predicted > 0 ? (cost - trialCost) / predicted : -1;
                if (rho > 0 && !double.IsNaN(trialCost))
                {
                    var relative = (cost - trialCost) / Math.Max(cost, 1e-300);
                    Array.Copy(trial, p, n);
                    Array.Copy(rTrial, r, m);
                    cost = trialCost;
                    lambda *= Math.Max(1.0 / 3, 1 - Math.Pow(2 * rho - 1, 3));
                    nu = 2;
                    improved = true;
                    if (relative < tolerance)
                        return new LmResult(p, initialRms, Math.Sqrt(cost / Math.Max(1, m)), iter + 1, true, "cost settled");
                }
                else
                {
                    lambda *= nu;
                    nu *= 2;
                }
            }
            if (!improved)
                return new LmResult(p, initialRms, Math.Sqrt(cost / Math.Max(1, m)), iter, true, "no further descent");
        }
        return new LmResult(p, initialRms, Math.Sqrt(cost / Math.Max(1, m)), maxIterations, false, "iteration limit");
    }

    public static void NumericJacobian(ILeastSquaresProblem problem, double[] p, ReadOnlySpan<double> r0, DenseMatrix j)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(j);
        var n = p.Length;
        var m = r0.Length;
        var plus = new double[m];
        var minus = new double[m];
        for (var c = 0; c < n; c++)
        {
            var orig = p[c];
            var h = 1e-6 * Math.Max(1.0, Math.Abs(orig));
            p[c] = orig + h;
            problem.Residuals(p, plus);
            p[c] = orig - h;
            problem.Residuals(p, minus);
            p[c] = orig;
            for (var row = 0; row < m; row++)
                j[row, c] = (plus[row] - minus[row]) / (2 * h);
        }
    }

    private static double SumSquares(ReadOnlySpan<double> v)
    {
        double s = 0;
        foreach (var x in v)
            s += x * x;
        return s;
    }
}
