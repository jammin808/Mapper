using System.Runtime.InteropServices;
using PlayMap.Core.Gpu;
using PlayMap.Core.Led;
using PlayMap.Render.Engine;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Led;

/// <summary>The pictures LED groups sample, this frame (render thread): a canvas's composed picture, an output's final picture, a
/// source resolved at a size. Null samples black.</summary>
public interface ILedPictures
{
    GpuTexture? Picture(LedGroup group);
}

/// <summary>The renderer's pictures as LED groups see them.</summary>
public sealed class RendererPictures(RenderEngine renderer) : ILedPictures
{
    public GpuTexture? Picture(LedGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Binding switch
        {
            LedBinding.Canvas => renderer.CanvasPicture(group.Key),
            LedBinding.Output => renderer.OutputPicture(group.Key),
            LedBinding.Source => renderer.SourcePicture(group.Source, group.Width, group.Height),
            _ => null,
        };
    }
}

/// <summary>LED sampling at the end of every frame, after every output is drawn and before the presents. Pass 1 samples each group's
/// picture into the colour target (one RGBA32F texel per pixel, linear light); pass 2 makes every DMX slot of every row from it
/// (four slots a texel of an RGBA8 target); the rows are read back through a ring of pixel-pack buffers behind fences and handed to
/// the sink when their fence has passed — never a stall on the frame path. Blackout reaches every fixture whose node follows it
/// (colour and dimmer slots 0, constants kept); a frozen frame keeps the colours it has (pass 1 is skipped) while pass 2 still runs,
/// so blackout, tests and the level still apply. The plan's GPU objects are made when a different plan arrives; once warm a frame
/// allocates nothing.</summary>
public sealed class LedPass : IFramePass
{
    private const int RingSlots = 3;

    private struct Read
    {
        public uint Buffer;
        public int Capacity;
        public nint Fence;
        public bool Pending;
        public long Leader;
        public long Mono;
        public long Serial;
        public long Frame;
        public int Rows;
    }

    private readonly GlDevice _device;
    private readonly ILedPictures _pictures;
    private readonly Read[] _reads = new Read[RingSlots];
    private readonly Func<string, float> _outputGain;
    private LedPlan? _offered;
    private LedPlan? _built;
    private GpuTexture? _attr;
    private RenderTarget? _colors;
    private GpuTexture? _patch;
    private GpuTexture? _fix;
    private GpuTexture? _air;
    private RenderTarget? _slots;
    private float[] _airData = [];
    private bool _hasColors;
    private bool _allFollowBlackout;
    private int _write;
    private int _read;
    private IAirLevels? _levels;
    private long _leaderNs;
    private bool _disposed;

    public LedPass(GlDevice device, ILedPictures pictures)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pictures);
        _device = device;
        _pictures = pictures;
        // An output showing a raw frame (a scan or an align run) shows a pattern, not the show: its fixtures are dark.
        _outputGain = id => Raw?.For(id) is { IsNone: false } ? 0f : _levels?.Gain(id, _leaderNs) ?? 1f;
    }

    public string Name => "led";

    /// <summary>After every pass that changes a picture, before the sends (1 000): neither the LED pass nor a send changes a picture, so
    /// each reads the outputs as the frame drew them, whichever runs first.</summary>
    public int Order => 900;

    /// <summary>Where frames go (null: the passes do not run).</summary>
    public ILedFrameSink? Sink { get; set; }

    /// <summary>The rig's live air (level, test, locate); set by the host before each frame.</summary>
    public LedLive Live { get; set; } = LedLive.Initial;

    /// <summary>Outputs showing a raw frame this frame (scan and align runs); set by the host before each frame.</summary>
    public IRawOutputs? Raw { get; set; }

    /// <summary>The monotonic instant this frame's light is expected (the host sets it before each frame).</summary>
    public long LightMonoNs { get; set; }

    /// <summary>Frames read back a frame or more after the one that asked (their fence had not passed at the next frame).</summary>
    public long LateCollects { get; private set; }

    /// <summary>Frames not read back because every read-back slot was still in flight.</summary>
    public long Skipped { get; private set; }

    /// <summary>Frames sampled.</summary>
    public long Frames { get; private set; }

    /// <summary>The colour target (the previs reads it); null before a plan with pixels has been drawn.</summary>
    public GpuTexture? Colors => _colors?.Color;

    /// <summary>Hands a plan to the render thread (any thread; the newest wins).</summary>
    public void Offer(LedPlan? plan) => Volatile.Write(ref _offered, plan);

    public void FrameEnd(in PassFrame frame)
    {
        if (_disposed)
            return;
        Collect(frame.Frame);
        var plan = Volatile.Read(ref _offered);
        if (plan is null || Sink is null || (plan.Pixels == 0 && plan.Rows.IsEmpty))
            return;
        if (!_device.HasReadBgra)
        {
            // Fail closed with the reason: no read-back, no LED frames.
            Problem = "this GPU context reads back no BGRA (EXT_read_format_bgra is absent): LED output is off on this node";
            return;
        }
        if (!ReferenceEquals(plan, _built))
            Build(plan);
        _levels = frame.Levels;
        _leaderNs = frame.LeaderNs;
        var d = _device;
        d.SetBlend(BlendMode.Opaque);
        d.SetDepth(false, false);
        var (rigGain, rigHeld) = RigAir(frame);
        // Blackout wins before any pass: when it reaches every fixture nothing can light, so no picture is resolved or sampled
        // (pass 2 still sends the black frame, constants kept).
        if (plan.Pixels > 0 && !(frame.Blackout && _allFollowBlackout))
        {
            Sample(plan, frame.Freeze, rigHeld);
            _hasColors = true;
        }
        Frames++;
        if (plan.Rows.IsEmpty)
            return;
        LedAir.Fill(plan, Live, frame.LeaderNs, frame.Blackout, _outputGain, rigGain, _airData);
        d.Upload(_air!, MemoryMarshal.AsBytes(_airData.AsSpan()), UploadFormat.Rgba32F);
        Patch(plan, frame.LeaderNs);
        Request(plan, frame);
        d.Flush();
        Collect(frame.Frame);
    }

    /// <summary>The air of fixtures that follow no output, the most restrictive of the enabled outputs': the darkest gain (master ×
    /// panic × degrade; the master alone when there are no outputs), 0 while a panic shows any output a safe look (the LEDs have no
    /// safe look of their own), and held while any output holds its picture. An output fixture samples its output's own final
    /// picture, which the renderer holds or replaces itself.</summary>
    private static (float Gain, bool Held) RigAir(in PassFrame frame)
    {
        if (frame.Levels is not { } levels)
            return (1f, false);
        var gain = 1f;
        var any = false;
        var held = false;
        foreach (var o in frame.Project.Outputs)
        {
            if (!o.Enabled)
                continue;
            any = true;
            gain = MathF.Min(gain, levels.Gain(o.Id, frame.LeaderNs));
            if (levels.Override(o.Id, frame.LeaderNs) is { IsNone: false })
                gain = 0f;
            held |= levels.Hold(o.Id, frame.LeaderNs);
        }
        return (any ? gain : levels.Gain("", frame.LeaderNs), held);
    }

    /// <summary>Why the pass sends nothing on this node (null when it can).</summary>
    public string? Problem { get; private set; }

    /// <summary>Pass 1. A frozen frame keeps every pixel's colour; a held air keeps the colours of every group but an output's
    /// (whose final picture the renderer holds).</summary>
    private void Sample(LedPlan plan, bool freeze, bool rigHeld)
    {
        var d = _device;
        var p = d.Program("led_sample");
        foreach (var g in plan.Groups)
        {
            if (g.Count == 0 || (_hasColors && (freeze || (rigHeld && g.Binding != LedBinding.Output))))
                continue;
            // An output on a raw frame shows a pattern: its fixtures are dark (their air is 0), so its picture is not sampled.
            if (_hasColors && g.Binding == LedBinding.Output && Raw?.For(g.Key) is { IsNone: false })
                continue;
            // The picture first: making it (a canvas composed on demand) binds targets and textures of its own.
            // A group with no picture this frame samples one black texel: its pixels are black.
            var tex = (g.Binding == LedBinding.None ? null : _pictures.Picture(g)) ?? _black!;
            d.SetBlend(BlendMode.Opaque);
            d.BindTarget(_colors!);
            var rowFirst = g.First / LedPlan.ColorsWidth;
            var rowLast = (g.First + g.Count - 1) / LedPlan.ColorsWidth;
            d.SetViewport(0, rowFirst, LedPlan.ColorsWidth, rowLast - rowFirst + 1);
            p.Set("uSpread", LedReference.TapSpread);
            p.SetInt("uFirst", g.First);
            p.SetInt("uCount", g.Count);
            p.SetInt("uTaps", g.Taps);
            p.Set("uGamma", g.Gamma);
            p.Set("uTexSize", tex.Width, tex.Height);
            d.BindSampler(p, "uAttr", 0, _attr!);
            d.BindSampler(p, "uTex", 1, tex);
            d.DrawFullscreen(p);
        }
        d.BindTarget(_colors!);
    }

    private GpuTexture? _black;

    private void Patch(LedPlan plan, long leaderNs)
    {
        var d = _device;
        var p = d.Program("led_patch");
        d.BindTarget(_slots!);
        d.BindSampler(p, "uColors", 0, _colors!.Color);
        d.BindSampler(p, "uPatch", 1, _patch!);
        d.BindSampler(p, "uFix", 2, _fix!);
        d.BindSampler(p, "uAir", 3, _air!);
        var live = Live;
        p.Set("uLevel", (float)Math.Clamp(live.LevelAt(leaderNs), 0, 1));
        p.SetInt("uTest", (int)live.Test);
        p.SetInt("uStep", LedTestPatterns.StepAt(leaderNs));
        p.SetInt("uFixtures", plan.Fixtures.Length);
        d.DrawFullscreen(p);
    }

    private void Request(LedPlan plan, in PassFrame frame)
    {
        ref var r = ref _reads[_write];
        if (r.Pending)
        {
            Skipped++;
            return;
        }
        var bytes = plan.Rows.Length * LedPlan.SlotsPerRow;
        if (r.Buffer == 0 || r.Capacity < bytes)
        {
            if (r.Buffer != 0)
                _device.DeleteBuffer(r.Buffer);
            r.Buffer = _device.CreatePixelBuffer(pack: true, bytes);
            r.Capacity = bytes;
        }
        _device.ReadIntoPackBuffer(_slots!, r.Buffer);
        r.Fence = _device.InsertFence();
        r.Pending = true;
        r.Leader = frame.LeaderNs;
        r.Mono = LightMonoNs;
        r.Serial = plan.Serial;
        r.Frame = frame.Frame;
        r.Rows = plan.Rows.Length;
        _write = (_write + 1) % RingSlots;
    }

    /// <summary>Hands every finished read (oldest first) to the sink; never waits.</summary>
    private unsafe void Collect(long frame)
    {
        while (true)
        {
            ref var r = ref _reads[_read];
            if (!r.Pending || !_device.IsSignaled(r.Fence))
                return;
            _device.DeleteFence(r.Fence);
            r.Fence = 0;
            r.Pending = false;
            _read = (_read + 1) % RingSlots;
            if (frame > r.Frame + 1)
                LateCollects++;
            var bytes = r.Rows * LedPlan.SlotsPerRow;
            var p = _device.MapPack(r.Buffer, bytes);
            if (p is null)
                continue;
            var target = Sink?.Rent(r.Rows);
            if (target is not null)
            {
                new ReadOnlySpan<byte>(p, bytes).CopyTo(target.Slots);
                target.LeaderNs = r.Leader;
                target.LightMonoNs = r.Mono;
                target.PlanSerial = r.Serial;
                target.Rows = r.Rows;
            }
            _device.UnmapPack(r.Buffer);
            if (target is not null)
                Sink!.Submit(target);
        }
    }

    /// <summary>Makes the plan's GPU objects (render thread, when a different plan arrives): the attribute, patch and fixture
    /// textures filled once, the colour and slot targets sized to it.</summary>
    private void Build(LedPlan plan)
    {
        var d = _device;
        _built = plan;
        if (_black is null)
        {
            _black = d.CreateTexture(1, 1, TextureFormat.Rgba8, TextureFilter.Nearest);
            d.Upload(_black, [0, 0, 0, 255], UploadFormat.Rgba8);
        }
        var colorRows = plan.ColorRows;
        // A new plan places pixels anew: colours held from the old one would land on the wrong pixels.
        _hasColors = false;
        _allFollowBlackout = plan.Fixtures.All(f => f.FollowBlackout);
        if (_colors is null || _colors.Height != colorRows)
        {
            _colors?.Dispose();
            _attr?.Dispose();
            _colors = d.CreateRenderTarget(LedPlan.ColorsWidth, colorRows, TextureFormat.Rgba32F, filter: TextureFilter.Nearest);
            _attr = d.CreateTexture(LedPlan.ColorsWidth, colorRows, TextureFormat.Rgba32F, TextureFilter.Nearest);
        }
        d.Upload(_attr!, MemoryMarshal.AsBytes(plan.Attributes.AsSpan(0, LedPlan.ColorsWidth * colorRows * 4)), UploadFormat.Rgba32F);
        var fixtures = Math.Max(1, plan.Fixtures.Length);
        if (_fix is null || _fix.Height != fixtures)
        {
            _fix?.Dispose();
            _air?.Dispose();
            _fix = d.CreateTexture(LedPlan.FixtureTexels, fixtures, TextureFormat.Rgba32F, TextureFilter.Nearest);
            _air = d.CreateTexture(1, fixtures, TextureFormat.Rgba32F, TextureFilter.Nearest);
            _airData = new float[fixtures * 4];
        }
        d.Upload(_fix, MemoryMarshal.AsBytes(plan.FixtureTexelData.AsSpan(0, fixtures * LedPlan.FixtureTexels * 4)), UploadFormat.Rgba32F);
        var rows = plan.Rows.Length;
        if (rows == 0)
            return;
        if (_patch is null || _patch.Height != rows)
        {
            _patch?.Dispose();
            _slots?.Dispose();
            _patch = d.CreateTexture(LedPlan.SlotsPerRow, rows, TextureFormat.Rgba32F, TextureFilter.Nearest);
            _slots = d.CreateRenderTarget(LedPlan.SlotsPerRow / 4, rows, TextureFormat.Rgba8, filter: TextureFilter.Nearest);
        }
        d.Upload(_patch, MemoryMarshal.AsBytes(plan.Patch.AsSpan()), UploadFormat.Rgba32F);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var i = 0; i < RingSlots; i++)
        {
            _device.DeleteFence(_reads[i].Fence);
            if (_reads[i].Buffer != 0)
                _device.DeleteBuffer(_reads[i].Buffer);
            _reads[i] = default;
        }
        _attr?.Dispose();
        _colors?.Dispose();
        _patch?.Dispose();
        _fix?.Dispose();
        _air?.Dispose();
        _slots?.Dispose();
        _black?.Dispose();
    }
}
