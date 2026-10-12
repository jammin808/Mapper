using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests;

/// <summary>The solvers every calibration stands on: SVD, the symmetric eigen-solver, least squares and
/// Levenberg–Marquardt, the rotation conversions and the seeded generator.</summary>
public class NumericsTests
{
    [Fact]
    public void TheSvdRebuildsTheMatrixAndSortsItsValues()
    {
        var a = new DenseMatrix(4, 3, [2, 0, 1, -1, 3, 2, 0, 1, 4, 5, 2, -3]);
        var svd = Svd.Decompose(a);
        Assert.True(svd.S[0] >= svd.S[1] && svd.S[1] >= svd.S[2]);
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                double v = 0;
                for (var k = 0; k < 3; k++)
                    v += svd.U[r, k] * svd.S[k] * svd.V[c, k];
                Assert.Equal(a[r, c], v, 9);
            }
        }
    }

    [Fact]
    public void TheNullVectorOfAWideSystemSolvesIt()
    {
        // Two equations, three unknowns: x + y + z = 0 and x − y = 0 → (1, 1, −2)/|·|.
        var a = new DenseMatrix(2, 3, [1, 1, 1, 1, -1, 0]);
        var x = Svd.Decompose(a).NullVector();
        var r = a.Multiply(x);
        Assert.True(Math.Abs(r[0]) < 1e-10 && Math.Abs(r[1]) < 1e-10);
        Assert.Equal(1, Math.Sqrt(x.Sum(v => v * v)), 10);
    }

    [Fact]
    public void TheEigenSolverFindsASymmetricMatrixsSpectrum()
    {
        var s = new DenseMatrix(3, 3, [4, 1, 0, 1, 3, 0, 0, 0, 1]);
        var (values, vectors) = SymmetricEigen.Decompose(s);
        Assert.Equal(1, values[0], 9);
        for (var k = 0; k < 3; k++)
        {
            var v = vectors.Column(k);
            var sv = s.Multiply(v);
            for (var i = 0; i < 3; i++)
                Assert.Equal(values[k] * v[i], sv[i], 9);
        }
    }

    [Fact]
    public void LeastSquaresFitsALine()
    {
        // y = 2x + 1 with symmetric noise.
        var a = new DenseMatrix(5, 2, [0, 1, 1, 1, 2, 1, 3, 1, 4, 1]);
        double[] b = [1.1, 2.9, 5.1, 6.9, 9.0];
        var x = LinearSolve.LeastSquares(a, b);
        Assert.Equal(2, x[0], 1);
        Assert.Equal(1, x[1], 1);
    }

    private sealed class Exponential(double[] xs, double[] ys) : ILeastSquaresProblem
    {
        public int ParameterCount => 2;
        public int ResidualCount => xs.Length;

        public void Residuals(ReadOnlySpan<double> p, Span<double> r)
        {
            for (var i = 0; i < xs.Length; i++)
                r[i] = p[0] * Math.Exp(p[1] * xs[i]) - ys[i];
        }

        public bool Jacobian(ReadOnlySpan<double> parameters, DenseMatrix jacobian) => false;
    }

    [Fact]
    public void LevenbergMarquardtRecoversAnExponential()
    {
        var xs = Enumerable.Range(0, 20).Select(i => i * 0.1).ToArray();
        var ys = xs.Select(x => 3 * Math.Exp(-1.5 * x)).ToArray();
        var result = LevenbergMarquardt.Solve(new Exponential(xs, ys), [1, 0]);
        Assert.True(result.Converged);
        Assert.Equal(3, result.Parameters[0], 6);
        Assert.Equal(-1.5, result.Parameters[1], 6);
        Assert.True(result.FinalRms < 1e-8);
    }

    [Fact]
    public void ARotationVectorSurvivesTheRoundTrip()
    {
        foreach (var r in new[] { new Vec3d(0.1, -0.2, 0.3), new Vec3d(0, 0, 3.1), new Vec3d(1e-9, 0, 0), new Vec3d(-2, 1, 0.5) })
        {
            var m = Mat3d.FromRotationVector(r);
            Assert.Equal(1, m.Determinant, 10);
            var back = m.ToRotationVector();
            var m2 = Mat3d.FromRotationVector(back);
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    Assert.Equal(m[i, j], m2[i, j], 8);
        }
    }

    [Fact]
    public void TheNearestRotationOfANoisyRotationIsARotation()
    {
        var m = Mat3d.FromRotationVector(new Vec3d(0.3, 0.2, -0.1)) + new Mat3d(0.01, 0, 0.02, 0, -0.01, 0, 0.005, 0, 0);
        var r = m.NearestRotation();
        Assert.Equal(1, r.Determinant, 9);
        var i = r * r.Transposed();
        Assert.Equal(1, i.M00, 9);
        Assert.Equal(0, i.M01, 9);
    }

    [Fact]
    public void TheGeneratorIsDeterministicAndCopiesWithItsState()
    {
        var a = new Pcg32(42);
        var b = new Pcg32(42);
        for (var i = 0; i < 100; i++)
            Assert.Equal(a.NextUInt(), b.NextUInt());
        var copy = a;
        Assert.Equal(a.NextDouble(), copy.NextDouble());
        var c = new Pcg32(43);
        Assert.NotEqual(new Pcg32(42).NextUInt(), c.NextUInt());
        var bounded = new Pcg32(7);
        for (var i = 0; i < 1000; i++)
            Assert.InRange(bounded.NextInt(10), 0, 9);
    }
}
