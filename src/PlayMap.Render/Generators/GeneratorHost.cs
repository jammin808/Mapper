using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Render.Effects;
using PlayMap.Render.Generators.Skia;
using PlayMap.Render.Gpu;
using SkiaSharp;

namespace PlayMap.Render.Generators;

/// <summary>A generator that faulted: what it was and what went wrong (for STATE and the desk).</summary>
public sealed record GeneratorFault(string GeneratorId, string Kind, string Message);

/// <summary>The generators drawn this show: one renderer per generator, size and (for the kinds that read it)
/// consumer, made on first use, re-configured when the generator changes, drawn at most once a frame, and
/// dropped after <see cref="IdleFrames"/> frames unused. A renderer that throws — configuring or drawing —
/// shows an error card and stays disabled until its kind or parameters change; nothing throws through a
/// frame. A new particle field joins a running one with the same settings and size, so a picture opened
/// late shows the same particles.
///
/// <para>Each generator draws on its own clock: seconds since its origin (<see cref="Generator.OriginNs"/>), or — when a playlist
/// routes it now — its item's clip time (<see cref="ContentFrame"/>, the frame's modulation); the sync marks alone keep the leader's
/// grid. Its bindings are evaluated through the frame's bus before it draws (kinds that read their numbers every frame), its draw
/// is timed on the GPU, and what it did is reported to the frame's <see cref="ContentStats"/>.</para></summary>
internal sealed class GeneratorHost : IDisposable
{
    public const int IdleFrames = 120;

    private readonly record struct Key(string GeneratorId, int Width, int Height, GeneratorContext Context);

    private sealed class Instance : IDisposable
    {
        public GeneratorRenderer? Renderer;
        public Generator Generator = null!;
        public long LastUsed;
        public long LastRendered = -1;
        public string? Fault;
        public GpuTexture? ErrorCard;
        public FxTimer? Timer;
        public long TimedNs = -1;

        public void Dispose()
        {
            Renderer?.Dispose();
            Renderer = null;
            ErrorCard?.Dispose();
            ErrorCard = null;
            Timer?.Dispose();
            Timer = null;
        }
    }

    private readonly RenderServices _services;
    private readonly Dictionary<Key, Instance> _instances = [];
    private readonly List<Key> _stale = [];
    private readonly HashSet<string> _alive = new(StringComparer.Ordinal);
    private ContentStats? _stats;
    private long _frame;

    public GeneratorHost(RenderServices services)
    {
        _services = services;
    }

    /// <summary>Renderers alive now.</summary>
    public int Count => _instances.Count;

    /// <summary>The generators faulted now.</summary>
    public IReadOnlyList<GeneratorFault> Faults()
    {
        var list = new List<GeneratorFault>();
        foreach (var i in _instances.Values)
            if (i.Fault is { } f)
                list.Add(new GeneratorFault(i.Generator.Id, i.Generator.Kind, f));
        return list;
    }

    /// <summary>True for the kinds whose picture depends on who it is drawn for.</summary>
    public static bool ReadsContext(string kindId) =>
        kindId.Equals("pattern.testcard", StringComparison.OrdinalIgnoreCase) || kindId.Equals("pattern.identify", StringComparison.OrdinalIgnoreCase);

    public void BeginFrame(long frame) => _frame = frame;

    /// <summary>A generator's renderer at a size, when one is alive (tests read a field's state through it).</summary>
    internal GeneratorRenderer? RendererOf(string generatorId, int width, int height)
    {
        foreach (var (key, inst) in _instances)
            if (key.GeneratorId == generatorId && key.Width == width && key.Height == height)
                return inst.Renderer;
        return null;
    }

    /// <summary>The generator's picture at a size for a consumer, drawn for this frame if it needs drawing.</summary>
    public GpuTexture Render(Generator generator, int width, int height, GeneratorContext context, in GeneratorFrame frame)
    {
        ArgumentNullException.ThrowIfNull(generator);
        width = Math.Clamp(width, 1, _services.Device.MaxTextureSize);
        height = Math.Clamp(height, 1, _services.Device.MaxTextureSize);
        var key = new Key(generator.Id, width, height, ReadsContext(generator.Kind) ? context : GeneratorContext.None);
        if (!_instances.TryGetValue(key, out var inst))
        {
            inst = new Instance { Generator = generator };
            _instances[key] = inst;
            Build(inst, generator, width, height, key.Context);
        }
        else if (!ReferenceEquals(inst.Generator, generator))
        {
            Update(inst, generator, width, height, key.Context);
        }
        inst.LastUsed = _frame;
        var content = frame.Modulation as ContentFrame;
        _stats = content?.Stats ?? _stats;
        var live = content?.Stats.For(generator.Id);
        if (inst.Fault is not null || inst.Renderer is null)
        {
            live?.Set(GeneratorState.Faulted, inst.Fault ?? "", _frame);
            return inst.ErrorCard ?? _services.Black;
        }
        if (inst.LastRendered != _frame)
        {
            inst.LastRendered = _frame;
            var renderer = inst.Renderer;
            var seconds = OwnSeconds(generator, in frame, content, out var origin);
            var own = frame.OnOwnClock(seconds, origin);
            try
            {
                renderer.Inputs = generator.Inputs;
                renderer.Bind(generator.Bindings);
                if (!ReferenceEquals(renderer.BoundNames, _lastNames.GetValueOrDefault(inst)))
                    NameLive(inst, live);
                renderer.Modulate(frame.Modulation, frame.LeaderNs, live?.LiveValues);
                inst.Timer ??= _services.Device.HasTimerQuery ? new FxTimer(_services.Device) : null;
                inst.Timer?.Begin();
                renderer.Render(in own);
                inst.Timer?.End();
            }
            catch (Exception ex)
            {
                inst.Timer?.Cancel();
                Fail(inst, width, height, ex);
                live?.Set(GeneratorState.Faulted, inst.Fault ?? ex.Message, _frame);
                return inst.ErrorCard ?? _services.Black;
            }
            if (live is not null)
            {
                if (inst.Timer is { LastNs: var ns } && ns != inst.TimedNs && ns > 0)
                {
                    inst.TimedNs = ns;
                    live.Gpu(ns);
                }
                var progress = renderer.Progress;
                live.Drawn(progress, renderer.Count, (long)Math.Round(seconds * 1e9));
                live.Set(progress < 1 ? GeneratorState.Warming : GeneratorState.Ok, renderer.Words, _frame);
            }
        }
        return inst.Renderer.Output;
    }

    private readonly Dictionary<Instance, IReadOnlyList<string>> _lastNames = [];

    /// <summary>Tells the generator's live entry the names its bound values are for (when its bindings change).</summary>
    private void NameLive(Instance inst, GeneratorLive? live)
    {
        var names = inst.Renderer!.BoundNames;
        _lastNames[inst] = names;
        live?.LiveNames([.. names]);
    }

    /// <summary>A generator's own time in seconds this frame and the leader ns it counts from: its item's clip time when a playlist
    /// routes it, else the time since its origin; the sync marks keep the leader's clock.</summary>
    public static double OwnSeconds(Generator generator, in GeneratorFrame frame, ContentFrame? content, out long originNs)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (GeneratorTraits.OnLeaderClock(generator.Kind))
        {
            originNs = 0;
            return frame.Seconds;
        }
        if (content is not null && content.TryItemTime(generator.Id, out var itemNs))
        {
            originNs = frame.LeaderNs - itemNs;
            return itemNs / 1e9;
        }
        originNs = generator.OriginNs;
        return frame.Seconds - generator.OriginNs / 1e9;
    }

    /// <summary>Drops the renderers no frame has used for a while (a generator none of whose renderers is left is forgotten by the
    /// frame's stats).</summary>
    public void EndFrame()
    {
        _stats?.SetFrame(_frame);
        foreach (var (key, inst) in _instances)
            if (_frame - inst.LastUsed > IdleFrames)
                _stale.Add(key);
        if (_stale.Count == 0)
            return;
        foreach (var key in _stale)
        {
            var inst = _instances[key];
            _lastNames.Remove(inst);
            inst.Dispose();
            _instances.Remove(key);
        }
        _stale.Clear();
        _alive.Clear();
        foreach (var key in _instances.Keys)
            _alive.Add(key.GeneratorId);
        if (_stats is not null)
            foreach (var id in _stats.Ids())
                if (!_alive.Contains(id))
                    _stats.Forget(id);
    }

    private void Update(Instance inst, Generator generator, int width, int height, GeneratorContext context)
    {
        var previous = inst.Generator;
        inst.Generator = generator;
        var kindChanged = !string.Equals(previous.Kind, generator.Kind, StringComparison.OrdinalIgnoreCase);
        var paramsChanged = !SameParams(previous.Params, generator.Params);
        if (!kindChanged && !paramsChanged)
            return;
        if (kindChanged || inst.Renderer is null)
        {
            inst.Dispose();
            Build(inst, generator, width, height, context);
            return;
        }
        inst.Fault = null;
        inst.ErrorCard?.Dispose();
        inst.ErrorCard = null;
        try
        {
            inst.Renderer.Configure(generator.Params);
        }
        catch (Exception ex)
        {
            Fail(inst, width, height, ex);
        }
    }

    private void Build(Instance inst, Generator generator, int width, int height, GeneratorContext context)
    {
        inst.Fault = null;
        inst.LastRendered = -1;
        try
        {
            var kind = GeneratorCatalog.Find(generator.Kind) ?? throw new NotSupportedException($"No generator kind '{generator.Kind}'.");
            inst.Renderer = Create(kind, width, height, context);
            inst.Renderer.Configure(generator.Params);
            inst.Renderer.Inputs = generator.Inputs;
            if (inst.Renderer is ParticleGenerator particles)
                JoinRunningField(particles, generator.Id);
        }
        catch (Exception ex)
        {
            Fail(inst, width, height, ex);
        }
    }

    /// <summary>A late renderer of a generator takes the field a running renderer of the same generator draws (same settings and
    /// size: the same clock and origin, so the same field).</summary>
    private void JoinRunningField(ParticleGenerator late, string generatorId)
    {
        foreach (var (key, other) in _instances)
            if (key.GeneratorId == generatorId && other.Renderer is ParticleGenerator running && !ReferenceEquals(running, late)
                && late.Sim.JoinTimeline(running.Sim))
                return;
    }

    private GeneratorRenderer Create(GeneratorKind kind, int width, int height, GeneratorContext context)
    {
        var s = _services;
        switch (kind.Id)
        {
            case "pattern.checker":
                return new CheckerGenerator(s, kind, width, height, context);
            case "pattern.motion":
                return new MotionGenerator(s, kind, width, height, context);
            case "pattern.colorcycle":
                return new ColorCycleGenerator(s, kind, width, height, context);
            case "pattern.syncmarks":
                return new SyncMarksGenerator(s, kind, width, height, context);
            case "particles.field":
                return new ParticleGenerator(s, kind, width, height, context);
            case "reactive.scene":
                return new ReactiveGenerator(s, kind, width, height, context);
            case ParticleFlowSettings.KindId:
                return new FlowGenerator(s, kind, width, height, context);
        }
        if (Fractal3DSettings.IsKind(kind.Id))
            return new RaymarchGenerator(s, kind, width, height, context);
        if (kind.Family == "fractal")
            return new FractalGenerator(s, kind, width, height, context);
        if (kind.Family == "effect")
            return new EffectGenerator(s, kind, width, height, context);
        var pattern = SkiaPatternRenderer.PatternFor(kind.Id) ?? throw new NotSupportedException($"No renderer for '{kind.Id}'.");
        return new SkiaPatternRenderer(s, kind, width, height, context, pattern);
    }

    private void Fail(Instance inst, int width, int height, Exception ex)
    {
        inst.Fault = ex.Message;
        inst.Renderer?.Dispose();
        inst.Renderer = null;
        inst.ErrorCard?.Dispose();
        inst.ErrorCard = null;
        try
        {
            inst.ErrorCard = ErrorCardTexture(width, height, inst.Generator.Name.Length > 0 ? inst.Generator.Name : inst.Generator.Kind, ex.Message);
        }
        catch (Exception cardFault)
        {
            // The card itself could not be drawn (no memory for it): the output shows black, the fault stands.
            inst.Fault = ex.Message + " (card: " + cardFault.Message + ")";
        }
    }

    private GpuTexture ErrorCardTexture(int width, int height, string title, string detail)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            ErrorCard.Draw(canvas, _services.Kit, width, height, title, detail);
            canvas.Flush();
        }
        var texture = _services.Device.CreateTexture(width, height, TextureFormat.Rgba8);
        _services.Device.UploadPointer(texture, bitmap.GetPixels(), UploadFormat.Rgba8, bitmap.RowBytes / 4);
        return texture;
    }

    /// <summary>Parameter sets equal by content (a snapshot that re-made the set with the same values is no change).</summary>
    public static bool SameParams(ParamSet a, ParamSet b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (ReferenceEquals(a.Values, b.Values))
            return true;
        if (a.Values.Count != b.Values.Count)
            return false;
        foreach (var (k, v) in a.Values)
            if (!b.Values.TryGetValue(k, out var other) || !string.Equals(v, other, StringComparison.Ordinal))
                return false;
        return true;
    }

    public void Dispose()
    {
        foreach (var inst in _instances.Values)
            inst.Dispose();
        _instances.Clear();
        _lastNames.Clear();
    }
}
