using System.Runtime.InteropServices;
using PlayMap.Core.Gpu;
using PlayMap.Core.Led;
using PlayMap.Render.Engine;

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

/// <summary>LED sampling at the end of every frame, after every output is drawn and before the presents, in passes of its own on the
/// frame's list. Pass 1 samples each group's picture into the colour target (one RGBA32F texel per pixel, linear light); pass 2 makes
/// every DMX slot of every row from it (four slots a texel of an RGBA8 target); the rows are read back through a read-back ring of
/// the pass's own and handed to the sink once read — never a stall on the frame path. Blackout reaches every fixture whose node follows it
/// (colour and dimmer slots 0, constants kept); a frozen frame keeps the colours it has (pass 1 is skipped) while pass 2 still runs,
/// so blackout, tests and the level still apply. The plan's GPU objects are made when a different plan arrives; once warm a frame
/// allocates nothing.</summary>
public sealed class LedPass : IFramePass
{
    private const int RingSlots = 3;

    /// <summary>What a read-back carries to the sink besides its bytes, in request order (the ring hands its reads back in its order).</summary>
    private struct Read
    {
        public long Leader;
        public long Mono;
        public long Serial;
        public long Frame;
        public int Rows;
    }

    /// <summary>Every LED draw writes its rows whole, opaque.</summary>
    private static readonly DrawState Drawn = DrawState.Opaque;

    /// <summary>The rig's sampling into its float colours and its patch into its 8-bit slots.</summary>
    internal static void Pipelines(PipelineList keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Add("led_sample", Drawn, TargetShape.Rgba32F);
        keys.Add("led_patch", Drawn, TargetShape.Rgba8);
    }

    private readonly IGpuDevice _device;
    private readonly ILedPictures _pictures;
    private readonly ProgramId _sample;
    private readonly ProgramId _patchProgram;
    private readonly Read[] _reads = new Read[RingSlots];
    private IReadbackRing? _ring;
    private long _requested;
    private long _collectFrame;
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
    private IAirLevels? _levels;
    private long _leaderNs;
    private bool _disposed;

    public LedPass(IGpuDevice device, ILedPictures pictures)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pictures);
        _device = device;
        _pictures = pictures;
        _sample = device.Programs.Id("led_sample");
        _patchProgram = device.Programs.Id("led_patch");
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
        if (!_device.Features.Readback)
        {
            // Fail closed with the reason: no read-back, no LED frames.
            Problem = "this GPU context reads back no BGRA (EXT_read_format_bgra is absent): LED output is off on this node";
            return;
        }
        var list = frame.List;
        if (!ReferenceEquals(plan, _built))
            Build(list, plan);
        _levels = frame.Levels;
        _leaderNs = frame.LeaderNs;
        var (rigGain, rigHeld) = RigAir(frame);
        // Blackout wins before any pass: when it reaches every fixture nothing can light, so no picture is resolved or sampled
        // (pass 2 still sends the black frame, constants kept).
        if (plan.Pixels > 0 && !(frame.Blackout && _allFollowBlackout))
        {
            Sample(list, plan, frame.Freeze, rigHeld);
            _hasColors = true;
        }
        Frames++;
        if (plan.Rows.IsEmpty)
            return;
        LedAir.Fill(plan, Live, frame.LeaderNs, frame.Blackout, _outputGain, rigGain, _airData);
        list.Upload(_air!, MemoryMarshal.AsBytes(_airData.AsSpan()), UploadFormat.Rgba32F);
        Patch(list, plan, frame.LeaderNs);
        Request(plan, frame);
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
    private void Sample(ICommandList list, LedPlan plan, bool freeze, bool rigHeld)
    {
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
            var rowFirst = g.First / LedPlan.ColorsWidth;
            var rowLast = (g.First + g.Count - 1) / LedPlan.ColorsWidth;
            // The group's rows of the colour target only: every other group's pixels keep what they hold.
            using (list.Pass(_colors!, new PassDesc(LoadOp.Load, default, X: 0, Y: rowFirst, Width: LedPlan.ColorsWidth, Height: rowLast - rowFirst + 1)))
                list.Draw(_sample, Drawn)
                    .Set("uSpread", LedReference.TapSpread)
                    .SetInt("uFirst", g.First)
                    .SetInt("uCount", g.Count)
                    .SetInt("uTaps", g.Taps)
                    .Set("uGamma", g.Gamma)
                    .Set("uTexSize", tex.Width, tex.Height)
                    .Texture("uAttr", _attr!)
                    .Texture("uTex", tex)
                    .Fullscreen();
        }
    }

    private GpuTexture? _black;

    private void Patch(ICommandList list, LedPlan plan, long leaderNs)
    {
        var live = Live;
        using (list.Pass(_slots!, PassDesc.Keep))
            list.Draw(_patchProgram, Drawn)
                .Texture("uColors", _colors!.Color)
                .Texture("uPatch", _patch!)
                .Texture("uFix", _fix!)
                .Texture("uAir", _air!)
                .Set("uLevel", (float)Math.Clamp(live.LevelAt(leaderNs), 0, 1))
                .SetInt("uTest", (int)live.Test)
                .SetInt("uStep", LedTestPatterns.StepAt(leaderNs))
                .SetInt("uFixtures", plan.Fixtures.Length)
                .Fullscreen();
    }

    /// <summary>Queues the slots' read-back, stamped with its number: at most the ring's slots are in flight, read back in order,
    /// so the stamp's place in <see cref="_reads"/> is its own until it is collected.</summary>
    private void Request(LedPlan plan, in PassFrame frame)
    {
        _ring ??= _device.CreateReadbackRing(RingSlots);
        var stamp = _requested;
        if (!_ring.Request(_slots!, stamp))
        {
            Skipped++;
            return;
        }
        ref var r = ref _reads[(int)(stamp % RingSlots)];
        r.Leader = frame.LeaderNs;
        r.Mono = LightMonoNs;
        r.Serial = plan.Serial;
        r.Frame = frame.Frame;
        r.Rows = plan.Rows.Length;
        _requested++;
    }

    /// <summary>Hands every finished read (oldest first) to the sink; never waits.</summary>
    private void Collect(long frame)
    {
        if (_ring is null)
            return;
        _collectFrame = frame;
        var more = true;
        while (more)
            more = _ring.TryCollect(this, Deliver);
    }

    /// <summary>One finished read: its rows copied into a frame the sink rents, with what its request carried.</summary>
    private static void Deliver(LedPass pass, ReadOnlySpan<byte> bgra, int width, int height, long stamp)
    {
        ref var r = ref pass._reads[(int)(stamp % RingSlots)];
        if (pass._collectFrame > r.Frame + 1)
            pass.LateCollects++;
        var bytes = r.Rows * LedPlan.SlotsPerRow;
        if (pass.Sink?.Rent(r.Rows) is not { } target)
            return;
        bgra[..bytes].CopyTo(target.Slots);
        target.LeaderNs = r.Leader;
        target.LightMonoNs = r.Mono;
        target.PlanSerial = r.Serial;
        target.Rows = r.Rows;
        pass.Sink.Submit(target);
    }

    /// <summary>A texture of the plan's, filled from the CPU and read texel by texel.</summary>
    private GpuTexture Table(int width, int height, TextureFormat format) =>
        _device.CreateTexture(new TextureDesc(width, height, format, FrameLists.Uploaded, TextureFilter.Nearest));

    /// <summary>Makes the plan's GPU objects (render thread, when a different plan arrives): the attribute, patch and fixture
    /// textures filled once through the frame's list, the colour and slot targets sized to it.</summary>
    private void Build(ICommandList list, LedPlan plan)
    {
        var d = _device;
        _built = plan;
        if (_black is null)
        {
            _black = Table(1, 1, TextureFormat.Rgba8);
            list.Upload(_black, [0, 0, 0, 255], UploadFormat.Rgba8);
        }
        var colorRows = plan.ColorRows;
        // A new plan places pixels anew: colours held from the old one would land on the wrong pixels.
        _hasColors = false;
        _allFollowBlackout = plan.Fixtures.All(f => f.FollowBlackout);
        if (_colors is null || _colors.Height != colorRows)
        {
            _colors?.Dispose();
            _attr?.Dispose();
            _colors = d.CreateTarget(new TargetDesc(LedPlan.ColorsWidth, colorRows, TextureFormat.Rgba32F, Filter: TextureFilter.Nearest));
            _attr = Table(LedPlan.ColorsWidth, colorRows, TextureFormat.Rgba32F);
        }
        list.Upload(_attr!, MemoryMarshal.AsBytes(plan.Attributes.AsSpan(0, LedPlan.ColorsWidth * colorRows * 4)), UploadFormat.Rgba32F);
        var fixtures = Math.Max(1, plan.Fixtures.Length);
        if (_fix is null || _fix.Height != fixtures)
        {
            _fix?.Dispose();
            _air?.Dispose();
            _fix = Table(LedPlan.FixtureTexels, fixtures, TextureFormat.Rgba32F);
            _air = Table(1, fixtures, TextureFormat.Rgba32F);
            _airData = new float[fixtures * 4];
        }
        list.Upload(_fix, MemoryMarshal.AsBytes(plan.FixtureTexelData.AsSpan(0, fixtures * LedPlan.FixtureTexels * 4)), UploadFormat.Rgba32F);
        var rows = plan.Rows.Length;
        if (rows == 0)
            return;
        if (_patch is null || _patch.Height != rows)
        {
            _patch?.Dispose();
            _slots?.Dispose();
            _patch = Table(LedPlan.SlotsPerRow, rows, TextureFormat.Rgba32F);
            _slots = d.CreateTarget(new TargetDesc(LedPlan.SlotsPerRow / 4, rows, TextureFormat.Rgba8, Filter: TextureFilter.Nearest));
        }
        list.Upload(_patch, MemoryMarshal.AsBytes(plan.Patch.AsSpan()), UploadFormat.Rgba32F);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ring?.Dispose();
        _ring = null;
        _attr?.Dispose();
        _colors?.Dispose();
        _patch?.Dispose();
        _fix?.Dispose();
        _air?.Dispose();
        _slots?.Dispose();
        _black?.Dispose();
    }
}
