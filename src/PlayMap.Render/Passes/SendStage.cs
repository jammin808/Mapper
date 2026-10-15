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

    /// <summary>Uyvy, Uyva, P216, Pa16 or Bgra8 (resolved from the send's own format and the tap).</summary>
    public required SendFormat Format { get; init; }

    public required int FpsN { get; init; }

    public required int FpsD { get; init; }

    public required SendFit Fit { get; init; }

    /// <summary>Pack and read back only while a receiver is connected.</summary>
    public bool OnlyWhenWatched { get; init; } = true;

    /// <summary>A canvas or source tap takes what the air does to the outputs: blackout, the pattern, the safe look, holds and the
    /// air's gain (an output tap is the output's own picture, which has them already).</summary>
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

    /// <summary>Why the send cannot be packed on this GPU (its packed target is larger than the GPU's largest texture), or "": set once
    /// by the render thread, read by STATE.</summary>
    public string Refused { get; private set; } = "";

    internal void Refuse(string words) => Refused = words;

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
        SendFormat.P216 => LivePlanes.FrameBytes(PixelLayout.P216, Width, Height),
        SendFormat.Pa16 => LivePlanes.FrameBytes(PixelLayout.Pa16, Width, Height),
        _ => 4L * Width * Height,
    };

    /// <summary>Whether a frame is due at a leader instant: frame k = ⌊leaderNs·N / (D·10⁹)⌋ fires once, when k passes the last
    /// sent, so nodes on one show clock send on one grid and a slower send decimates exactly. Render thread.</summary>
    /// <summary>How far back (in frames of the send's grid) leader time may jitter before it counts as a step that re-arms the grid.</summary>
    public const long RearmFrames = 2;

    public bool Due(long leaderNs)
    {
        if (FpsN <= 0 || FpsD <= 0)
            return false;
        var k = (long)Math.Floor(leaderNs * (double)FpsN / (FpsD * 1e9));
        // Leader time that went back more than a couple of frames (a node that joined a leader with an earlier epoch) re-arms the
        // grid at once, rather than holding the send until time passes where it was; a frame or two back is jitter, not a step.
        if (k <= LastK && LastK - k <= RearmFrames)
            return false;
        LastK = k;
        return true;
    }
}

/// <summary>The sends at the end of every frame (after every output is drawn, before any present): for each entry of the published
/// plan, finished read-backs are offered to its sender; a frame due on the leader-time grid is tapped (an output's final picture, a
/// canvas, a source), scaled, packed and queued for read-back — unless nobody watches and the send packs only for watchers (counted,
/// no GPU work), or every read-back slot is still with the sender (counted busy). An output tap that has no picture this frame (an
/// output disabled, or not drawn) sends black (counted blank). A canvas or source tap that follows the air takes what every output
/// takes: black in blackout, the test pattern or the safe look while it covers every output, a hold while every output holds, and
/// the brightest output's gain (master × panic × degrade), so it is dark only when every output is. Render thread; never blocks;
/// nothing is allocated per frame once warm (a ring is made once per entry and retired once the plan drops it and its sender let go
/// of every frame).</summary>
public sealed class SendStage(Engine.RenderEngine renderer) : Engine.IFramePass
{
    private readonly Video.PackPass _pack = new(renderer.Device);
    private readonly Dictionary<SendPlanEntry, Video.SendReadbackRing> _rings = new(ReferenceEqualityComparer.Instance);
    private readonly List<SendPlanEntry> _gone = [];
    private readonly List<Video.SendReadbackRing> _retiring = [];
    private readonly HashSet<SendPlanEntry> _live = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;
    private float _airGain = 1;
    private SourceRef? _airSafe;
    private bool _airHeld;

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
        ReadAir(in frame);
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
            // A packed target larger than this GPU's textures is refused once, in words, never thrown on the render thread each frame.
            if (entry.Refused.Length > 0 || TooLarge(entry, frame.Device))
                continue;
            // A hold on every output that shows the show (FREEZE, or a FREEZE outcome) holds a send that follows the air as it holds an
            // output: its last packed frame goes out again, unchanged (a send first packed during the hold holds its first frame).
            var follows = entry.Kind != SendTapKind.Output && entry.FollowAir;
            var held = follows && _airHeld && !frame.Blackout
                ? _pack.Last(entry, entry.Width, entry.Height, entry.Format)
                : null;
            Gpu.RenderTarget packed;
            if (held is not null)
            {
                packed = held;
            }
            else
            {
                var picture = Tap(entry, in frame, out var fit);
                if (picture is null)
                    entry.CountBlank();
                packed = _pack.Pack(entry, picture, entry.Width, entry.Height, entry.Format, fit, follows ? _airGain : 1f);
            }
            var (bytes, stride) = Video.PackPass.WireShape(entry.Format, entry.Width, entry.Height);
            ring.Request(packed, entry.Format, entry.Width, entry.Height, bytes, stride, frame.LeaderNs, entry.FpsN, entry.FpsD);
            entry.CountPacked();
        }
    }

    /// <summary>What the air does to the show this frame, from the enabled outputs that show it (no safe look on them): the brightest
    /// of their gains, and whether all of them hold. When no output shows the show (every one on a safe look), the safe look if they
    /// all show the same one, at the brightest of their gains, else black. With no enabled output (or no levels) the air is untouched
    /// but for FREEZE. Allocation-free.</summary>
    private void ReadAir(in Engine.PassFrame frame)
    {
        _airGain = 1;
        _airSafe = null;
        _airHeld = frame.Freeze;
        if (frame.Levels is not { } levels)
            return;
        var any = false;
        var showing = false;
        var showGain = 0f;
        var allHeld = true;
        var safeGain = 0f;
        SourceRef? safe = null;
        var oneLook = true;
        foreach (var o in frame.Project.Outputs)
        {
            if (!o.Enabled)
                continue;
            any = true;
            var gain = levels.Gain(o.Id, frame.LeaderNs);
            if (levels.Override(o.Id, frame.LeaderNs) is { IsNone: false } look)
            {
                safeGain = Math.Max(safeGain, gain);
                if (safe is null)
                    safe = look;
                else if (!Equals(safe, look))
                    oneLook = false;
                continue;
            }
            showing = true;
            showGain = Math.Max(showGain, gain);
            allHeld &= frame.Held(o.Id);
        }
        if (!any)
            return;
        if (showing)
        {
            _airGain = Math.Clamp(showGain, 0f, 1f);
            _airHeld = allHeld;
            return;
        }
        _airSafe = oneLook ? safe : null;
        _airGain = oneLook ? Math.Clamp(safeGain, 0f, 1f) : 0f;
        _airHeld = false;
    }

    /// <summary>Whether this entry's targets (its packed target, and the picture scaled to its size) are larger than this GPU's
    /// largest texture; refuses it once, in words, when they are.</summary>
    private static bool TooLarge(SendPlanEntry entry, Gpu.GlDevice device)
    {
        var (tw, th) = Video.PackPass.TargetSize(entry.Format, entry.Width, entry.Height);
        var most = device.MaxTextureSize;
        if (Math.Max(Math.Max(tw, th), Math.Max(entry.Width, entry.Height)) <= most)
            return false;
        entry.Refuse(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{entry.Name}: {entry.Width}×{entry.Height} packs into a {tw}×{th} target, larger than this GPU's {most}"));
        return true;
    }

    /// <summary>The tap's picture this frame, or null for black. An output tap is the output's final raster, so blackout, a test
    /// pattern or a safe look on it, its gain and a hold reach the send exactly as they reach the output; a disabled output sends
    /// black. A canvas or source tap that follows the air is black in blackout and shows the air's test pattern or safe look while it
    /// covers every output.</summary>
    private Gpu.GpuTexture? Tap(SendPlanEntry entry, in Engine.PassFrame frame, out SendFit fit)
    {
        fit = entry.Fit;
        if (entry.Kind != SendTapKind.Output && entry.FollowAir)
        {
            if (frame.Blackout)
                return null;
            // As an output decides: the safe look before the pattern, each stretched over the whole picture as the output stage does.
            if (_airSafe is { } safe)
            {
                fit = SendFit.Stretch;
                return renderer.SourcePicture(safe, entry.Width, entry.Height);
            }
            if (frame.PatternOverride is { IsNone: false } pattern && frame.PatternOutputs.IsDefaultOrEmpty)
            {
                fit = SendFit.Stretch;
                return renderer.SourcePicture(pattern, entry.Width, entry.Height);
            }
            if (_airGain <= 0)
                return null;
        }
        return entry.Kind switch
        {
            SendTapKind.Output => frame.Project.Outputs.Find(entry.Target) is { Enabled: true } ? renderer.OutputPicture(entry.Target) : null,
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
            {
                _rings.Remove(e);
                _pack.Forget(e);
            }
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
