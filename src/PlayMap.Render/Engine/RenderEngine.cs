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
/// what it shows — an output's finished picture (<see cref="OutputId"/>) or the editor's free view
/// (<see cref="Editor"/>).</summary>
public sealed record PreviewRequest(string ViewId, PreviewRingWriter Ring, int Width, int Height, string? OutputId = null, EditorCamera? Editor = null);

/// <summary>The renderer the engine drives: once a frame, <see cref="Render"/> draws every enabled output of
/// the project snapshot — sources, canvases, the projector pass, the output stage — into the output's
/// finished picture and presents it to the output's window, if one is attached; then <see cref="Preview"/>
/// renders the views viewers asked for and reads them back without stalling.
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
        public OutputWindow? Window;
        public FramePacer Pacer = new(60);
        public GpuTimer? Timer;
        public bool HasPicture;
        public long LastFrame;
        public double RenderMs;
        public GpuTexture? IdentifyCard;
        public GeneratorContext IdentifyKey;

        public void Dispose()
        {
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

        public void Dispose()
        {
            Readback.Dispose();
            Target.Dispose();
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

    /// <summary>The generators faulted now (each shows its error card until its settings change).</summary>
    public IReadOnlyList<GeneratorFault> GeneratorFaults() => _generators.Faults();

    /// <summary>Compiles every shader now, so the first show frame does not.</summary>
    public void WarmUp()
    {
        foreach (var name in Shaders.ShaderLibrary.Names)
            Device.Program(name);
    }

    /// <summary>Shows an output in a window. The first window attached paces the loop (its swap waits for
    /// the vertical blank); the others swap at once, so one thread driving several windows waits once a frame.</summary>
    public void Attach(string outputId, OutputWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var st = State(outputId);
        st.Window = window;
        if (!_paceOrder.Contains(outputId))
            _paceOrder.Add(outputId);
        RepickPacer();
    }

    /// <summary>Stops presenting an output into its window (the caller disposes the window).</summary>
    public void Detach(string outputId)
    {
        if (_outputs.TryGetValue(outputId, out var st))
            st.Window = null;
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
            first = false;
        }
    }

    /// <summary>An output's finished picture (8-bit, top row first), or null before it was drawn.</summary>
    public GpuTexture? OutputPicture(string outputId) =>
        _outputs.TryGetValue(outputId, out var st) && st.HasPicture ? st.Final?.Color : null;

    /// <summary>Reads an output's finished picture back synchronously as BGRA (tests and snapshots only).</summary>
    public bool ReadOutput(string outputId, Span<byte> bgra)
    {
        if (!_outputs.TryGetValue(outputId, out var st) || st.Final is null || !st.HasPicture)
            return false;
        Device.ReadPixels(st.Final, bgra);
        return true;
    }

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
        _generatorFrame = new GeneratorFrame(seconds, (long)Math.Floor(Math.Max(0, seconds) * rate + 1e-6), rate, inputs.Impulse, inputs.Project.Settings.FlashGuard);
        _caches.BeginFrame(Frame);
        _sources.BeginFrame(inputs.Project, inputs.Sources ?? NoSources.Instance, inputs.LeaderNs, Frame, in _generatorFrame);
        CollectPreviews();

        var outputs = inputs.Project.Outputs;
        for (var i = 0; i < outputs.Length; i++)
        {
            var output = outputs[i];
            if (!output.Enabled || output.Width < 1 || output.Height < 1)
                continue;
            var start = MonoClock.Instance.NowNs;
            var st = State(output.Id);
            st.LastFrame = Frame;
            EnsureFinal(st, output.Width, output.Height);
            if (inputs.Blackout)
            {
                // Black before any content code runs.
                Device.BindTarget(st.Final!);
                Device.Clear(0, 0, 0, 1);
                st.HasPicture = true;
            }
            else if (!(inputs.Freeze && st.HasPicture))
            {
                if (_options.GpuTiming)
                    (st.Timer ??= new GpuTimer(Device)).Begin();
                DrawOutput(output, i, st, in inputs);
                st.Timer?.End();
                st.HasPicture = true;
            }
            st.RenderMs = (MonoClock.Instance.NowNs - start) / 1e6;
        }

        foreach (var id in _paceOrder)
        {
            if (!_outputs.TryGetValue(id, out var st) || st.Window is null || st.Final is null)
                continue;
            st.Window.Present(st.Final.Color);
            st.Pacer.OnPresented(st.Window.LastSwapNs);
        }

        _sources.EndFrame();
        _caches.EndFrame();
        _services.Pool.Trim();
        EvictOutputs(outputs);
    }

    private void DrawOutput(Output output, int index, OutputState st, in FrameInputs inputs)
    {
        var project = inputs.Project;
        var projector = output.ProjectorId is { } pid ? project.FindProjector(pid) : null;
        var context = new GeneratorContext(output.Name, index + 1, projector?.Color ?? "#3EC1F3", true);
        GpuTexture picture;
        var src = new Vector4(0, 0, 1, 1);
        try
        {
            if (inputs.PatternOverride is { IsNone: false } pattern)
            {
                picture = _sources.Resolve(pattern, output.Width, output.Height, context);
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

        var warp = _caches.Warp(_resources.Warp(output.Id));
        var mask = _resources.OutputMask(output.Id) is { } m ? _caches.Coverage(m) : null;
        var blend = _resources.Blend(output.Id) is { } b ? _caches.Blend(b) : null;
        var lens = _resources.LensPredistortion(output.Id) is { } l ? _caches.Lens(l) : null;
        _outputStage.Draw(st.Final!, new OutputStageInputs(picture, src, warp, mask, blend, lens, output.Color,
            OutputStage.Quarters(output.Rotation), _options.Dither ? 0.49f : 0f, Frame));
        if (inputs.Identify)
            DrawIdentify(st, context, output.Width, output.Height);
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

        if (request.Editor is { } camera && _project is not null)
        {
            _stage.RenderEditor(pv.Target, _project, camera, _resources, _sources, in _generatorFrame);
        }
        else
        {
            var picture = request.OutputId is { } id ? OutputPicture(id) : null;
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

    private OutputState State(string outputId)
    {
        if (!_outputs.TryGetValue(outputId, out var st))
        {
            st = new OutputState();
            _outputs[outputId] = st;
        }
        return st;
    }

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
        st.Projector = Device.CreateRenderTarget(width, height, TextureFormat.Rgba16F, depth: true);
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
