using System.Globalization;
using System.Text;

namespace PlayMap.Core.Numerics;

/// <summary>A dense row-major matrix of doubles for the solvers (DLT systems, normal equations,
/// Jacobians). Small and plain on purpose: calibration problems are hundreds of rows, not millions.</summary>
public sealed class DenseMatrix
{
    public int Rows { get; }
    public int Cols { get; }

    /// <summary>The values, row-major: element (r, c) is at r × Cols + c.</summary>
    public double[] Data { get; }

    public DenseMatrix(int rows, int cols)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        Rows = rows;
        Cols = cols;
        Data = new double[rows * cols];
    }

    public DenseMatrix(int rows, int cols, double[] data)
    {
        if (data.Length != rows * cols)
            throw new ArgumentException("The data does not fit the shape.", nameof(data));
        Rows = rows;
        Cols = cols;
        Data = data;
    }

    public double this[int r, int c]
    {
        get => Data[r * Cols + c];
        set => Data[r * Cols + c] = value;
    }

    public static DenseMatrix Identity(int n)
    {
        var m = new DenseMatrix(n, n);
        for (var i = 0; i < n; i++)
            m[i, i] = 1;
        return m;
    }

    public static DenseMatrix FromMat3(Mat3d m) => new(3, 3, m.ToArray());

    public Mat3d ToMat3()
    {
        if (Rows != 3 || Cols != 3)
            throw new InvalidOperationException("Not a 3×3 matrix.");
        return Mat3d.FromArray(Data);
    }

    public DenseMatrix Clone() => new(Rows, Cols, (double[])Data.Clone());

    public Span<double> RowSpan(int r) => Data.AsSpan(r * Cols, Cols);

    public double[] Column(int c)
    {
        var col = new double[Rows];
        for (var r = 0; r < Rows; r++)
            col[r] = this[r, c];
        return col;
    }

    public DenseMatrix Transposed()
    {
        var t = new DenseMatrix(Cols, Rows);
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
                t[c, r] = this[r, c];
        return t;
    }

    /// <summary>The product A·B.</summary>
    public static DenseMatrix Multiply(DenseMatrix a, DenseMatrix b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Cols != b.Rows)
            throw new ArgumentException("The shapes do not chain.");
        var m = new DenseMatrix(a.Rows, b.Cols);
        for (var r = 0; r < a.Rows; r++)
        {
            for (var k = 0; k < a.Cols; k++)
            {
                var v = a.Data[r * a.Cols + k];
                if (v == 0)
                    continue;
                var bo = k * b.Cols;
                var mo = r * m.Cols;
                for (var c = 0; c < b.Cols; c++)
                    m.Data[mo + c] += v * b.Data[bo + c];
            }
        }
        return m;
    }

    public double[] Multiply(ReadOnlySpan<double> x)
    {
        if (x.Length != Cols)
            throw new ArgumentException("The vector does not fit.", nameof(x));
        var y = new double[Rows];
        for (var r = 0; r < Rows; r++)
        {
            double s = 0;
            var o = r * Cols;
            for (var c = 0; c < Cols; c++)
                s += Data[o + c] * x[c];
            y[r] = s;
        }
        return y;
    }

    /// <summary>AᵀA, computed directly (symmetric, Cols × Cols).</summary>
    public DenseMatrix NormalMatrix()
    {
        var n = new DenseMatrix(Cols, Cols);
        for (var r = 0; r < Rows; r++)
        {
            var o = r * Cols;
            for (var i = 0; i < Cols; i++)
            {
                var ai = Data[o + i];
                if (ai == 0)
                    continue;
                for (var j = i; j < Cols; j++)
                    n.Data[i * Cols + j] += ai * Data[o + j];
            }
        }
        for (var i = 0; i < Cols; i++)
            for (var j = 0; j < i; j++)
                n.Data[i * Cols + j] = n.Data[j * Cols + i];
        return n;
    }

    /// <summary>Aᵀb.</summary>
    public double[] TransposeMultiply(ReadOnlySpan<double> b)
    {
        if (b.Length != Rows)
            throw new ArgumentException("The vector does not fit.", nameof(b));
        var y = new double[Cols];
        for (var r = 0; r < Rows; r++)
        {
            var br = b[r];
            if (br == 0)
                continue;
            var o = r * Cols;
            for (var c = 0; c < Cols; c++)
                y[c] += Data[o + c] * br;
        }
        return y;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                if (c > 0)
                    sb.Append(' ');
                sb.Append(this[r, c].ToString("0.######", CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
