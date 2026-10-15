using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Generators;

/// <summary>The GPU particle field (<c>particles.flow</c>, and <c>particles.image</c>: born where a source picture is bright, drifting
/// along its contours, coloured by it): every slot's state in two RGBA32F textures stepped by a fragment pass on
/// a fixed 1/120 s grid of the generator's own time, the births on <see cref="ParticleSchedule"/> (integers, the C# twin's), the
/// sprites drawn instanced from the state with no CPU copy. The field has bounded memory: it is re-anchored one longest life
/// before the time it must show — on its first frame, after a seek, a jump past a life or any change of its parameters — and run
/// forward from there, so it is the same field bit for bit as one that played from the start (on one GPU and driver). While it
/// settles it holds its last picture and says how far it has come. The catch-up a frame may do is capped by the field's size.</summary>
internal sealed class FlowGenerator : GeneratorRenderer
{
    /// <summary>Particle-steps one frame may do while it settles.</summary>
    public const long CatchUpBudget = 30_000_000;

    private readonly RenderTarget _target;
    private readonly GpuTexture _atlas;
    private readonly FlashGuard _guard = new();
    private StateTarget? _current;
    private StateTarget? _next;
    private ParticleFlowSettings _settings = ParticleFlowSettings.Resolve(ParamSet.Empty);
    private ColorRgb[] _palette = [ColorRgb.White];
    private ColorRgb _background = ColorRgb.Black;
    private int _side;
    private long _simStep = long.MinValue;
    private long _targetStep;
    private long _anchorStep;
    private bool _drawn;
    private string _words = "";
    private string _pictureWords = "";
    private SourceRef? _missing;
    private string _missingWords = "";

    public FlowGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
        : base(services, kind, width, height, context)
    {
        _target = Device.CreateRenderTarget(width, height, TextureFormat.Rgba16F);
        _atlas = Device.CreateTexture(ParticleGenerator.Sprite * 5, ParticleGenerator.Sprite, TextureFormat.Rgba8);
        ParticleGenerator.DrawAtlas(Device, _atlas);
        Device.BindTarget(_target);
        Device.Clear(0, 0, 0, 1);
    }

    public override GpuTexture Output => _target.Color;

    public override bool Animated => true;

    public override int Count => _settings.Count;

    public override string Words => _pictureWords.Length > 0 ? _pictureWords : _words;

    /// <summary>How far the field has settled since it was anchored (1 once it shows the time it is asked for).</summary>
    public override double Progress
    {
        get
        {
            if (_simStep == long.MinValue)
                return 0;
            var span = _targetStep - _anchorStep;
            return span <= 0 || _simStep >= _targetStep ? 1 : Math.Clamp((double)(_simStep - _anchorStep) / span, 0, 1);
        }
    }

    /// <summary>The step the field has been run to (biased steps are what the shader saw).</summary>
    public long SimStep => _simStep;

    public ParticleFlowSettings Settings => _settings;

    /// <summary>The state the field holds now (tests read it back).</summary>
    public StateTarget? State => _current;

    /// <summary>The most particles this GPU steps: a software rasteriser draws 64 k.</summary>
    public static int MaxCountFor(GlDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var r = device.Renderer;
        return r.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || r.Contains("softpipe", StringComparison.OrdinalIgnoreCase)
            || r.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase)
            ? ParticleFlowSettings.SoftwareCount : ParticleFlowSettings.MaxCount;
    }

    protected override void OnConfigure()
    {
        var max = MaxCountFor(Device);
        var asked = ParticleFlowSettings.Resolve(Params, kindId: Kind.Id);
        _settings = ParticleFlowSettings.Resolve(Params, max, Kind.Id);
        _words = asked.Count > _settings.Count
            ? $"{asked.Count} particles asked; this GPU ({Device.Renderer}) steps {_settings.Count}"
            : "";
        _palette = _settings.Palette();
        _background = ColorRgb.Parse(_settings.Background, ColorRgb.Black);
        var side = ParticleFlowSettings.Side(_settings.Count);
        if (side != _side || _current is null)
        {
            _current?.Dispose();
            _next?.Dispose();
            _current = null;
            _next = null;
            _side = side;
            _current = Device.CreateStateTarget(side, side, TextureFormat.Rgba32F);
            _next = Device.CreateStateTarget(side, side, TextureFormat.Rgba32F);
        }
        _guard.Reset();
        // Any change re-anchors: the field is then a pure function of its parameters and its time, whoever changed it when.
        _simStep = long.MinValue;
    }

    /// <summary>Steps a frame may run while it settles (never fewer than two: a frame at 60 Hz needs two).</summary>
    public int StepsPerFrame => (int)Math.Clamp(CatchUpBudget / Math.Max(1, _settings.Count), 2, 64);

    protected override void Draw(in GeneratorFrame frame)
    {
        var picture = PictureOf(in frame);
        _targetStep = (long)Math.Floor(frame.Seconds * ParticleSchedule.StepsPerSecond + 1e-9);
        var life = _settings.LifeMaxSteps;
        if (_simStep == long.MinValue || _targetStep < _simStep || _targetStep - _simStep > life)
            Anchor(_targetStep - life);
        var budget = _simStep < _targetStep - 2 ? StepsPerFrame : int.MaxValue;
        for (var n = 0; n < budget && _simStep < _targetStep; n++)
        {
            _simStep++;
            var stepSurge = EffectEnvelope.At(frame.Impulse, _simStep / (double)ParticleSchedule.StepsPerSecond);
            Step(_simStep, in stepSurge, picture);
        }
        if (_simStep < _targetStep && _drawn)
            return;
        var surge = EffectEnvelope.At(frame.Impulse, frame.Seconds);
        DrawSprites(in surge);
        var flash = frame.FlashGuard ? _guard.Limit(surge.Flash, frame.Seconds) : surge.Flash;
        Services.DrawFlash(_target, flash);
        _drawn = true;
    }

    /// <summary>The source picture the field reads (born on it, moved by it or coloured by it), or null (with words) when it has none.</summary>
    private GpuTexture? PictureOf(in GeneratorFrame frame)
    {
        if (!_settings.ReadsPicture(Num("flow")))
        {
            _pictureWords = "";
            return null;
        }
        if (!Inputs.TryGetValue("source", out var source) || source.IsNone)
        {
            _pictureWords = "it reads no picture yet: GENERATOR INPUT <generator> source <source>";
            return null;
        }
        if ((frame.Modulation as ContentFrame)?.Pictures is not { } pictures || pictures.Picture(source, 256, 256) is not { } texture)
        {
            // Said once per source, not once a frame.
            if (!ReferenceEquals(_missing, source))
            {
                _missing = source;
                _missingWords = $"no picture from {source} on this node yet";
            }
            _pictureWords = _missingWords;
            return null;
        }
        _pictureWords = "";
        return texture;
    }

    /// <summary>Starts the field over, every slot dormant, at <paramref name="step"/> (one longest life before the time it must show).</summary>
    private void Anchor(long step)
    {
        _anchorStep = step;
        _simStep = step;
        foreach (var t in (ReadOnlySpan<StateTarget?>)[_current, _next])
        {
            Device.BindStateTarget(t!);
            Device.Clear(0, 0, 0, 0);
        }
    }

    private void Step(long step, in EffectSurge surge, GpuTexture? picture)
    {
        var d = Device;
        var s = _settings;
        var p = d.Program("content.pstep");
        d.BindStateTarget(_next!);
        d.SetBlend(BlendMode.Opaque);
        p.SetInt("uSide", _side);
        p.SetInt("uCount", s.Count);
        p.SetInt("uStep", unchecked((int)ParticleSchedule.Biased(step)));
        p.SetInt("uSeedHash", unchecked((int)ParticleSchedule.Pcg(s.Seed)));
        p.SetInt("uLifeMin", s.LifeMinSteps);
        p.SetInt("uLifeMax", s.LifeMaxSteps);
        p.Set("uRate", (float)Num("rate"));
        p.Set("uDt", 1f / ParticleSchedule.StepsPerSecond);
        p.Set("uAspect", Aspect);
        p.SetInt("uEmitter", EmitterCode(s.Emitter));
        p.Set("uEmit", (float)Num("x"), (float)Num("y"), (float)Num("size"), 0);
        var dir = Num("direction") * Math.PI / 180;
        p.Set("uBirth", (float)Num("speedMin"), (float)Math.Max(Num("speedMin"), Num("speedMax")), (float)dir, (float)(Num("spread") * Math.PI / 180));
        p.Set("uThreshold", (float)Num("threshold"));
        p.SetInt("uHasPicture", picture is null ? 0 : 1);
        p.Set("uForces", (float)Num("gravity"), (float)Num("wind"), (float)Num("drag"), (float)Num("curl"));
        var drift = ContentTime.Wrap(step / (double)ParticleSchedule.StepsPerSecond * Num("curlSpeed"), 4096);
        p.Set("uCurl", (float)Num("curlScale"), (float)drift, Int("curlOctaves"), 0);
        p.Set("uVortex", (float)Num("vortexX"), (float)Num("vortexY"), (float)Num("vortex"), 0);
        p.Set("uAttract", (float)Num("attractX"), (float)Num("attractY"), (float)Num("attract"), (float)Num("attractRadius"));
        var speedMul = (1 + 3f * surge.Speed) * (1 - 0.95f * surge.Slow);
        p.Set("uSurge", speedMul, surge.Lift, surge.Swirl, surge.Gust * 2f);
        p.SetInt("uEdges", (int)s.Edges);
        p.Set("uRestitution", (float)Num("restitution"));
        p.SetInt("uWeight", (int)s.Weight);
        p.SetInt("uInvert", s.Invert ? 1 : 0);
        p.Set("uFlow", (float)Num("flow"), s.FlowMode == FlowMode.Across ? 1 : 0);
        p.SetInt("uColorSource", s.ColorBy == FlowColorBy.Source ? 1 : 0);
        d.BindSampler(p, "uS0", 0, _current!.A);
        d.BindSampler(p, "uS1", 1, _current.B);
        d.BindSampler(p, "uPicture", 2, picture ?? Services.Black);
        d.DrawFullscreen(p);
        (_current, _next) = (_next, _current);
    }

    private void DrawSprites(in EffectSurge surge)
    {
        var d = Device;
        var s = _settings;
        d.BindTarget(_target);
        d.Clear(_background.R, _background.G, _background.B, 1);
        var p = d.Program("content.psprite");
        p.SetInt("uSide", _side);
        p.Set("uAspect", Aspect);
        p.Set("uTarget", Width, Height);
        p.Set("uSize", (float)(Num("sizeMin") / 1080), (float)(Math.Max(Num("sizeMin"), Num("sizeMax")) / 1080));
        p.SetInt("uSizeOverLife", (int)s.SizeOverLife);
        p.SetInt("uColorBy", (int)s.ColorBy);
        p.Set("uSpeedRef", (float)Math.Max(0.01, Num("speedMax")));
        p.Set("uOpacity", (float)Num("opacity"));
        p.Set("uAtlas", ParticleGenerator.ShapeIndex(s.Shape) / 5f, 1f / 5f);
        p.Set("uGrow", 1 + surge.Scale + surge.Glow * 0.5f);
        p.Set("uNcol", Math.Min(_palette.Length, ParticleFlowSettings.PaletteColors));
        FractalGenerator.SetPalette(p, _palette);
        d.BindSampler(p, "uS0", 0, _current!.A);
        d.BindSampler(p, "uS1", 1, _current.B);
        d.BindSampler(p, "uTex", 2, _atlas);
        d.SetBlend(s.Glow ? BlendMode.AddPremultiplied : BlendMode.Premultiplied);
        d.DrawGeneratedQuads(p, s.Count);
        d.SetBlend(BlendMode.Opaque);
    }

    private float Aspect => Width / (float)Math.Max(1, Height);

    private static int EmitterCode(FlowEmitter e) => e switch
    {
        FlowEmitter.Point => 1,
        FlowEmitter.Line => 2,
        FlowEmitter.Ring => 3,
        FlowEmitter.Disc => 4,
        FlowEmitter.Top => 5,
        FlowEmitter.Bottom => 6,
        FlowEmitter.Left => 7,
        FlowEmitter.Right => 8,
        FlowEmitter.Edges => 9,
        FlowEmitter.Picture => 10,
        _ => 0,
    };

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _current?.Dispose();
        _next?.Dispose();
        _atlas.Dispose();
        _target.Dispose();
    }
}
