using PlayMap.Core.Media;

namespace PlayMap.Media.Frames;

/// <summary>A source's counters and its decode-time distribution (the last 128 frames). Written by the
/// producer thread, read by STATE at a low rate; the percentile is computed on a stack copy, so a read
/// allocates only the health record it returns.</summary>
internal sealed class SourceStats
{
    private const int Window = 128;
    private readonly Lock _gate = new();
    private readonly double[] _decodeMs = new double[Window];
    private long _samples;
    private long _decoded;
    private long _dropped;
    private long _late;

    public long Decoded => Interlocked.Read(ref _decoded);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Late => Interlocked.Read(ref _late);

    /// <summary>A frame came out of the decoder (shown, queued or discarded on the way to a seek target).</summary>
    public void FrameDecoded() => Interlocked.Increment(ref _decoded);

    /// <summary>The time a frame cost: its share of demux and decode plus its conversion into the pool.</summary>
    public void DecodeTime(double ms)
    {
        lock (_gate)
        {
            _decodeMs[_samples % Window] = ms;
            _samples++;
        }
    }

    public void FrameDropped() => Interlocked.Increment(ref _dropped);

    public void FrameLate() => Interlocked.Increment(ref _late);

    /// <summary>The 95th percentile of the recent decode times in milliseconds (0 before any frame).</summary>
    public double DecodeMsP95()
    {
        Span<double> copy = stackalloc double[Window];
        int n;
        lock (_gate)
        {
            n = (int)Math.Min(_samples, Window);
            _decodeMs.AsSpan(0, n).CopyTo(copy);
        }
        if (n == 0)
            return 0;
        var used = copy[..n];
        used.Sort();
        var index = (int)Math.Ceiling(0.95 * n) - 1;
        return used[Math.Clamp(index, 0, n - 1)];
    }

    public SourceHealth Health(bool ready, string status, string decoder, bool hardware) =>
        new(ready, status, Decoded, Dropped, Late, Math.Round(DecodeMsP95(), 3), decoder, hardware);
}
