using PlayMap.Core.Media;
using PlayMap.Core.Model;

namespace PlayMap.Render.Passes;

/// <summary>Which point of the pipeline a send reads.</summary>
public enum SendTapKind { Output, Canvas, Source }

/// <summary>One send as the render thread runs it: what it taps, at what size, format and rate, and the sender it offers packed
/// frames to. Built off the render thread when the project or a sender changes and published whole (an immutable array of these);
/// the render thread reads the fields and writes only the counters, which STATE reads. The sink arrives later than the entry (a
/// sender binds sockets off the render thread): an entry with no sink yet is skipped.</summary>
public sealed class SendPlanEntry
{
    private ISendSink? _sink;
    private long _packed;
    private long _skippedUnwatched;
    private long _busy;
    private long _blank;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required SendTapKind Kind { get; init; }

    /// <summary>The output or canvas id the tap reads (Output, Canvas).</summary>
    public string Target { get; init; } = "";

    /// <summary>The source the tap draws (Source).</summary>
    public SourceRef? Source { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Uyvy, Uyva or Bgra8 (resolved from the send's own format and the tap).</summary>
    public required SendFormat Format { get; init; }

    public required int FpsN { get; init; }

    public required int FpsD { get; init; }

    public required SendFit Fit { get; init; }

    /// <summary>Pack and read back only while a receiver is connected.</summary>
    public bool OnlyWhenWatched { get; init; } = true;

    /// <summary>Blackout reaches a canvas or source tap too (an output tap is the output's own picture).</summary>
    public bool FollowAir { get; init; } = true;

    /// <summary>The sender, once made; null while it binds.</summary>
    public ISendSink? Sink
    {
        get => Volatile.Read(ref _sink);
        set => Volatile.Write(ref _sink, value);
    }

    /// <summary>The leader-time frame index last sent (render thread).</summary>
    internal long LastK { get; set; } = long.MinValue;

    /// <summary>Frames packed and handed to the sender.</summary>
    public long Packed => Volatile.Read(ref _packed);

    /// <summary>Frames due while nobody watched (no pack, no read-back).</summary>
    public long SkippedUnwatched => Volatile.Read(ref _skippedUnwatched);

    /// <summary>Frames due while every read-back slot was still held by the sender.</summary>
    public long Busy => Volatile.Read(ref _busy);

    /// <summary>Frames due while the tap had no picture (an output off, a canvas not drawn): sent black.</summary>
    public long Blank => Volatile.Read(ref _blank);

    internal void CountPacked() => Interlocked.Increment(ref _packed);

    internal void CountUnwatched() => Interlocked.Increment(ref _skippedUnwatched);

    internal void CountBusy() => Interlocked.Increment(ref _busy);

    internal void CountBlank() => Interlocked.Increment(ref _blank);

    /// <summary>The bytes of one packed frame of this entry, as the sender receives it.</summary>
    public long FrameBytes => Format switch
    {
        SendFormat.Uyvy => LivePlanes.FrameBytes(PixelLayout.Uyvy, Width, Height),
        SendFormat.Uyva => LivePlanes.FrameBytes(PixelLayout.Uyva, Width, Height),
        _ => 4L * Width * Height,
    };

    /// <summary>Whether a frame is due at a leader instant: frame k = ⌊leaderNs·N / (D·10⁹)⌋ fires once, when k passes the last
    /// sent, so nodes on one show clock send on one grid and a slower send decimates exactly. Render thread.</summary>
    public bool Due(long leaderNs)
    {
        if (FpsN <= 0 || FpsD <= 0)
            return false;
        var k = (long)Math.Floor(leaderNs * (double)FpsN / (FpsD * 1e9));
        if (k <= LastK)
            return false;
        LastK = k;
        return true;
    }
}
