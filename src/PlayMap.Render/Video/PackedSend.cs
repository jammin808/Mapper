using System.Numerics;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Video;

/// <summary>One read-back of a packed send frame, handed to the sender as a <see cref="SendFrame"/>: its bytes are the read-back's
/// own memory in place, valid until <see cref="Release"/> (any thread, once), which hands the read's slot back to its ring — the ring
/// frees it on the render thread at its next collect, so the sender reads the GPU's bytes in place and the render thread copies
/// nothing.</summary>
public sealed class ReadbackFrame : SendFrame
{
    private readonly IHeldReadbackRing _ring;
    private readonly int _slot;
    private int _released = 1;

    internal ReadbackFrame(IHeldReadbackRing ring, int slot)
    {
        _ring = ring;
        _slot = slot;
    }

    internal void Arm(nint data, int bytes, int stride, int width, int height, SendFormat format, long leaderNs, int fpsN, int fpsD)
    {
        Data = data;
        Bytes = bytes;
        Stride = stride;
        Width = width;
        Height = height;
        Format = format;
        LeaderNs = leaderNs;
        FpsN = fpsN;
        FpsD = fpsD;
        Transfer = TransferFunction.Sdr;
        Volatile.Write(ref _released, 0);
    }

    /// <summary>True once the sender let go (or before the frame was ever handed out).</summary>
    internal bool Released => Volatile.Read(ref _released) != 0;

    public override void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _ring.Release(_slot);
    }
}

/// <summary>Reads packed send frames back without stalling the render thread, through a read-back ring the sender reads in place
/// (<see cref="IGpuDevice.CreateHeldReadbackRing"/>): each request reads the packed target into a free slot; <see cref="Collect"/>
/// offers each finished read to the sender as a <see cref="ReadbackFrame"/>, held until the sender releases it — then the ring's next
/// collect frees the slot. A request with no free slot is refused (the caller counts it busy): the render thread never waits for
/// the GPU or the network. Render thread only, except <see cref="ReadbackFrame.Release"/>.</summary>
public sealed class SendReadbackRing : IDisposable
{
    private sealed class Wire
    {
        public int FrameBytes;
        public int Stride;
        public int Width;
        public int Height;
        public SendFormat Format;
        public long LeaderNs;
        public int FpsN;
        public int FpsD;
        public required ReadbackFrame Frame { get; init; }
    }

    private readonly IHeldReadbackRing _ring;
    private readonly Wire[] _wires;
    private bool _disposed;

    public SendReadbackRing(IGpuDevice device, int slots = 4)
    {
        ArgumentNullException.ThrowIfNull(device);
        _ring = device.CreateHeldReadbackRing(slots);
        _wires = new Wire[_ring.Slots];
        for (var i = 0; i < _wires.Length; i++)
            _wires[i] = new Wire { Frame = new ReadbackFrame(_ring, i) };
    }

    /// <summary>Slots the sender holds now (handed, not released).</summary>
    public int Handed => _ring.Held;

    public bool HasFree => _ring.HasFree;

    /// <summary>Queues a read of a packed target (RGBA bytes, top row first). <paramref name="frameBytes"/> and
    /// <paramref name="stride"/> describe the wire frame the sender receives (the read may be longer: UYVA's last target row).
    /// False when every slot is in use.</summary>
    public bool Request(RenderTarget packed, SendFormat format, int width, int height, int frameBytes, int stride, long leaderNs, int fpsN, int fpsD)
    {
        ArgumentNullException.ThrowIfNull(packed);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ring.Request(packed, leaderNs, out var slot))
            return false;
        var w = _wires[slot];
        w.FrameBytes = Math.Min(frameBytes, packed.Width * packed.Height * 4);
        w.Stride = stride;
        w.Width = width;
        w.Height = height;
        w.Format = format;
        w.LeaderNs = leaderNs;
        w.FpsN = fpsN;
        w.FpsD = fpsD;
        return true;
    }

    /// <summary>Frees the slots the sender released, then offers every finished read to the sender, oldest first (with no sender the
    /// read is let go at once). Never waits.</summary>
    public int Collect(ISendSink? sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var offered = 0;
        while (_ring.TryTake(out var read))
        {
            var w = _wires[read.Slot];
            if (sink is null)
            {
                _ring.Release(read.Slot);
                continue;
            }
            w.Frame.Arm(read.Data, w.FrameBytes, w.Stride, w.Width, w.Height, w.Format, w.LeaderNs, w.FpsN, w.FpsD);
            sink.Offer(w.Frame);
            offered++;
        }
        return offered;
    }

    /// <summary>Lets the ring go once no sender holds a frame; false (nothing freed) while one still does — the caller asks again a
    /// frame later.</summary>
    public bool TryDispose()
    {
        if (_disposed)
            return true;
        if (_ring.Held > 0)
            return false;
        Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ring.Dispose();
    }
}

/// <summary>The send pack pass: a picture scaled to the send's size (fit with black bars, fill cropped, or stretched) and packed by
/// <c>video.pack</c> into an RGBA8 target whose read-back bytes are the wire frame (<see cref="SendFormat.Uyvy"/>: w/2 × h;
/// <see cref="SendFormat.Uyva"/>: w/2 × (h + ⌈h/2⌉), the alpha plane in the rows below h; <see cref="SendFormat.P216"/> and
/// <see cref="SendFormat.Pa16"/>: w/2 × 2h and 3h, one plane row a target row, 16-bit samples as byte pairs; <see cref="SendFormat.Bgra8"/>:
/// w × h).
/// Each send keeps its own packed target (keyed by the caller's key), so a send that holds re-reads its last frame unchanged; the
/// scaling targets are shared per size. Every draw is a pass of its own on the frame's list. Nothing is allocated per frame once warm.
/// Render thread.</summary>
public sealed class PackPass : IDisposable
{
    /// <summary>The scale and the pack write their targets whole, opaque.</summary>
    private static readonly DrawState Drawn = DrawState.Opaque;

    private static readonly PassDesc Black = FrameLists.Clear(0, 0, 0, 1);

    private readonly IGpuDevice _device;
    private readonly ProgramId _packProgram;
    private readonly ProgramId _blit;
    private readonly Dictionary<object, RenderTarget> _packed = new(ReferenceEqualityComparer.Instance);
    // Shared per size and kept while used: a size no pack has used for KeepFrames render frames (half a minute at 60 Hz, longer than
    // any send's own period but the slowest) is let go, so a send resized while dragged leaves nothing behind.
    private const long KeepFrames = 1_800;
    private const long SweepEvery = 300;
    private readonly Dictionary<(int W, int H), (RenderTarget Target, long Used)> _scaled = [];
    private readonly Dictionary<(int W, int H), (RenderTarget Target, long Used)> _black = [];
    private readonly List<(int W, int H)> _stale = [];
    private long _frame;

    public PackPass(IGpuDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _packProgram = device.Programs.Id("video.pack");
        _blit = device.Programs.Id("blit");
    }

    /// <summary>The wire frame's bytes and its first plane's row for a send of this shape.</summary>
    public static (int Bytes, int Stride) WireShape(SendFormat format, int width, int height) => format switch
    {
        SendFormat.Uyvy => (2 * width * height, 2 * width),
        SendFormat.Uyva => (3 * width * height, 2 * width),
        SendFormat.P216 => (4 * width * height, 2 * width),
        SendFormat.Pa16 => (6 * width * height, 2 * width),
        _ => (4 * width * height, 4 * width),
    };

    /// <summary>The send's packed target as last packed (null before its first pack, or when its shape changed since).</summary>
    public RenderTarget? Last(object key, int width, int height, SendFormat format)
    {
        var (tw, th) = TargetSize(format, width, height);
        return _packed.TryGetValue(key, out var t) && t.Width == tw && t.Height == th ? t : null;
    }

    /// <summary>Lets go of a send's packed target (the send left the plan).</summary>
    public void Forget(object key)
    {
        if (_packed.Remove(key, out var t))
            t.Dispose();
    }

    /// <summary>The packed target's size for a send of this shape (its planes stacked in rows).</summary>
    public static (int W, int H) TargetSize(SendFormat format, int width, int height) => format switch
    {
        SendFormat.Uyvy => (width / 2, height),
        SendFormat.Uyva => (width / 2, height + (height + 1) / 2),
        SendFormat.P216 => (width / 2, 2 * height),
        SendFormat.Pa16 => (width / 2, 3 * height),
        _ => (width, height),
    };

    /// <summary>Scales <paramref name="picture"/> (null = black) to the send's size and packs it into the send's own target
    /// (<paramref name="key"/>), its colour times <paramref name="gain"/> (the air's, for a send that follows it; alpha untouched),
    /// through the frame's <paramref name="list"/>; answers the packed target to read.</summary>
    public RenderTarget Pack(ICommandList list, object key, GpuTexture? picture, int width, int height, SendFormat format, SendFit fit,
        float gain = 1f)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(key);
        var source = Scale(list, picture, width, height, fit);
        var (tw, th) = TargetSize(format, width, height);
        if (!_packed.TryGetValue(key, out var target) || target.Width != tw || target.Height != th)
        {
            target?.Dispose();
            target = _device.CreateTarget(new TargetDesc(tw, th, TextureFormat.Rgba8, Filter: TextureFilter.Nearest));
            _packed[key] = target;
        }
        using (list.Pass(target, PassDesc.Keep))
            list.Draw(_packProgram, Drawn)
                .SetInt("uMode", format switch { SendFormat.Uyvy => 0, SendFormat.Uyva => 1, SendFormat.P216 or SendFormat.Pa16 => 3, _ => 2 })
                .SetInt("uWidth", width)
                .SetInt("uHeight", height)
                .Set("uGain", Math.Clamp(gain, 0f, 1f))
                .Texture("uPicture", source)
                .Fullscreen();
        return target;
    }

    private GpuTexture Scale(ICommandList list, GpuTexture? picture, int width, int height, SendFit fit)
    {
        if (picture is null)
        {
            // One black picture per size, kept: two sends of different sizes with nothing to show make nothing per frame.
            if (!_black.TryGetValue((width, height), out var kept))
            {
                var made = _device.CreateTarget(new TargetDesc(width, height, TextureFormat.Rgba8, Filter: TextureFilter.Nearest));
                using (list.Pass(made, Black))
                {
                    // The pass's clear is the whole of it.
                }
                kept = (made, 0);
            }
            _black[(width, height)] = (kept.Target, _frame);
            return kept.Target.Color;
        }
        if (picture.Width == width && picture.Height == height)
            return picture;
        var target = _scaled.TryGetValue((width, height), out var scaled)
            ? scaled.Target
            : _device.CreateTarget(new TargetDesc(width, height, TextureFormat.Rgba16F, Filter: TextureFilter.Nearest));
        _scaled[(width, height)] = (target, _frame);
        var src = new Vector4(0, 0, 1, 1);
        int vx = 0, vy = 0, vw = width, vh = height;
        var srcAspect = picture.Width / (double)picture.Height;
        var dstAspect = width / (double)height;
        if (fit == SendFit.Fit && Math.Abs(srcAspect - dstAspect) > 1e-6)
        {
            if (srcAspect > dstAspect)
            {
                vh = (int)Math.Round(width / srcAspect);
                vy = (height - vh) / 2;
            }
            else
            {
                vw = (int)Math.Round(height * srcAspect);
                vx = (width - vw) / 2;
            }
        }
        else if (fit == SendFit.Fill && Math.Abs(srcAspect - dstAspect) > 1e-6)
        {
            if (srcAspect > dstAspect)
            {
                var keep = (float)(dstAspect / srcAspect);
                src = new Vector4((1 - keep) / 2, 0, keep, 1);
            }
            else
            {
                var keep = (float)(srcAspect / dstAspect);
                src = new Vector4(0, (1 - keep) / 2, 1, keep);
            }
        }
        // The whole target cleared black, the picture drawn into its rectangle.
        using (list.Pass(target, Black with { X = vx, Y = vy, Width = Math.Max(1, vw), Height = Math.Max(1, vh) }))
            list.Draw(_blit, Drawn).Set("uSrc", src).Texture("uTex", picture).Fullscreen();
        return target.Color;
    }

    public void Dispose()
    {
        foreach (var t in _packed.Values)
            t.Dispose();
        _packed.Clear();
        foreach (var t in _scaled.Values)
            t.Target.Dispose();
        _scaled.Clear();
        foreach (var t in _black.Values)
            t.Target.Dispose();
        _black.Clear();
    }

    /// <summary>Once a render frame, before its packs: the frame's number, and every <see cref="SweepEvery"/> frames the shared
    /// pictures of sizes no pack used for <see cref="KeepFrames"/> frames let go.</summary>
    public void BeginFrame(long frame)
    {
        _frame = frame;
        if (frame % SweepEvery != 0)
            return;
        Sweep(_scaled);
        Sweep(_black);
    }

    /// <summary>Lets go of every shared scale and black picture (no send is left).</summary>
    public void Trim()
    {
        if (_scaled.Count == 0 && _black.Count == 0)
            return;
        foreach (var t in _scaled.Values)
            t.Target.Dispose();
        _scaled.Clear();
        foreach (var t in _black.Values)
            t.Target.Dispose();
        _black.Clear();
    }

    private void Sweep(Dictionary<(int W, int H), (RenderTarget Target, long Used)> cache)
    {
        _stale.Clear();
        foreach (var (size, entry) in cache)
            if (_frame - entry.Used >= KeepFrames)
                _stale.Add(size);
        foreach (var size in _stale)
        {
            cache[size].Target.Dispose();
            cache.Remove(size);
        }
    }
}
