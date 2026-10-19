using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Generators;

/// <summary>The 3D fractals (<c>fractal.mandelbulb</c>, <c>fractal.mandelbox</c>, <c>fractal.kifs</c>) on one marcher: sphere tracing
/// through the family's distance estimator (Core's <see cref="Fractal3D"/> is the twin), drawn at the render scale into a target of
/// its own and filtered up to the picture. The camera orbits on the generator's time; a sting's glow, zoom and flash and the sound
/// (through the flash guard's bound) move it.</summary>
internal sealed class RaymarchGenerator : GeneratorRenderer
{
    private static readonly DrawState Upscaled = FrameLists.Quads(BlendMode.Opaque);

    private readonly RenderTarget _target;
    private readonly FlashGuard _guard = new();
    private readonly ProgramId _march;
    private RenderTarget? _inner;
    private Fractal3DSettings _settings = null!;
    private ColorRgb[] _palette = [ColorRgb.White];
    private ColorRgb _background = ColorRgb.Black;

    public RaymarchGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
        : base(services, kind, width, height, context)
    {
        _march = Device.Programs.Id("content.raymarch");
        _target = Device.CreateTarget(new TargetDesc(width, height, TextureFormat.Rgba16F));
    }

    public override GpuTexture Output => _target.Color;

    public override bool Animated => true;

    public override string Words => HeardNothing ? NoSoundWords : "";

    public Fractal3DSettings Settings => _settings;

    /// <summary>The internal picture's size at a render scale.</summary>
    public static (int W, int H) InnerSize(int width, int height, double scale) =>
        (Math.Max(1, (int)Math.Round(width * Math.Clamp(scale, 0.25, 1))), Math.Max(1, (int)Math.Round(height * Math.Clamp(scale, 0.25, 1))));

    protected override void OnConfigure()
    {
        _settings = Fractal3DSettings.Resolve(Kind.Id, Params);
        _palette = _settings.Palette();
        _background = ColorRgb.Parse(_settings.Background, ColorRgb.Black);
        _guard.Reset();
    }

    protected override void Draw(in GeneratorFrame frame)
    {
        var surge = EffectEnvelope.At(frame.Impulse, frame.Seconds);
        var (w, h) = InnerSize(Width, Height, Num("renderScale"));
        if (_inner is null || _inner.Width != w || _inner.Height != h)
        {
            _inner?.Dispose();
            _inner = null;
            _inner = Device.CreateTarget(new TargetDesc(w, h, TextureFormat.Rgba16F));
        }
        var sound = Hear(frame, Num("sound"));
        var bright = (float)Num("brightness") * (1 + surge.Glow * 0.5f);
        if (sound.Amount > 0)
            bright = FlashGuard.Brightness(bright, sound.Low, sound.Amount);
        var camera = Camera(frame.Seconds, surge);
        var list = List;
        using (list.Pass(_inner, PassDesc.Keep))
        {
            var draw = list.Draw(_march, DrawState.Opaque);
            Apply(draw, camera, w, h, mode: 0);
            draw.Set(ProgramIds.ContentRaymarch.uBright, bright).Fullscreen();
        }
        using (list.Pass(_target, PassDesc.Keep))
            Services.DrawQuad(list, _target, _inner.Color, 0, 0, Width, Height, 0, 1, in Upscaled);
        var flash = frame.FlashGuard ? _guard.Limit(surge.Flash, frame.Seconds) : surge.Flash;
        Services.DrawFlash(_target, flash);
    }

    /// <summary>The camera at a time: its orbit from the speed, distance, height and field of view (a sting's zoom moves it in).</summary>
    public Fractal3DCamera Camera(double seconds, in EffectSurge surge) =>
        Fractal3DCamera.Orbit(seconds, Num("speed"), Num("distance") * (1 - 0.3 * Math.Clamp(surge.Zoom, -1, 1)), Num("height"), Num("fov"));

    /// <summary>The DE's numbers this frame (a binding may move the continuous ones).</summary>
    public Fractal3DParams De()
    {
        var q = _settings.De;
        return q with
        {
            Power = (float)Num("power", q.Power),
            Scale = (float)Num("scale", q.Scale),
            FoldLimit = (float)Num("foldLimit", q.FoldLimit),
            MinRadius = (float)Num("minRadius", q.MinRadius),
            FixedRadius = (float)Num("fixedRadius", q.FixedRadius),
            Twist = (float)Num("twist", q.Twist),
            Offset = new System.Numerics.Vector3((float)Num("offsetX", q.Offset.X), (float)Num("offsetY", q.Offset.Y), (float)Num("offsetZ", q.Offset.Z)),
        };
    }

    /// <summary>Sets the marcher's constants for a camera at an internal size into a draw; mode 1 writes the distance travelled.</summary>
    public void Apply(DrawBuilder draw, in Fractal3DCamera camera, int w, int h, int mode)
    {
        var q = De();
        draw.Set(ProgramIds.ContentRaymarch.uRes, w, h)
            .Set(ProgramIds.ContentRaymarch.uEye, camera.Eye.X, camera.Eye.Y, camera.Eye.Z)
            .Set(ProgramIds.ContentRaymarch.uRight, camera.Right.X, camera.Right.Y, camera.Right.Z)
            .Set(ProgramIds.ContentRaymarch.uUp, camera.Up.X, camera.Up.Y, camera.Up.Z)
            .Set(ProgramIds.ContentRaymarch.uFwd, camera.Forward.X, camera.Forward.Y, camera.Forward.Z)
            .Set(ProgramIds.ContentRaymarch.uTanHalf, camera.TanHalfFov)
            .SetInt(ProgramIds.ContentRaymarch.uKind, (int)_settings.Kind)
            .SetInt(ProgramIds.ContentRaymarch.uIters, q.Iterations)
            .Set(ProgramIds.ContentRaymarch.uPower, q.Power)
            .Set(ProgramIds.ContentRaymarch.uScale, q.Scale)
            .Set(ProgramIds.ContentRaymarch.uFoldLimit, q.FoldLimit)
            .Set(ProgramIds.ContentRaymarch.uMinR, q.MinRadius)
            .Set(ProgramIds.ContentRaymarch.uFixedR, q.FixedRadius)
            .SetInt(ProgramIds.ContentRaymarch.uFold, (int)q.Fold)
            .Set(ProgramIds.ContentRaymarch.uOffset, q.Offset.X, q.Offset.Y, q.Offset.Z)
            .Set(ProgramIds.ContentRaymarch.uTwist, q.Twist)
            .SetInt(ProgramIds.ContentRaymarch.uSteps, Int("steps"))
            .Set(ProgramIds.ContentRaymarch.uCone, Fractal3D.Cone(camera.TanHalfFov, h, (float)Num("detail")))
            .Set(ProgramIds.ContentRaymarch.uGlow, (float)Num("glow"))
            .Set(ProgramIds.ContentRaymarch.uAo, (float)Num("ao"))
            .Set(ProgramIds.ContentRaymarch.uFog, (float)Num("fog"))
            .Set(ProgramIds.ContentRaymarch.uBright, (float)Num("brightness"))
            .Set(ProgramIds.ContentRaymarch.uPhase, 0f)
            .Set(ProgramIds.ContentRaymarch.uBg, _background.R, _background.G, _background.B)
            .SetInt(ProgramIds.ContentRaymarch.uMode, mode)
            .Set(ProgramIds.ContentRaymarch.uNcol, Math.Min(_palette.Length, Fractal3DSettings.PaletteColors));
        FractalGenerator.SetPalette(draw, _palette);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _inner?.Dispose();
        _target.Dispose();
    }
}
