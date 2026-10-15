using System.Numerics;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Video;

/// <summary>One read-back of a packed send frame, handed to the sender as a <see cref="SendFrame"/>: its bytes are the pack buffer's
/// mapped memory, valid until <see cref="Release"/> (any thread, once). The ring unmaps the buffer on the render thread at its next
/// collect after the release, so the sender reads the GPU's bytes in place and the render thread copies nothing.</summary>
public sealed class ReadbackFrame : SendFrame
{
    private int _released = 1;

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

    public override void Release() => Volatile.Write(ref _released, 1);
}

/// <summary>Reads packed send frames back without stalling the render thread: each request reads the packed target into the next free
/// pixel-pack buffer with a fence behind it; <see cref="Collect"/> maps a buffer once its fence has passed and offers it to the sender,
/// and the buffer stays mapped (GL allows it while that buffer is not used by GL) until the sender releases the frame — then the next
/// collect unmaps it and frees the slot. A request with no free slot is refused (the caller counts it busy): the render thread never
/// waits for the GPU or the network. Render thread only, except <see cref="ReadbackFrame.Release"/>.</summary>
public sealed unsafe class SendReadbackRing : IDisposable
{
    private enum SlotState { Free, Reading, Handed }

    private sealed class Slot
    {
        public uint Buffer;
        public int Capacity;
        public nint Fence;
        public SlotState State;
        public long Sequence;
        public int ReadBytes;
        public int FrameBytes;
        public int Stride;
        public int Width;
        public int Height;
        public SendFormat Format;
        public long LeaderNs;
        public int FpsN;
        public int FpsD;
        public readonly ReadbackFrame Frame = new();
    }

    private readonly GlDevice _device;
    private readonly Slot[] _slots;
    private long _sequence;
    private bool _disposed;

    public SendReadbackRing(GlDevice device, int slots = 4)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _slots = new Slot[Math.Max(2, slots)];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new Slot();
    }

    /// <summary>Slots in flight on the GPU or held by the sender.</summary>
    public int InUse
    {
        get
        {
            var n = 0;
            foreach (var s in _slots)
                if (s.State != SlotState.Free)
                    n++;
            return n;
        }
    }

    /// <summary>Slots the sender holds now (mapped, not released).</summary>
    public int Handed
    {
        get
        {
            var n = 0;
            foreach (var s in _slots)
                if (s.State == SlotState.Handed && !s.Frame.Released)
                    n++;
            return n;
        }
    }

    public bool HasFree
    {
        get
        {
            foreach (var s in _slots)
                if (s.State == SlotState.Free)
                    return true;
            return false;
        }
    }

    /// <summary>Queues a read of a packed target (RGBA bytes, top row first). <paramref name="frameBytes"/> and
    /// <paramref name="stride"/> describe the wire frame the sender receives (the read may be longer: UYVA's last target row).
    /// False when every slot is in use.</summary>
    public bool Request(RenderTarget packed, SendFormat format, int width, int height, int frameBytes, int stride, long leaderNs, int fpsN, int fpsD)
    {
        ArgumentNullException.ThrowIfNull(packed);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Slot? slot = null;
        foreach (var s in _slots)
        {
            if (s.State == SlotState.Free)
            {
                slot = s;
                break;
            }
        }
        if (slot is null)
            return false;
        var readBytes = packed.Width * packed.Height * 4;
        if (slot.Buffer == 0 || slot.Capacity < readBytes)
        {
            if (slot.Buffer != 0)
                _device.DeleteBuffer(slot.Buffer);
            slot.Buffer = _device.CreatePixelBuffer(pack: true, readBytes);
            slot.Capacity = readBytes;
        }
        _device.ReadRgbaIntoPackBuffer(packed, slot.Buffer, sixteen: false);
        slot.Fence = _device.InsertFence();
        slot.State = SlotState.Reading;
        slot.Sequence = ++_sequence;
        slot.ReadBytes = readBytes;
        slot.FrameBytes = Math.Min(frameBytes, readBytes);
        slot.Stride = stride;
        slot.Width = width;
        slot.Height = height;
        slot.Format = format;
        slot.LeaderNs = leaderNs;
        slot.FpsN = fpsN;
        slot.FpsD = fpsD;
        return true;
    }

    /// <summary>Frees the slots the sender released, then offers every finished read to the sender, oldest first (with no sender the
    /// read is dropped and its slot freed). Never waits.</summary>
    public int Collect(ISendSink? sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var s in _slots)
        {
            if (s.State == SlotState.Handed && s.Frame.Released)
            {
                _device.UnmapPack(s.Buffer);
                s.State = SlotState.Free;
            }
        }
        var offered = 0;
        while (true)
        {
            Slot? oldest = null;
            foreach (var s in _slots)
                if (s.State == SlotState.Reading && (oldest is null || s.Sequence < oldest.Sequence))
                    oldest = s;
            if (oldest is null || !_device.IsSignaled(oldest.Fence))
                return offered;
            _device.DeleteFence(oldest.Fence);
            oldest.Fence = 0;
            if (sink is null)
            {
                oldest.State = SlotState.Free;
                continue;
            }
            var p = _device.MapPack(oldest.Buffer, oldest.ReadBytes);
            if (p is null)
            {
                oldest.State = SlotState.Free;
                continue;
            }
            oldest.Frame.Arm((nint)p, oldest.FrameBytes, oldest.Stride, oldest.Width, oldest.Height, oldest.Format, oldest.LeaderNs, oldest.FpsN, oldest.FpsD);
            oldest.State = SlotState.Handed;
            sink.Offer(oldest.Frame);
            offered++;
        }
    }

    /// <summary>Frees every GL object once no sender holds a frame; false (nothing freed) while one still does — the caller asks again
    /// a frame later.</summary>
    public bool TryDispose()
    {
        if (_disposed)
            return true;
        foreach (var s in _slots)
            if (s.State == SlotState.Handed && !s.Frame.Released)
                return false;
        Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var s in _slots)
        {
            if (s.State == SlotState.Handed)
                _device.UnmapPack(s.Buffer);
            _device.DeleteFence(s.Fence);
            if (s.Buffer != 0)
                _device.DeleteBuffer(s.Buffer);
            s.Buffer = 0;
            s.Fence = 0;
            s.State = SlotState.Free;
        }
    }
}

/// <summary>The send pack pass: a picture scaled to the send's size (fit with black bars, fill cropped, or stretched) and packed by
/// <c>video.pack</c> into an RGBA8 target whose read-back bytes are the wire frame (<see cref="SendFormat.Uyvy"/>: w/2 × h;
/// <see cref="SendFormat.Uyva"/>: w/2 × (h + ⌈h/2⌉), the alpha plane in the rows below h; <see cref="SendFormat.P216"/> and
/// <see cref="SendFormat.Pa16"/>: w/2 × 2h and 3h, one plane row a target row, 16-bit samples as byte pairs; <see cref="SendFormat.Bgra8"/>:
/// w × h).
/// Each send keeps its own packed target (keyed by the caller's key), so a send that holds re-reads its last frame unchanged; the
/// scaling targets are shared per size. Nothing is allocated per frame once warm. Render thread.</summary>
public sealed class PackPass : IDisposable
{
    private readonly GlDevice _device;
    private readonly Dictionary<object, RenderTarget> _packed = new(ReferenceEqualityComparer.Instance);
    // Shared per size and kept while used: a size no pack has used for SweepEvery packs is let go (a send resized while dragged leaves
    // nothing behind).
    private const long SweepEvery = 600;
    private readonly Dictionary<(int W, int H), (RenderTarget Target, long Used)> _scaled = [];
    private readonly Dictionary<(int W, int H), (RenderTarget Target, long Used)> _black = [];
    private readonly List<(int W, int H)> _stale = [];
    private long _packs;

    public PackPass(GlDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
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
    /// (<paramref name="key"/>), its colour times <paramref name="gain"/> (the air's, for a send that follows it; alpha untouched);
    /// answers the packed target to read.</summary>
    public RenderTarget Pack(object key, GpuTexture? picture, int width, int height, SendFormat format, SendFit fit, float gain = 1f)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (++_packs % SweepEvery == 0)
        {
            Sweep(_scaled);
            Sweep(_black);
        }
        var source = Scale(picture, width, height, fit);
        var (tw, th) = TargetSize(format, width, height);
        if (!_packed.TryGetValue(key, out var target) || target.Width != tw || target.Height != th)
        {
            target?.Dispose();
            target = _device.CreateRenderTarget(tw, th, TextureFormat.Rgba8, filter: TextureFilter.Nearest);
            _packed[key] = target;
        }
        var p = _device.Program("video.pack");
        _device.BindTarget(target);
        _device.SetBlend(BlendMode.Opaque);
        p.SetInt("uMode", format switch { SendFormat.Uyvy => 0, SendFormat.Uyva => 1, SendFormat.P216 or SendFormat.Pa16 => 3, _ => 2 });
        p.SetInt("uWidth", width);
        p.SetInt("uHeight", height);
        p.Set("uGain", Math.Clamp(gain, 0f, 1f));
        _device.BindSampler(p, "uPicture", 0, source);
        _device.DrawFullscreen(p);
        return target;
    }

    private GpuTexture Scale(GpuTexture? picture, int width, int height, SendFit fit)
    {
        if (picture is null)
        {
            // One black picture per size, kept: two sends of different sizes with nothing to show make nothing per frame.
            if (!_black.TryGetValue((width, height), out var kept))
            {
                var made = _device.CreateRenderTarget(width, height, TextureFormat.Rgba8, filter: TextureFilter.Nearest);
                _device.BindTarget(made);
                _device.Clear(0, 0, 0, 1);
                kept = (made, 0);
            }
            _black[(width, height)] = (kept.Target, _packs);
            return kept.Target.Color;
        }
        if (picture.Width == width && picture.Height == height)
            return picture;
        var target = _scaled.TryGetValue((width, height), out var scaled)
            ? scaled.Target
            : _device.CreateRenderTarget(width, height, TextureFormat.Rgba16F, filter: TextureFilter.Nearest);
        _scaled[(width, height)] = (target, _packs);
        _device.BindTarget(target);
        _device.SetBlend(BlendMode.Opaque);
        _device.Clear(0, 0, 0, 1);
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
        _device.SetViewport(vx, vy, Math.Max(1, vw), Math.Max(1, vh));
        var p = _device.Program("blit");
        p.Set("uSrc", src);
        _device.BindSampler(p, "uTex", 0, picture);
        _device.DrawFullscreen(p);
        _device.SetViewport(0, 0, width, height);
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

    /// <summary>Lets go of the targets of sizes no pack used in the last <see cref="SweepEvery"/> packs.</summary>
    private void Sweep(Dictionary<(int W, int H), (RenderTarget Target, long Used)> cache)
    {
        _stale.Clear();
        foreach (var (size, entry) in cache)
            if (_packs - entry.Used >= SweepEvery)
                _stale.Add(size);
        foreach (var size in _stale)
        {
            cache[size].Target.Dispose();
            cache.Remove(size);
        }
    }
}
