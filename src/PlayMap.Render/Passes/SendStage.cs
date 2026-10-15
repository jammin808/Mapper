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

/// <summary>The sends at the end of every frame (after every output is drawn, before any present): for each entry of the published
/// plan, finished read-backs are offered to its sender; a frame due on the leader-time grid is tapped (an output's final picture, a
/// canvas, a source), scaled, packed and queued for read-back — unless nobody watches and the send packs only for watchers (counted,
/// no GPU work), or every read-back slot is still with the sender (counted busy). An output tap that has no picture this frame sends
/// black (counted blank); blackout reaches canvas and source taps that follow the air. Render thread; never blocks; nothing is
/// allocated per frame once warm (a ring is made once per entry and retired once the plan drops it and its sender let go of every
/// frame).</summary>
public sealed class SendStage(Engine.RenderEngine renderer) : Engine.IFramePass
{
    private readonly Video.PackPass _pack = new(renderer.Device);
    private readonly Dictionary<SendPlanEntry, Video.SendReadbackRing> _rings = new(ReferenceEqualityComparer.Instance);
    private readonly List<SendPlanEntry> _gone = [];
    private readonly List<Video.SendReadbackRing> _retiring = [];
    private readonly HashSet<SendPlanEntry> _live = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public string Name => "videoio.sends";

    /// <summary>After every other pass: a send carries what the air shows.</summary>
    public int Order => 1_000;

    /// <summary>The plan this frame runs (set by the render host each frame from the published plan; a struct copy).</summary>
    public System.Collections.Immutable.ImmutableArray<SendPlanEntry> Plan { get; set; } = [];

    /// <summary>Whether anything is still held for a send (a ring with frames out, or retiring).</summary>
    public bool Busy => _rings.Count > 0 || _retiring.Count > 0;

    public void FrameEnd(in Engine.PassFrame frame)
    {
        if (_disposed)
            return;
        RetireGone();
        var plan = Plan;
        if (plan.IsDefaultOrEmpty)
            return;
        foreach (var entry in plan)
        {
            if (!_rings.TryGetValue(entry, out var ring))
            {
                ring = new Video.SendReadbackRing(frame.Device);
                _rings[entry] = ring;
            }
            var sink = entry.Sink;
            ring.Collect(sink);
            if (!entry.Due(frame.LeaderNs) || sink is null)
                continue;
            if (entry.OnlyWhenWatched && sink.Connections == 0)
            {
                entry.CountUnwatched();
                continue;
            }
            if (!ring.HasFree)
            {
                entry.CountBusy();
                continue;
            }
            var picture = Tap(entry, in frame);
            if (picture is null)
                entry.CountBlank();
            var packed = _pack.Pack(picture, entry.Width, entry.Height, entry.Format, entry.Fit);
            var (bytes, stride) = Video.PackPass.WireShape(entry.Format, entry.Width, entry.Height);
            ring.Request(packed, entry.Format, entry.Width, entry.Height, bytes, stride, frame.LeaderNs, entry.FpsN, entry.FpsD);
            entry.CountPacked();
        }
    }

    private Gpu.GpuTexture? Tap(SendPlanEntry entry, in Engine.PassFrame frame)
    {
        if (entry.Kind != SendTapKind.Output && entry.FollowAir && frame.Blackout)
            return null;
        return entry.Kind switch
        {
            SendTapKind.Output => renderer.OutputPicture(entry.Target),
            SendTapKind.Canvas => renderer.CanvasPicture(entry.Target),
            _ => entry.Source is { } source ? renderer.SourcePicture(source, entry.Width, entry.Height) : null,
        };
    }

    /// <summary>Rings whose entry left the plan: collected once more (frames released by a closed sender), then freed once no frame
    /// is out.</summary>
    private void RetireGone()
    {
        if (_rings.Count > 0)
        {
            _live.Clear();
            foreach (var e in Plan)
                _live.Add(e);
            foreach (var (entry, ring) in _rings)
            {
                if (_live.Contains(entry))
                    continue;
                ring.Collect(null);
                _retiring.Add(ring);
                _gone.Add(entry);
            }
            foreach (var e in _gone)
                _rings.Remove(e);
            _gone.Clear();
        }
        for (var i = _retiring.Count - 1; i >= 0; i--)
        {
            _retiring[i].Collect(null);
            if (_retiring[i].TryDispose())
                _retiring.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // A sender lets go of a frame within a fraction of a second (its worker sends the next or flushes an idle one); the GL objects
        // go only after, so no sender reads memory that is no longer mapped. A sender that never lets go is waited for a second.
        SpinWait.SpinUntil(() => _rings.Values.All(r => r.Handed == 0) && _retiring.All(r => r.Handed == 0), TimeSpan.FromSeconds(1));
        foreach (var ring in _rings.Values)
            ring.Dispose();
        _rings.Clear();
        foreach (var ring in _retiring)
            ring.Dispose();
        _retiring.Clear();
        _pack.Dispose();
    }
}
