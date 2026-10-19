using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Core.Modulation;
using PlayMap.Render.Generators.Skia;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Generators;

/// <summary>What one frame of a generator is for: the time in seconds, the show's frame index at it (leader time × the show's
/// rate, floored — the same on every node), the show's rate, the sting firing, and whether the photosensitivity guard is on. The
/// renderer builds it on the leader clock; the generator host hands each generator its own time (seconds since its origin, or its
/// item's clip time when a playlist routes it), with the sting moved onto that clock, so a renderer animates on
/// <see cref="Seconds"/> alone.</summary>
public readonly record struct GeneratorFrame(double Seconds, long ShowFrame, double FrameRate, EffectImpulse Impulse, bool FlashGuard)
{
    /// <summary>The leader ns the generator's time counts from (its origin, or its item's start); 0 on the leader clock.</summary>
    public long OriginNs { get; init; }

    private readonly long? _ownNs;

    /// <summary><see cref="Seconds"/> in whole nanoseconds: the generator's own time, exact days into a show (a field's steps count
    /// on it in integers).</summary>
    public long OwnNs
    {
        get => _ownNs ?? (long)Math.Round(Seconds * 1e9);
        init => _ownNs = value;
    }

    /// <summary>The frame's leader instant in nanoseconds (the same instant as <see cref="Seconds"/>, unrounded).</summary>
    public long LeaderNs { get; init; }

    /// <summary>Modulation sources a generator's bindings read (null = none).</summary>
    public PlayMap.Core.Modulation.IModulationBus? Modulation { get; init; }

    /// <summary>The analysed sound a reactive generator reads (null = none).</summary>
    public PlayMap.Core.Modulation.IAudioFrames? Audio { get; init; }

    public static GeneratorFrame At(double seconds, double frameRate = 60, bool flashGuard = true) =>
        new(seconds, (long)Math.Floor(Math.Max(0, seconds) * frameRate + 1e-6), frameRate, EffectImpulse.None, flashGuard)
        {
            LeaderNs = (long)Math.Round(seconds * 1e9),
        };

    /// <summary>The frame on a generator's own clock: <paramref name="ownNs"/> of its time, its frame index counted on that clock,
    /// the sting moved onto that clock (it surges at the same leader instant), the origin named.</summary>
    public GeneratorFrame OnOwnClock(long ownNs, long originNs)
    {
        var seconds = ownNs / 1e9;
        var shift = Seconds - seconds;
        var impulse = Impulse.IsNone ? Impulse : Impulse with { StartSeconds = Impulse.StartSeconds - shift };
        var showFrame = (long)Math.Floor(Math.Max(0, seconds) * FrameRate + 1e-6);
        return this with { Seconds = seconds, OwnNs = ownNs, ShowFrame = showFrame, Impulse = impulse, OriginNs = originNs };
    }
}

/// <summary>Who a generator draws for: the output's (or canvas's) name, its number in the show (1-based; 0
/// when it has none), its colour (#RRGGBB) and whether the picture lands one-to-one on a raster (the test
/// card marks its one-to-one fields when it does not). Kinds that do not read it share one picture.</summary>
public readonly record struct GeneratorContext(string Name, int Number, string Color, bool NativeRaster)
{
    public static readonly GeneratorContext None = new("", 0, "#3EC1F3", true);
}

/// <summary>What every generator shares: the device, the pool, the Skia kit, a white texel.</summary>
internal sealed class RenderServices : IDisposable
{
    public RenderServices(GlDevice device)
    {
        Device = device;
        Pool = new ResourcePool(device);
        Kit = new SkiaKit();
        White = device.CreateTexture(1, 1, TextureFormat.Rgba8, TextureFilter.Nearest);
        device.Upload(White, [255, 255, 255, 255], UploadFormat.Rgba8);
        Black = device.CreateTexture(1, 1, TextureFormat.Rgba8, TextureFilter.Nearest);
        device.Upload(Black, [0, 0, 0, 255], UploadFormat.Rgba8);
    }

    public GlDevice Device { get; }
    public ResourcePool Pool { get; }
    public SkiaKit Kit { get; }

    /// <summary>A white texel: a flat colour drawn through the textured paths.</summary>
    public GpuTexture White { get; }

    /// <summary>A black texel: the picture of a source that has none yet.</summary>
    public GpuTexture Black { get; }

    /// <summary>Draws a texture into a rectangle of the bound target (pixels, top-left origin) with a blend
    /// shaped by the quad shader: mode 0 normal (straight alpha), 1 add, 2 multiply, 3 screen, 4 premultiplied.</summary>
    public void DrawQuad(RenderTarget target, GpuTexture texture, float x, float y, float w, float h, int mode, float opacity,
        float tr = 1, float tg = 1, float tb = 1, float ta = 1)
    {
        var d = Device;
        var p = d.Program("quad");
        d.BindTarget(target);
        p.Set("uRect", x, y, w, h);
        p.Set("uTarget", target.Width, target.Height);
        p.Set("uUv", 0, 0, 1, 1);
        p.SetInt("uMode", mode);
        p.Set("uOpacity", opacity);
        p.Set("uTint", tr, tg, tb, ta);
        d.BindSampler(p, "uTex", 0, texture);
        d.DrawGeneratedQuads(p);
    }

    /// <summary>A flat white veil over the whole target at an alpha (a sting's flash, already guarded).</summary>
    public void DrawFlash(RenderTarget target, float flash)
    {
        if (flash <= 0.01f)
            return;
        Device.SetBlend(BlendMode.Normal);
        DrawQuad(target, White, 0, 0, target.Width, target.Height, 0, Math.Clamp(flash * FlashGuard.MaxLevel, 0, 1));
        Device.SetBlend(BlendMode.Opaque);
    }

    public void Dispose()
    {
        White.Dispose();
        Black.Dispose();
        Kit.Dispose();
        Pool.Dispose();
    }
}

/// <summary>One generator drawn at one size for one consumer. Owns its output texture; renders into it when
/// asked (every frame when <see cref="Animated"/>, once per change otherwise).</summary>
internal abstract class GeneratorRenderer : IDisposable
{
    protected GeneratorRenderer(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
    {
        Services = services;
        Kind = kind;
        Width = width;
        Height = height;
        Context = context;
    }

    protected RenderServices Services { get; }
    protected GlDevice Device => Services.Device;

    public GeneratorKind Kind { get; }
    public int Width { get; }
    public int Height { get; }
    public GeneratorContext Context { get; }

    /// <summary>The parameters the renderer draws with now.</summary>
    public ParamSet Params { get; private set; } = ParamSet.Empty;

    /// <summary>The pictures the generator reads ("source": what a particle field emits from).</summary>
    public System.Collections.Immutable.ImmutableSortedDictionary<string, SourceRef> Inputs { get; set; } =
        System.Collections.Immutable.ImmutableSortedDictionary.Create<string, SourceRef>(StringComparer.Ordinal);

    /// <summary>How far a generator that settles has come, 0–1 (1: it draws its picture).</summary>
    public virtual double Progress => 1;

    /// <summary>What it draws a count of (particles), 0 when it has none.</summary>
    public virtual int Count => 0;

    /// <summary>What it says about itself ("" when nothing): a count lowered for this GPU, a source it cannot read.</summary>
    public virtual string Words => "";

    /// <summary>True when the kind evaluates its bindings live on the render thread (its numbers are read every frame).</summary>
    public bool DrawsBindings => GeneratorTraits.Of(Kind.Id).DrawsBindings;

    private Binding[] _bindings = [];
    private string[] _boundNames = [];
    private System.Collections.Immutable.ImmutableArray<PlayMap.Core.Model.ParamBinding> _bound = [];
    private IModulationBus? _slotsFor;

    /// <summary>One binding the renderer evaluates: the parameter's schema, the source key it reads (with its smoothing), the bus slot
    /// resolved for it, and the value it drew last.</summary>
    private sealed class Binding(PlayMap.Core.Model.ParamBinding binding, ParamSpec spec, string sourceKey)
    {
        public PlayMap.Core.Model.ParamBinding Source { get; } = binding;
        public ParamSpec Spec { get; } = spec;
        public string Key { get; } = sourceKey;
        public int Slot = -1;
        public double Value;
        public bool Applied;
    }

    /// <summary>The names of the numbers its bindings move (the order <see cref="LiveValues"/> fills).</summary>
    public IReadOnlyList<string> BoundNames => _boundNames;

    /// <summary>Takes a generator's bindings: each one on a number of this kind, enabled and not RATE, is evaluated every frame; the
    /// rest hold the base value. Allocates when the bindings change, never per frame.</summary>
    public void Bind(System.Collections.Immutable.ImmutableArray<PlayMap.Core.Model.ParamBinding> bindings)
    {
        if (bindings == _bound)
            return;
        _bound = bindings;
        var list = new List<Binding>();
        if (DrawsBindings && !bindings.IsDefaultOrEmpty)
            foreach (var b in bindings)
                if (b.Enabled && b.Mode != BindingMode.Rate && ParamValues.Spec(Kind, b.Param) is { Type: ParamType.Number } spec
                    && !GeneratorTraits.ConfigureOnly(Kind.Id, spec.Name))
                    list.Add(new Binding(b, spec, ModTargets.SourceKey(b)));
        _bindings = [.. list];
        _boundNames = [.. list.Select(x => x.Spec.Name)];
        _slotsFor = null;
    }

    /// <summary>Evaluates the bindings at the frame's leader instant through the frame's bus (a missing source holds the base) and
    /// writes the values drawn into <paramref name="live"/> (one per bound name).</summary>
    public void Modulate(IModulationBus? bus, long leaderNs, double[]? live)
    {
        if (_bindings.Length == 0)
            return;
        if (!ReferenceEquals(bus, _slotsFor))
        {
            _slotsFor = bus;
            foreach (var b in _bindings)
                b.Slot = bus?.Resolve(b.Key) ?? -1;
        }
        for (var i = 0; i < _bindings.Length; i++)
        {
            var b = _bindings[i];
            var baseValue = BaseNumber(b.Spec.Name);
            var s = 0.5;
            var read = bus is not null && (b.Slot >= 0 ? bus.TryRead(b.Slot, leaderNs, out s) : bus.TryRead(b.Key, leaderNs, out s));
            b.Applied = read;
            b.Value = read ? BindingEval.Apply(b.Source, s, baseValue, b.Spec.Min, b.Spec.Max) : baseValue;
            if (live is not null && i < live.Length)
                live[i] = b.Value;
        }
    }

    /// <summary>A number's value for this frame: its binding's when one moves it, else <see cref="BaseNumber"/>.</summary>
    protected double Num(string name) => Bound(name, out var v) ? v : BaseNumber(name);

    /// <summary>A number's value with no binding: the parameter set's, else the default (a kind whose looks come from a preset
    /// answers the preset's value).</summary>
    protected virtual double BaseNumber(string name) => ParamValues.Number(Params, Kind, name);

    /// <summary>The value a binding moves a number to this frame, when one does.</summary>
    protected bool Bound(string name, out double value)
    {
        foreach (var b in _bindings)
            if (b.Applied && b.Spec.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = b.Value;
                return true;
            }
        value = 0;
        return false;
    }

    /// <summary>A number from a resolved setting, unless a binding moves it this frame.</summary>
    protected double Num(string name, double resolved) => Bound(name, out var v) ? v : resolved;

    private IModulationBus? _earsFor;
    private int _levelSlot = -1;
    private int _bassSlot = -1;

    /// <summary>True when the last <see cref="Hear"/> asked for sound and the node's bus had none (the scene drew neutral).</summary>
    protected bool HeardNothing { get; private set; }

    /// <summary>What a scene hears this frame: the bus's level and bass at the frame's instant, moving it by <paramref name="amount"/>
    /// (0: nothing is read and nothing moves). A node with no sound reads neutral (0.5) and says so.</summary>
    protected ReactiveSound Hear(in GeneratorFrame frame, double amount)
    {
        HeardNothing = false;
        if (amount <= 0)
            return ReactiveSound.None;
        var bus = frame.Modulation;
        if (bus is null)
        {
            HeardNothing = true;
            return new ReactiveSound(0.5f, 0.5f, (float)Math.Clamp(amount, 0, 1));
        }
        if (!ReferenceEquals(bus, _earsFor))
        {
            _earsFor = bus;
            _levelSlot = bus.Resolve(ReactiveSound.LevelSource);
            _bassSlot = bus.Resolve(ReactiveSound.BassSource);
        }
        var heardLevel = _levelSlot >= 0 ? bus.TryRead(_levelSlot, frame.LeaderNs, out var level) : bus.TryRead(ReactiveSound.LevelSource, frame.LeaderNs, out level);
        var heardBass = _bassSlot >= 0 ? bus.TryRead(_bassSlot, frame.LeaderNs, out var bass) : bus.TryRead(ReactiveSound.BassSource, frame.LeaderNs, out bass);
        HeardNothing = !heardLevel && !heardBass;
        return new ReactiveSound(heardLevel ? (float)level : 0.5f, heardBass ? (float)bass : 0.5f, (float)Math.Clamp(amount, 0, 1));
    }

    /// <summary>The words a listening scene says when the node has no sound.</summary>
    public const string NoSoundWords = "audio: none on this node (it draws neutral)";

    /// <summary>The picture (top row first).</summary>
    public abstract GpuTexture Output { get; }

    /// <summary>True when the picture moves with time (rendered every frame).</summary>
    public abstract bool Animated { get; }

    /// <summary>True when the parameters changed since the last render (a still picture re-renders then).</summary>
    protected bool Dirty { get; set; } = true;

    /// <summary>Takes new parameters; the next render uses them.</summary>
    public void Configure(ParamSet parameters)
    {
        Params = parameters;
        Dirty = true;
        OnConfigure();
    }

    /// <summary>Reads the parameters into the renderer's own form (throws when they cannot be drawn).</summary>
    protected virtual void OnConfigure()
    {
    }

    /// <summary>What the renderer reads before its draw is timed (another source's picture, which draws on its own account).</summary>
    public virtual void Prepare(in GeneratorFrame frame)
    {
    }

    /// <summary>True when bindings move some of its numbers.</summary>
    protected bool HasBindings => _bindings.Length > 0;

    /// <summary>Draws when the picture needs it.</summary>
    public void Render(in GeneratorFrame frame)
    {
        if (!Animated && !Dirty)
            return;
        Draw(in frame);
        Output.Version++;
        Dirty = false;
    }

    protected abstract void Draw(in GeneratorFrame frame);

    protected int Int(string name) => ParamValues.Integer(Params, Kind, name);
    protected bool Flag(string name) => ParamValues.Flag(Params, Kind, name);
    protected string Choice(string name) => ParamValues.Choice(Params, Kind, name);
    protected string Text(string name) => ParamValues.Text(Params, Kind, name);
    protected ColorRgb Color(string name, ColorRgb fallback) => ColorRgb.Parse(ParamValues.Text(Params, Kind, name), fallback);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the renderer's GPU objects (on the render thread).</summary>
    protected abstract void Dispose(bool disposing);
}
