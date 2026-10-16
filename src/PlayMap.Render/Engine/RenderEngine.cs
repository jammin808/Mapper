using System.Numerics;
using PlayMap.Core.Content;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Render.Generators;
using PlayMap.Render.Generators.Skia;
using PlayMap.Render.Gpu;
using PlayMap.Render.Passes;
using PlayMap.Render.Platform;
using PlayMap.Render.Projection;
using PlayMap.Render.Timing;
using SkiaSharp;

namespace PlayMap.Render.Engine;

/// <summary>How the engine renders.</summary>
public sealed record RenderEngineOptions(
    /// <summary>Dither the 8-bit output under half a code (exact codes stay exact).</summary>
    bool Dither = true,
    /// <summary>Stream video planes through pixel-unpack buffers.</summary>
    bool StreamUploads = true,
    /// <summary>Measure each output's GPU time with timer queries.</summary>
    bool GpuTiming = true);

/// <summary>A preview a viewer asked for: a view id, the ring it goes to, its size (at most the ring's), and
/// what it shows — an output's finished picture (<see cref="OutputId"/>), the editor's free view (<see cref="Editor"/>), or what a
/// projector puts on the wall (<see cref="ProjectorId"/>: its picture of the stage through the lens lookup of the output that drives
/// it, before that output's warp, blend, mask and trim, black where it lights nothing).</summary>
public sealed record PreviewRequest(string ViewId, PreviewRingWriter Ring, int Width, int Height, string? OutputId = null, EditorCamera? Editor = null,
    string? ProjectorId = null);

/// <summary>The renderer the engine drives: once a frame, <see cref="Render"/> draws every enabled output of
/// the project snapshot — sources, canvases, the projector pass, the output stage — into the output's
/// finished picture and presents it to the output's presenter (<see cref="IOutputPresenter"/>: a GL window today), if one is
/// attached; then <see cref="Preview"/> renders the views viewers asked for and reads them back without stalling. Every output
/// keeps a <see cref="PresentClock"/>: the pacing presenter's is fitted to its vblanks, the others count against their own
/// refresh (<see cref="Present"/>).
///
/// Threading: everything here runs on the render thread that made the <see cref="RenderContext"/>, with that
/// context current; providers are called on it and must not block. Blackout is checked before any content
/// code runs. A faulting generator draws its error card; nothing a source or generator throws escapes a frame.
/// The frame path reuses its targets, buffers and arrays; it allocates nothing once warm.</summary>
public sealed class RenderEngine : IDisposable
{
    private const int PreviewIdleFrames = 120;

    private sealed class OutputState : IDisposable
    {
        public RenderTarget? Final;
        public RenderTarget? Projector;
        public IOutputPresenter? Window;
        public FramePacer Pacer = new(60);
        /// <summary>The output's vblank truth and ledger: measured on the pacing presenter, counted against the output's own
        /// refresh otherwise (offscreen included).</summary>
        public PresentClock Clock = new(60, measured: false, PresentPath.Offscreen);
        /// <summary>The refresh an offscreen output is measured against (its configured rate).</summary>
        public double OffscreenHz;
        public GpuTimer? Timer;
        public bool HasPicture;
        /// <summary>The picture up is a raw frame, not the show's: a freeze has nothing of the show to hold.</summary>
        public bool Raw;
        public long LastFrame;
        public double RenderMs;
        public GpuTexture? IdentifyCard;
        public GeneratorContext IdentifyKey;
        /// <summary>Whole frames the output's picture is held back to light with the slowest display, and what is left over (ms).</summary>
        public int DelayFrames;
        public double ResidualMs;
        /// <summary>The pictures the delay holds (d + 1 of the output's size; none at 0).</summary>
        public readonly DelayRing<RenderTarget> Ring = new();
        /// <summary>The renderer's frame the ring was last written in (a frame between that and this one breaks its sequence).</summary>
        public long RingFrame = -1;
        /// <summary>The picture presented this frame (the ring's tail, or the finished picture with no delay).</summary>
        public GpuTexture? Shown;
        public RenderTarget? ShownTarget;

        public void Dispose()
        {
            Ring.Dispose();
            Final?.Dispose();
            Projector?.Dispose();
            Timer?.Dispose();
            IdentifyCard?.Dispose();
        }
    }

    private sealed class PreviewState : IDisposable
    {
        public required RenderTarget Target { get; init; }
        public required PixelPackRing Readback { get; init; }
        public required PreviewRingWriter Ring { get; set; }
        public long LastFrame;

        /// <summary>A projector view's own picture before the lens lookup (made at the view's size on its first frame).</summary>
        public RenderTarget? Picture;

        public void Dispose()
        {
            Readback.Dispose();
            Target.Dispose();
            Picture?.Dispose();
        }
    }

    private readonly RenderContext _context;
    private readonly RenderEngineOptions _options;
    private readonly RenderServices _services;
    private readonly GeneratorHost _generators;
    private readonly SourceResolver _sources;
    private readonly GpuCaches _caches;
    private readonly StageRenderer _stage;
    private readonly OutputStage _outputStage;
    private readonly Dictionary<string, OutputState> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreviewState> _previews = new(StringComparer.Ordinal);
    private readonly List<string> _stale = [];
    private readonly List<string> _paceOrder = [];
    private Project? _project;
    private System.Collections.Immutable.ImmutableArray<IFramePass> _passesGiven;
    private IFramePass[] _passes = [];
    private IStageResources _resources = EmptyStageResources.Instance;
    private GeneratorFrame _generatorFrame;
    private long _leaderNs;
    private bool _disposed;

    public RenderEngine(RenderContext context, RenderEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _options = options ?? new RenderEngineOptions();
        _services = new RenderServices(context.Device);
        _generators = new GeneratorHost(_services);
        _sources = new SourceResolver(_services, _generators, _options.StreamUploads);
        _caches = new GpuCaches(context.Device);
        _stage = new StageRenderer(_services, _caches);
        _outputStage = new OutputStage(_services);
    }

    public GlDevice Device => _context.Device;

    /// <summary>Frames rendered.</summary>
    public long Frame { get; private set; }

    /// <summary>Video uploads done (all sources; a repeated generation is not one).</summary>
    public int VideoUploads => _sources.VideoUploads;

    /// <summary>Meshes uploaded (a mesh instance the stage keeps handing back is uploaded once).</summary>
    public int MeshUploads => _caches.MeshUploads;

    /// <summary>Warp meshes uploaded (a warp instance the stage keeps handing back is uploaded once).</summary>
    public int WarpUploads => _caches.WarpUploads;

    /// <summary>The generators faulted now (each shows its error card until its settings change).</summary>
    public IReadOnlyList<GeneratorFault> GeneratorFaults() => _generators.Faults();

    /// <summary>Compiles every shader now, so the first show frame does not.</summary>
    public void WarmUp()
    {
        foreach (var name in Shaders.ShaderLibrary.Names)
            Device.Program(name);
    }

    /// <summary>Shows an output through a presenter (a window). The first presenter attached paces the loop (its present waits
    /// for the vertical blank); the others present at once, so one thread driving several windows waits once a frame.
    /// The output's presents are measured against <paramref name="refreshHz"/>, its display's refresh (60 when the
    /// platform does not say).</summary>
    public void Attach(string outputId, IOutputPresenter window, double refreshHz = 0)
    {
        ArgumentNullException.ThrowIfNull(window);
        var st = State(outputId);
        st.Window = window;
        var hz = refreshHz > 1 && double.IsFinite(refreshHz) ? refreshHz : 60;
        if (Math.Abs(st.Pacer.NominalHz - hz) > 1e-6)
            st.Pacer = new FramePacer(hz);
        st.Clock = new PresentClock(hz, measured: false, window.Path);
        if (!_paceOrder.Contains(outputId))
            _paceOrder.Add(outputId);
        RepickPacer();
    }

    /// <summary>Stops presenting an output into its window (the caller disposes the window); it counts offscreen from now.</summary>
    public void Detach(string outputId)
    {
        if (_outputs.TryGetValue(outputId, out var st) && st.Window is not null)
        {
            st.Window = null;
            st.Clock = new PresentClock(st.OffscreenHz > 1 ? st.OffscreenHz : 60, measured: false, PresentPath.Offscreen);
        }
        _paceOrder.Remove(outputId);
        RepickPacer();
    }

    private void RepickPacer()
    {
        var first = true;
        foreach (var id in _paceOrder)
        {
            if (!_outputs.TryGetValue(id, out var st) || st.Window is null)
                continue;
            st.Window.SetSwapInterval(first ? 1 : 0);
            st.Clock.SetMeasured(first);
            first = false;
        }
    }

    /// <summary>An output's finished picture this frame (8-bit, top row first) — what a pass that taps the output (a send, an LED
    /// map) reads, whatever delay its display has; null before it was drawn.</summary>
    public GpuTexture? OutputPicture(string outputId) =>
        _outputs.TryGetValue(outputId, out var st) && st.HasPicture ? st.Final?.Color : null;

    /// <summary>An output's picture as it went to its display this frame: with a delay, the one drawn that many frames ago — what the
    /// previews show; null before it was drawn.</summary>
    public GpuTexture? PresentedPicture(string outputId) =>
        _outputs.TryGetValue(outputId, out var st) && st.HasPicture ? st.Shown ?? st.Final?.Color : null;

    /// <summary>Holds an output's picture back whole frames (per-output latency: the faster displays wait for the slowest), with the
    /// residual its clock reports. Render thread; the ring's pictures are made at its next frame when the delay changes.</summary>
    public void SetDelay(string outputId, int frames, double residualMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        var st = State(outputId);
        st.DelayFrames = Math.Min(frames, LatencyPlan.MaxDelayFrames);
        st.ResidualMs = residualMs;
    }

    /// <summary>Reads an output's finished picture back synchronously as BGRA (tests and snapshots only).</summary>
    public bool ReadOutput(string outputId, Span<byte> bgra)
    {
        if (!_outputs.TryGetValue(outputId, out var st) || st.Final is null || !st.HasPicture)
            return false;
        Device.ReadPixels(st.Final, bgra);
        return true;
    }

    /// <summary>Reads back the picture an output presented this frame (its delay ring's tail; the finished picture with no delay),
    /// BGRA, synchronously (tests only).</summary>
    public bool ReadPresented(string outputId, Span<byte> bgra)
    {
        if (!_outputs.TryGetValue(outputId, out var st) || st.ShownTarget is null || !st.HasPicture)
            return false;
        Device.ReadPixels(st.ShownTarget, bgra);
        return true;
    }

    /// <summary>An output's presentation now (numbers only); null for an output the renderer has not drawn.</summary>
    public PresentSnapshot? Present(string outputId) =>
        _outputs.TryGetValue(outputId, out var st) ? st.Clock.Snapshot(MonoClock.Instance.NowNs) : null;

    /// <summary>The time the last frame spent in present calls, ms (the pacing presenter's wait for the vblank included).</summary>
    public double LastPresentMs { get; private set; }

    /// <summary>The output's next vblank after <paramref name="nowNs"/> from its fitted clock, or null when its clock is not
    /// measured (offscreen, or a presenter that does not wait).</summary>
    public long? NextVblankNs(string outputId, long nowNs) =>
        _outputs.TryGetValue(outputId, out var st) && st.Window is not null && st.Clock.Measured && st.Clock.Vblank.HasPhase
            ? st.Clock.Vblank.NextVblank(nowNs)
            : null;

    /// <summary>The presenter an output shows through, or null (offscreen).</summary>
    public IOutputPresenter? PresenterOf(string outputId) => _outputs.TryGetValue(outputId, out var st) ? st.Window : null;

    /// <summary>Zeroes an output's presentation counters (PRESENT RESET); answers the totals that were there, or null for an output
    /// the renderer has not drawn.</summary>
    public PresentSnapshot? ResetPresent(string outputId) =>
        _outputs.TryGetValue(outputId, out var st) ? st.Clock.Reset(MonoClock.Instance.NowNs) : null;

    public OutputStats Stats(string outputId)
    {
        if (!_outputs.TryGetValue(outputId, out var st))
            return default;
        var p = st.Pacer;
        return new OutputStats(p.Fps, p.Presented, p.Missed, p.FrameMs, p.FrameMsP95, st.RenderMs, st.Timer?.LastMs ?? 0, p.RefreshHz);
    }

    /// <summary>Renders every enabled output for one frame and presents the ones with windows.</summary>
    public void Render(in FrameInputs inputs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(inputs.Project);
        Frame++;
        _project = inputs.Project;
        _resources = inputs.Stage ?? EmptyStageResources.Instance;
        _leaderNs = inputs.LeaderNs;
        var rate = FrameRateOf(inputs.Project.Settings.FrameRate);
        var seconds = inputs.LeaderSeconds;
        _generatorFrame = new GeneratorFrame(seconds, (long)Math.Floor(Math.Max(0, seconds) * rate + 1e-6), rate, inputs.Impulse, inputs.Project.Settings.FlashGuard)
        {
            LeaderNs = inputs.LeaderNs,
            Modulation = inputs.Modulation,
            Audio = inputs.Audio,
        };
        OrderPasses(inputs.Passes);
        var passFrame = new PassFrame(Frame, inputs.LeaderNs, inputs.Project, _generatorFrame, Device, inputs.Blackout, inputs.Freeze)
        {
            PatternOverride = inputs.PatternOverride,
            PatternOutputs = inputs.PatternOutputs,
            Levels = inputs.Levels,
        };
        _caches.BeginFrame(Frame);
        _sources.BeginFrame(inputs.Project, inputs.Sources ?? NoSources.Instance, inputs.LeaderNs, Frame, in _generatorFrame);
        _sources.SetPasses(_passes, in passFrame);
        var (chainWidth, chainHeight) = SurfaceChainSize(inputs.Project);
        _stage.BeginFrame(chainWidth, chainHeight);
        CollectPreviews();

        var outputs = inputs.Project.Outputs;
        var frameStart = MonoClock.Instance.NowNs;
        foreach (var id in _paceOrder)
            if (_outputs.TryGetValue(id, out var paced) && paced.Window is not null)
                paced.Clock.BeginFrame(frameStart);
        for (var i = 0; i < outputs.Length; i++)
        {
            var output = outputs[i];
            if (!output.Enabled || output.Width < 1 || output.Height < 1)
                continue;
            var start = MonoClock.Instance.NowNs;
            var st = State(output.Id);
            if (st.Window is null)
                KeepOffscreenClock(st, output);
            st.LastFrame = Frame;
            EnsureFinal(st, output.Width, output.Height);
            if (inputs.Blackout)
            {
                // Black before any content code runs.
                Device.BindTarget(st.Final!);
                Device.Clear(0, 0, 0, 1);
                st.HasPicture = true;
                st.Raw = false;
            }
            else if (inputs.Raw?.For(output.Id) is { IsNone: false } raw)
            {
                DrawRaw(output, st, raw);
                st.HasPicture = true;
                st.Raw = true;
            }
            else if (!((inputs.Freeze || (inputs.Levels?.Hold(output.Id, inputs.LeaderNs) ?? false)) && st.HasPicture && !st.Raw))
            {
                if (_options.GpuTiming)
                    (st.Timer ??= new GpuTimer(Device)).Begin();
                DrawOutput(output, i, st, in inputs, in passFrame);
                st.Timer?.End();
                st.HasPicture = true;
                st.Raw = false;
            }
            st.RenderMs = (MonoClock.Instance.NowNs - start) / 1e6;
        }

        foreach (var pass in _passes)
            pass.FrameEnd(in passFrame);

        // The delay rings: each output's finished picture goes in, the one drawn d frames ago comes out (a scan's raw frame is never
        // delayed: its present stamp is the frame's own).
        for (var i = 0; i < outputs.Length; i++)
        {
            if (outputs[i].Enabled && _outputs.TryGetValue(outputs[i].Id, out var st) && st.LastFrame == Frame && st.Final is not null)
            {
                st.ShownTarget = Delayed(st);
                st.Shown = st.ShownTarget.Color;
            }
        }

        var presentStart = MonoClock.Instance.NowNs;
        foreach (var id in _paceOrder)
        {
            if (!_outputs.TryGetValue(id, out var st) || st.Window is null || st.Final is null)
                continue;
            var presented = st.Window.Present(st.Shown ?? st.Final.Color);
            if (presented.Queued)
                st.Pacer.OnPresented(presented.ReturnedNs);
            st.Clock.OnPresented(in presented);
        }
        foreach (var id in _paceOrder)
            if (_outputs.TryGetValue(id, out var st) && st.Window is not null)
                st.Window.Observe(st.Clock);
        LastPresentMs = (MonoClock.Instance.NowNs - presentStart) / 1e6;

        // Offscreen outputs: their picture moved on now; each is counted against its own refresh.
        var finished = PresentResult.Done(MonoClock.Instance.NowNs);
        for (var i = 0; i < outputs.Length; i++)
        {
            var output = outputs[i];
            if (output.Enabled && _outputs.TryGetValue(output.Id, out var st) && st.Window is null && st.LastFrame == Frame)
                st.Clock.OnPresented(in finished);
        }

        _sources.EndFrame();
        _caches.EndFrame();
        _services.Pool.Trim();
        EvictOutputs(outputs);
    }

    /// <summary>The size a chained surface's content is made at: the largest raster (by pixels, the first in output order on a tie)
    /// of the enabled projector outputs, so every projector samples one picture at the best resolution any of them needs; 0 × 0
    /// with no projector output.</summary>
    private static (int Width, int Height) SurfaceChainSize(Project project)
    {
        var (w, h) = (0, 0);
        foreach (var o in project.Outputs)
            if (o.Enabled && o.Mode == OutputMode.Projector && o.ProjectorId is not null && (long)o.Width * o.Height > (long)w * h)
                (w, h) = (o.Width, o.Height);
        return (w, h);
    }

    /// <summary>The frame's passes in their order, sorted again only when a different set is handed in (no allocation per frame).</summary>
    private void OrderPasses(System.Collections.Immutable.ImmutableArray<IFramePass> given)
    {
        if (given == _passesGiven)
            return;
        _passesGiven = given;
        _passes = given.IsDefaultOrEmpty ? [] : [.. given.OrderBy(p => p.Order)];
    }

    private void DrawOutput(Output output, int index, OutputState st, in FrameInputs inputs, in PassFrame passFrame)
    {
        var project = inputs.Project;
        var projector = output.ProjectorId is { } pid ? project.FindProjector(pid) : null;
        var context = new GeneratorContext(output.Name, index + 1, projector?.Color ?? "#3EC1F3", true);
        GpuTexture picture;
        var src = new Vector4(0, 0, 1, 1);
        try
        {
            if (inputs.Levels?.Override(output.Id, inputs.LeaderNs) is { IsNone: false } safe)
            {
                // The safe look a panic shows on this output, drawn like a pattern override: as its source is, through no chain.
                picture = _sources.ResolvePlain(safe, output.Width, output.Height, context);
            }
            else if (inputs.PatternCovers(output.Id))
            {
                picture = _sources.ResolvePlain(inputs.PatternOverride!, output.Width, output.Height, context);
            }
            else
            {
                switch (output.Mode)
                {
                    case OutputMode.Projector when projector is not null:
                        EnsureProjector(st, output.Width, output.Height);
                        _stage.RenderProjector(st.Projector!, project, projector, _resources, _sources, in _generatorFrame, context with { NativeRaster = false });
                        picture = st.Projector!.Color;
                        break;
                    case OutputMode.Canvas:
                        picture = _sources.Canvas(output.Source.Id) ?? _services.Black;
                        if (project.FindCanvas(output.Source.Id) is { } canvas && !output.SourceRect.IsEmpty)
                        {
                            var r = output.SourceRect;
                            src = new Vector4((float)(r.X / canvas.Width), (float)(r.Y / canvas.Height), (float)(r.W / canvas.Width), (float)(r.H / canvas.Height));
                        }
                        break;
                    case OutputMode.Source:
                        picture = _sources.Resolve(output.Source, output.Width, output.Height, context);
                        break;
                    default:
                        picture = _services.Black;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is GlException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // A source that could not be drawn this frame shows black; the generators keep their own faults.
            picture = _services.Black;
            Device.InvalidateState();
        }

        foreach (var pass in _passes)
            picture = pass.Picture(in passFrame, output, picture);

        var warp = _caches.Warp(_resources.Warp(output.Id));
        var mask = _resources.OutputMask(output.Id) is { } m ? _caches.Coverage(m) : null;
        var blend = _resources.Blend(output.Id) is { } b ? _caches.Blend(b) : null;
        var lens = _resources.LensPredistortion(output.Id) is { } l ? _caches.Lens(l) : null;
        // The dither is seeded by the show frame, a function of the leader instant: a backup's picture is the primary's to the byte.
        _outputStage.Draw(st.Final!, new OutputStageInputs(picture, src, warp, mask, blend, lens, output.Color,
            OutputStage.Quarters(output.Rotation), _options.Dither ? 0.49f : 0f, _generatorFrame.ShowFrame)
        {
            Gain = inputs.Levels?.Gain(output.Id, inputs.LeaderNs),
            Measured = inputs.Measured?.For(output.Id),
        });
        if (inputs.Identify)
            DrawIdentify(st, context, output.Width, output.Height);
        foreach (var pass in _passes)
            pass.Final(in passFrame, output, st.Final!);
    }

    /// <summary>A canvas's picture this frame, for a pass that taps it (a send, an LED map); null when the canvas is not drawn.
    /// Render thread, during a frame.</summary>
    public GpuTexture? CanvasPicture(string canvasId) => _sources.Canvas(canvasId);

    /// <summary>A source's picture this frame at a size, for a pass that taps it. Render thread, during a frame.</summary>
    public GpuTexture? SourcePicture(SourceRef source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.IsNone ? null : _sources.Resolve(source, width, height, GeneratorContext.None);
    }

    /// <summary>What the air shows instead of the show (a safe look, a test pattern) at a size, for a pass that taps it: resolved as
    /// an output resolves it, through no pass (a canvas or playlist in it composed or mixed without its layers' and items' calls).
    /// Render thread, during a frame.</summary>
    public GpuTexture? LookPicture(SourceRef look, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(look);
        return look.IsNone ? null : _sources.ResolvePlain(look, width, height, GeneratorContext.None);
    }

    /// <summary>A raw picture over the whole raster: the identity warp, no mask, blend or lens, a neutral trim,
    /// no rotation and no dither, so an 8-bit code in is the same code out at the same pixel.</summary>
    private void DrawRaw(Output output, OutputState st, SourceRef raw)
    {
        GpuTexture picture;
        try
        {
            picture = _sources.ResolvePlain(raw, output.Width, output.Height, new GeneratorContext(output.Name, 0, "#FFFFFF", true));
        }
        catch (Exception ex) when (ex is GlException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            picture = _services.Black;
            Device.InvalidateState();
        }
        _outputStage.Draw(st.Final!, new OutputStageInputs(picture, new Vector4(0, 0, 1, 1), _caches.Warp(null), null, null, null,
            ColorTrim.Neutral, 0, 0f, Frame));
    }

    /// <summary>The identification card over an output: number, name and colour, drawn once per change at
    /// a modest size and scaled over the picture.</summary>
    private void DrawIdentify(OutputState st, GeneratorContext context, int width, int height)
    {
        if (st.IdentifyCard is null || st.IdentifyKey != context)
        {
            st.IdentifyCard?.Dispose();
            var scale = Math.Min(1.0, 540.0 / height);
            var w = Math.Max(16, (int)(width * scale));
            var h = Math.Max(16, (int)(height * scale));
            using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                IdentifyPattern.DrawCard(canvas, _services.Kit, w, h, context, grid: false, frame: true, ground: false);
                canvas.Flush();
            }
            st.IdentifyCard = Device.CreateTexture(w, h, TextureFormat.Rgba8);
            Device.UploadPointer(st.IdentifyCard, bitmap.GetPixels(), UploadFormat.Rgba8, bitmap.RowBytes / 4);
            st.IdentifyKey = context;
        }
        Device.SetBlend(BlendMode.Premultiplied);
        _services.DrawQuad(st.Final!, st.IdentifyCard, 0, 0, width, height, 4, 1);
        Device.SetBlend(BlendMode.Opaque);
    }

    /// <summary>Renders a preview and queues its read-back; the picture reaches the ring a frame or two later
    /// (finished reads are collected here and at the start of every <see cref="Render"/>). Call after
    /// <see cref="Render"/> in the same frame. A view not asked for in a while is let go.</summary>
    public void Preview(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var w = Math.Clamp(request.Width, 1, request.Ring.MaxWidth);
        var h = Math.Clamp(request.Height, 1, request.Ring.MaxHeight);
        if (!_previews.TryGetValue(request.ViewId, out var pv) || pv.Target.Width != w || pv.Target.Height != h)
        {
            pv?.Dispose();
            pv = new PreviewState
            {
                Target = Device.CreateRenderTarget(w, h, TextureFormat.Rgba8, depth: true),
                Readback = new PixelPackRing(Device),
                Ring = request.Ring,
            };
            _previews[request.ViewId] = pv;
        }
        pv.Ring = request.Ring;
        pv.LastFrame = Frame;
        CollectPreview(pv);

        if (request.ProjectorId is { } projectorId && _project?.FindProjector(projectorId) is { } projector)
        {
            PreviewProjector(pv, _project, projector, w, h);
        }
        else if (request.Editor is { } camera && _project is not null)
        {
            _stage.RenderEditor(pv.Target, _project, camera, _resources, _sources, in _generatorFrame);
        }
        else
        {
            var picture = request.OutputId is { } id ? PresentedPicture(id) : null;
            var d = Device;
            d.BindTarget(pv.Target);
            d.SetBlend(BlendMode.Opaque);
            if (picture is null)
            {
                d.Clear(0, 0, 0, 1);
            }
            else
            {
                var blit = d.Program("blit");
                blit.Set("uSrc", 0, 0, 1, 1);
                d.BindSampler(blit, "uTex", 0, picture);
                d.DrawFullscreen(blit);
            }
        }
        pv.Readback.Request(pv.Target, _leaderNs);
    }

    /// <summary>A projector's view: the stage as the projector lights it (at the view's size, so the picture fills the raster as the
    /// output's does), then through the lens lookup of the first enabled output that drives it — no warp, mask, blend, trim or dither —
    /// so it equals that output's picture before those, and is black wherever the projector lights nothing.</summary>
    private void PreviewProjector(PreviewState pv, Project project, Projector projector, int w, int h)
    {
        if (pv.Picture is null || pv.Picture.Width != w || pv.Picture.Height != h)
        {
            pv.Picture?.Dispose();
            pv.Picture = Device.CreateRenderTarget(w, h, Device.TargetFormat(TextureFormat.Rgba16F, out _), depth: true);
        }
        Output? output = null;
        var index = 0;
        for (var i = 0; i < project.Outputs.Length && output is null; i++)
        {
            var o = project.Outputs[i];
            if (o.Enabled && o.Mode == OutputMode.Projector && string.Equals(o.ProjectorId, projector.Id, StringComparison.Ordinal))
                (output, index) = (o, i);
        }
        var context = new GeneratorContext(output?.Name ?? projector.Name, index + 1, projector.Color, true);
        _stage.RenderProjector(pv.Picture, project, projector, _resources, _sources, in _generatorFrame, context with { NativeRaster = false });
        var lens = output is not null && _resources.LensPredistortion(output.Id) is { } l ? _caches.Lens(l) : null;
        _outputStage.Draw(pv.Target, new OutputStageInputs(pv.Picture.Color, new Vector4(0, 0, 1, 1), _caches.Warp(null), null, null, lens,
            ColorTrim.Neutral, 0, 0f, Frame));
    }

    /// <summary>Delivers every finished preview read to its ring (never waits).</summary>
    public void CollectPreviews()
    {
        foreach (var (id, pv) in _previews)
        {
            if (Frame - pv.LastFrame > PreviewIdleFrames)
            {
                _stale.Add(id);
                continue;
            }
            CollectPreview(pv);
        }
        foreach (var id in _stale)
        {
            _previews[id].Dispose();
            _previews.Remove(id);
        }
        _stale.Clear();
    }

    private static void CollectPreview(PreviewState pv)
    {
        while (pv.Readback.TryCollect(pv.Ring, WriteToRing))
        {
            // Each pass delivers one finished read; the loop drains the ones that are done.
        }
    }

    private static void WriteToRing(PreviewRingWriter ring, ReadOnlySpan<byte> bgra, int width, int height, long stamp) =>
        ring.Write(bgra, width, height, width * 4, stamp);

    /// <summary>An offscreen output's clock counts against its configured refresh (60 when it names none); a changed refresh starts
    /// its count again.</summary>
    private static void KeepOffscreenClock(OutputState st, Output output)
    {
        var hz = output.RefreshHz is > 1 and <= 1000 && double.IsFinite(output.RefreshHz) ? output.RefreshHz : 60;
        if (Math.Abs(st.OffscreenHz - hz) < 1e-9 && st.Clock.Path == PresentPath.Offscreen)
            return;
        st.OffscreenHz = hz;
        st.Clock = new PresentClock(hz, measured: false, PresentPath.Offscreen);
    }

    private OutputState State(string outputId)
    {
        if (!_outputs.TryGetValue(outputId, out var st))
        {
            st = new OutputState();
            _outputs[outputId] = st;
        }
        return st;
    }

    /// <summary>A ring let go makes nothing (a static maker: nothing is captured, so the frame path allocates no closure).</summary>
    private static RenderTarget NoPicture() => throw new InvalidOperationException("a ring with no delay makes no pictures");

    /// <summary>The picture an output shows this frame through its delay ring (the finished one itself with no delay or a raw frame).</summary>
    private RenderTarget Delayed(OutputState st)
    {
        var final = st.Final!;
        st.Clock.DelayFrames = st.DelayFrames;
        st.Clock.LatencyResidualMs = st.ResidualMs;
        // Frames the output skipped (switched off, or not drawn) break the sequence: what the ring held from before them is never
        // shown after them.
        var unbroken = st.RingFrame == Frame - 1;
        st.RingFrame = Frame;
        if (st.DelayFrames == 0 || st.Raw)
        {
            if (st.DelayFrames == 0 && st.Ring.Delay > 0)
                st.Ring.Configure(0, NoPicture);
            // A raw frame breaks the sequence: what the ring held from before it is never shown after it.
            st.Ring.Restart();
            return final;
        }
        var resized = st.Ring.First is { } held && (held.Width != final.Width || held.Height != final.Height);
        if (st.Ring.Delay != st.DelayFrames || resized)
            RemakeRing(st, final.Width, final.Height);
        if (!unbroken)
            st.Ring.Restart();
        var head = st.Ring.Next();
        var d = Device;
        d.BindTarget(head);
        d.SetBlend(BlendMode.Opaque);
        var blit = d.Program("blit");
        blit.Set("uSrc", 0, 0, 1, 1);
        d.BindSampler(blit, "uTex", 0, final.Color);
        d.DrawFullscreen(blit);
        return st.Ring.Presented();
    }

    /// <summary>Makes an output's delay pictures again at its delay and size. Its own method: the maker's closure (it captures the
    /// size) is made only when the ring is remade. Written in <see cref="Delayed"/>, an optimised build would allocate that closure
    /// at the method's entry, on every frame of every output, whichever branch the frame takes.</summary>
    private void RemakeRing(OutputState st, int width, int height) =>
        st.Ring.Configure(st.DelayFrames, () => Device.CreateRenderTarget(width, height, TextureFormat.Rgba8), remake: true);

    private void EnsureFinal(OutputState st, int width, int height)
    {
        if (st.Final is not null && st.Final.Width == width && st.Final.Height == height)
            return;
        st.Final?.Dispose();
        st.Final = Device.CreateRenderTarget(width, height, TextureFormat.Rgba8);
        st.HasPicture = false;
    }

    private void EnsureProjector(OutputState st, int width, int height)
    {
        if (st.Projector is not null && st.Projector.Width == width && st.Projector.Height == height)
            return;
        st.Projector?.Dispose();
        // RGBA16F where the context renders float colour; RGBA8 on an ES context without EXT_color_buffer_float.
        st.Projector = Device.CreateRenderTarget(width, height, Device.TargetFormat(TextureFormat.Rgba16F, out _), depth: true);
    }

    /// <summary>Outputs gone from the project (and with no window) are let go after a while.</summary>
    private void EvictOutputs(System.Collections.Immutable.ImmutableArray<Output> outputs)
    {
        foreach (var (id, st) in _outputs)
            if (st.Window is null && Frame - st.LastFrame > PreviewIdleFrames && outputs.Find(id) is null)
                _stale.Add(id);
        foreach (var id in _stale)
        {
            _outputs[id].Dispose();
            _outputs.Remove(id);
        }
        _stale.Clear();
    }

    /// <summary>The show's frame rate from the project's setting ("50", "59.94", "60000/1001").</summary>
    public static double FrameRateOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 60;
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0
            && double.TryParse(text.AsSpan(0, slash), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num)
            && double.TryParse(text.AsSpan(slash + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var den)
            && den > 0)
            return Math.Clamp(num / den, 1, 1000);
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? Math.Clamp(v, 1, 1000)
            : 60;
    }

    /// <summary>Deletes every GPU object the engine made; on the render thread with the context current.
    /// Windows attached stay the caller's to dispose.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var pv in _previews.Values)
            pv.Dispose();
        _previews.Clear();
        foreach (var st in _outputs.Values)
            st.Dispose();
        _outputs.Clear();
        _stage.Dispose();
        _caches.Dispose();
        _sources.Dispose();
        _generators.Dispose();
        _services.Dispose();
        _disposed = true;
    }
}
