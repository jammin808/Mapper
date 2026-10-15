namespace PlayMap.Core.Audio;

/// <summary>Sixteen band-pass filters (RBJ biquads, 0 dB peak gain) on band edges 30·1.4806^k Hz from 30 Hz to 16 kHz, centred on
/// each band's geometric middle with Q 2.53: a sine at a band's centre passes its own band at unity and its neighbours at about
/// −7 dB. A band's level is the RMS of its output × √2 (a full-scale sine at the centre reads 1), the power averaged with a time
/// constant of 1.5 periods of the band's centre (5 ms at least), so a low band reads steadily over hops shorter than its period.</summary>
public sealed class FilterBank
{
    public const int Count = 16;
    public const double LowHz = 30;
    public const double HighHz = 16000;
    public const double Q = 2.53;

    private readonly double[] _b0 = new double[Count];
    private readonly double[] _a1 = new double[Count];
    private readonly double[] _a2 = new double[Count];
    private readonly double[] _x1 = new double[Count];
    private readonly double[] _x2 = new double[Count];
    private readonly double[] _y1 = new double[Count];
    private readonly double[] _y2 = new double[Count];
    private readonly double[] _power = new double[Count];
    private readonly double[] _follow = new double[Count];

    public FilterBank(int sampleRate)
    {
        if (sampleRate <= 2 * HighHz)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "the filterbank needs a rate above 32 kHz");
        for (var k = 0; k < Count; k++)
        {
            var w0 = 2 * Math.PI * Centre(k) / sampleRate;
            var alpha = Math.Sin(w0) / (2 * Q);
            var a0 = 1 + alpha;
            _b0[k] = alpha / a0;
            _a1[k] = -2 * Math.Cos(w0) / a0;
            _a2[k] = (1 - alpha) / a0;
            var tau = Math.Max(0.005, 1.5 / Centre(k));
            _follow[k] = 1 - Math.Exp(-1 / (tau * sampleRate));
        }
    }

    /// <summary>The lower edge of band <paramref name="k"/> (k = 16 gives the top edge, 16 kHz).</summary>
    public static double Edge(int k) => LowHz * Math.Pow(HighHz / LowHz, k / (double)Count);

    /// <summary>The band's centre: the geometric middle of its edges.</summary>
    public static double Centre(int k) => Math.Sqrt(Edge(k) * Edge(k + 1));

    /// <summary>Runs one sample through every band (b1 = 0 and b2 = −b0 for this band-pass).</summary>
    public void Process(double x)
    {
        for (var k = 0; k < Count; k++)
        {
            var y = _b0[k] * (x - _x2[k]) - _a1[k] * _y1[k] - _a2[k] * _y2[k];
            _x2[k] = _x1[k];
            _x1[k] = x;
            _y2[k] = _y1[k];
            _y1[k] = y;
            _power[k] += (y * y - _power[k]) * _follow[k];
        }
    }

    /// <summary>Each band's level now (RMS × √2, clamped 0–1).</summary>
    public void TakeHop(Span<float> levels)
    {
        for (var k = 0; k < Count; k++)
            levels[k] = (float)Math.Clamp(Math.Sqrt(2 * _power[k]), 0, 1);
    }

    public void Reset()
    {
        Array.Clear(_x1);
        Array.Clear(_x2);
        Array.Clear(_y1);
        Array.Clear(_y2);
        Array.Clear(_power);
    }
}
