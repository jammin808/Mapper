namespace PlayMap.Core.Audio;

/// <summary>The FFT of a real sequence of a power-of-two length n, as a complex FFT of n/2 points and one unpacking pass: bins
/// 0..n/2 out. One instance per thread that uses it (its work buffers are its own); nothing is allocated after construction.</summary>
public sealed class RealFft
{
    private readonly int _n;
    private readonly int _half;
    private readonly int[] _reverse;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly double[] _postCos;
    private readonly double[] _postSin;
    private readonly double[] _zr;
    private readonly double[] _zi;

    public RealFft(int n)
    {
        if (n < 4 || (n & (n - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(n), n, "a real FFT's length is a power of two of at least 4");
        _n = n;
        _half = n / 2;
        _reverse = new int[_half];
        var bits = 0;
        while (1 << bits < _half)
            bits++;
        for (var i = 0; i < _half; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }
        _cos = new double[_half / 2 + 1];
        _sin = new double[_half / 2 + 1];
        for (var k = 0; k < _cos.Length; k++)
        {
            _cos[k] = Math.Cos(2 * Math.PI * k / _half);
            _sin[k] = -Math.Sin(2 * Math.PI * k / _half);
        }
        _postCos = new double[_half + 1];
        _postSin = new double[_half + 1];
        for (var k = 0; k <= _half; k++)
        {
            _postCos[k] = Math.Cos(2 * Math.PI * k / n);
            _postSin[k] = -Math.Sin(2 * Math.PI * k / n);
        }
        _zr = new double[_half];
        _zi = new double[_half];
    }

    public int Length => _n;

    /// <summary>The number of bins out: n/2 + 1 (DC to Nyquist).</summary>
    public int Bins => _half + 1;

    /// <summary>X[k] = Σ x[t]·e^(−2πikt/n) for k = 0..n/2, into <paramref name="re"/> and <paramref name="im"/>.</summary>
    public void Forward(ReadOnlySpan<double> x, Span<double> re, Span<double> im)
    {
        if (x.Length != _n)
            throw new ArgumentException($"the input holds {_n} samples", nameof(x));
        if (re.Length < Bins || im.Length < Bins)
            throw new ArgumentException($"the output holds {Bins} bins", nameof(re));
        // Pack the even samples as the real part and the odd ones as the imaginary part, in bit-reversed order.
        for (var i = 0; i < _half; i++)
        {
            var j = _reverse[i];
            _zr[j] = x[2 * i];
            _zi[j] = x[2 * i + 1];
        }
        Butterflies();
        // Unpack: X[k] = E[k] + W^k·O[k], with E and O the even and odd halves' spectra read from Z[k] and conj(Z[n/2−k]).
        for (var k = 0; k <= _half; k++)
        {
            var a = k == _half ? 0 : k;
            var b = k == 0 ? 0 : _half - k;
            var zr = _zr[a];
            var zi = _zi[a];
            var cr = _zr[b];
            var ci = -_zi[b];
            var er = 0.5 * (zr + cr);
            var ei = 0.5 * (zi + ci);
            // O = (Z − conj Z')/(2i) = ((zi − ci) − i(zr − cr))/2
            var or = 0.5 * (zi - ci);
            var oi = -0.5 * (zr - cr);
            var wr = _postCos[k];
            var wi = _postSin[k];
            re[k] = er + wr * or - wi * oi;
            im[k] = ei + wr * oi + wi * or;
        }
    }

    private void Butterflies()
    {
        for (var len = 2; len <= _half; len <<= 1)
        {
            var step = _half / len;
            var halfLen = len / 2;
            for (var start = 0; start < _half; start += len)
            {
                for (var k = 0; k < halfLen; k++)
                {
                    // k < len/2, so the twiddle's index stays below n/4 (the table's length).
                    var t = k * step;
                    var wr = _cos[t];
                    var wi = _sin[t];
                    var a = start + k;
                    var b = a + halfLen;
                    var tr = _zr[b] * wr - _zi[b] * wi;
                    var ti = _zr[b] * wi + _zi[b] * wr;
                    _zr[b] = _zr[a] - tr;
                    _zi[b] = _zi[a] - ti;
                    _zr[a] += tr;
                    _zi[a] += ti;
                }
            }
        }
    }
}
