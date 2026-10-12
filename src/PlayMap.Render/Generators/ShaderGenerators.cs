using System.Globalization;
using PlayMap.Core.Content;
using PlayMap.Render.Generators.Skia;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Generators;

/// <summary>A generator drawn by a fragment shader into its own target, every frame or once per change.</summary>
internal abstract class ShaderGenerator : GeneratorRenderer
{
    private readonly RenderTarget _target;

    protected ShaderGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context, TextureFormat format)
        : base(services, kind, width, height, context)
    {
        _target = Device.CreateRenderTarget(width, height, format);
    }

    protected RenderTarget Target => _target;

    public override GpuTexture Output => _target.Color;

    /// <summary>Binds the target and draws the program over it.</summary>
    protected void Fill(ShaderProgram program)
    {
        Device.BindTarget(_target);
        Device.SetBlend(BlendMode.Opaque);
        Device.DrawFullscreen(program);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _target.Dispose();
    }
}

/// <summary>Squares, swapping on an interval when animated (LED refresh and latency).</summary>
internal sealed class CheckerGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
    : ShaderGenerator(services, kind, width, height, context, TextureFormat.Rgba8)
{
    public override bool Animated => Flag("animate");

    protected override void Draw(in GeneratorFrame frame)
    {
        var p = Device.Program("checker");
        var interval = Math.Max(0.05, Num("interval"));
        var swap = Flag("animate") && ((long)Math.Floor(Math.Max(0, frame.Seconds) / interval) & 1) == 1;
        p.Set("uCell", Math.Max(1, Int("cell")));
        p.Set("uColorA", 1, 1, 1, 1);
        p.Set("uColorB", 0, 0, 0, 1);
        p.SetInt("uSwap", swap ? 1 : 0);
        Fill(p);
    }
}

/// <summary>The motion tests: a moving bar (whole pixels a frame in judder mode), a bouncing box, the frame
/// flash (black and white alternating every show frame: a steady grey means dropped frames), a zone plate
/// and a scrolling grid. Everything is a function of the leader time and the show's frame index; the frame
/// flash goes through the flash guard when it is on.</summary>
internal sealed class MotionGenerator : ShaderGenerator
{
    private readonly FlashGuard _guard = new();
    private readonly TextOverlay _label;
    private readonly TextOverlay _counter;
    private readonly TextOverlay _caption;

    public MotionGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
        : base(services, kind, width, height, context, TextureFormat.Rgba8)
    {
        _label = new TextOverlay(services);
        _counter = new TextOverlay(services);
        _caption = new TextOverlay(services);
    }

    public override bool Animated => true;

    private double _labelRate = double.NaN;
    private string _labelText = "";

    protected override void OnConfigure()
    {
        _guard.Reset();
        _labelRate = double.NaN;
    }

    protected override void Draw(in GeneratorFrame frame)
    {
        var variant = Choice("variant") switch
        {
            "box" => 1,
            "flash" => 2,
            "zoneplate" => 3,
            "scroll" => 4,
            _ => 0,
        };
        var odd = (frame.ShowFrame & 1) == 1;
        // The frame flash's white: on the white phase what the guard lets through; on the black phase the
        // guard sees the fall (and the small corner blocks and counter are lit at the guard's peak).
        float flash = 1;
        if (variant == 2 && frame.FlashGuard)
        {
            var allowed = _guard.Limit(odd ? 1f : 0f, frame.Seconds);
            flash = odd ? allowed : FlashGuard.MaxLevel;
        }
        var p = Device.Program("motion");
        p.Set("uRes", Width, Height);
        p.SetInt("uVariant", variant);
        p.Set("uTime", (float)frame.Seconds);
        p.Set("uFrame", frame.ShowFrame % 1_000_000);
        p.Set("uSpeed", (float)Num("speed"));
        p.Set("uPxPerFrame", Int("pxPerFrame"));
        p.SetInt("uVertical", Flag("vertical") ? 1 : 0);
        if (variant == 1)
            p.Set("uAccent", 0x3E / 255f, 0xC1 / 255f, 0xF3 / 255f, 1);
        else
            p.Set("uAccent", 1, 1, 1, 1);
        p.Set("uFlash", Math.Min(1f, flash));
        Fill(p);

        var chip = Px.ChipText(Height);
        var margin = Math.Max(6f, Height * 0.02f);
        if (variant == 2)
        {
            var size = Math.Max(10f, Height * 0.2f);
            _counter.Set(Px.Number((int)(frame.ShowFrame % 1000)), size, chip: false);
            // The counter is drawn in the phase's foreground: black on the white frame, white on the black.
            var ink = odd ? 0f : Math.Min(1f, flash);
            _counter.Draw(Target, (Width - _counter.Width) / 2f, (Height - _counter.Height) / 2f, ink, ink, ink);
            _caption.Set("alternating every frame — a steady grey means dropped frames", Math.Clamp(Height * 0.03f, 10, 40), chip: false);
            _caption.Draw(Target, (Width - _caption.Width) / 2f, Height * 0.68f, ink, ink, ink);
        }
        else if (Flag("fps"))
        {
            if (frame.FrameRate != _labelRate)
            {
                _labelRate = frame.FrameRate;
                _labelText = LabelFor(Choice("variant"), Int("pxPerFrame"), Num("speed"), frame.FrameRate);
            }
            _label.Set(_labelText, chip, chip: true);
            _label.Draw(Target, (Width - _label.Width) / 2f, Height - _label.Height - margin);
            _counter.Set(Px.Number((int)(frame.ShowFrame % 1000)), chip, chip: true);
            _counter.Draw(Target, Width - _counter.Width - margin, margin);
        }
    }

    /// <summary>The info line: the test, its pace and the show's rate.</summary>
    public static string LabelFor(string variant, int pxPerFrame, double speed, double frameRate) => pxPerFrame > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{variant.ToUpperInvariant()} · {pxPerFrame} px/frame · {frameRate:0.##} fps")
        : string.Create(CultureInfo.InvariantCulture, $"{variant.ToUpperInvariant()} · {speed:0} px/s · {frameRate:0.##} fps");

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _label.Dispose();
        _counter.Dispose();
        _caption.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The full field through a list of colours. With the guard on, an unfaded cycle steps no faster
/// than the guard's three changes a second.</summary>
internal sealed class ColorCycleGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
    : ShaderGenerator(services, kind, width, height, context, TextureFormat.Rgba8)
{
    public const int MaxColors = 16;
    private static readonly string[] Slots = [.. Enumerable.Range(0, MaxColors).Select(i => "uColors[" + i.ToString(CultureInfo.InvariantCulture) + "]")];
    private ColorRgb[] _colors = [ColorRgb.White];

    public override bool Animated => true;

    protected override void OnConfigure()
    {
        var all = ColorRgb.ParseList(Text("colors"), ColorRgb.White);
        _colors = all.Length <= MaxColors ? all : all[..MaxColors];
    }

    /// <summary>The seconds each colour holds: the operator's, floored by the guard for a hard cut.</summary>
    public static double IntervalFor(double interval, bool fade, bool guard) =>
        guard && !fade ? Math.Max(interval, FlashGuard.MinPeriodSeconds) : Math.Max(0.05, interval);

    protected override void Draw(in GeneratorFrame frame)
    {
        var p = Device.Program("colorcycle");
        for (var i = 0; i < _colors.Length; i++)
            p.Set(Slots[i], _colors[i].R, _colors[i].G, _colors[i].B);
        p.SetInt("uCount", _colors.Length);
        p.Set("uTime", (float)frame.Seconds);
        p.Set("uInterval", (float)IntervalFor(Num("interval"), Flag("fade"), frame.FlashGuard));
        p.SetInt("uFade", Flag("fade") ? 1 : 0);
        Fill(p);
    }
}

/// <summary>Sync marks: a white flash for <c>flashMs</c> at every multiple of <c>period</c> on the leader's
/// clock (the time input is leader seconds), through the flash guard when it is on, and a bar growing from
/// each mark to the next along the bottom.</summary>
internal sealed class SyncMarksGenerator : ShaderGenerator
{
    private readonly FlashGuard _guard = new();
    private readonly TextOverlay _label;

    public SyncMarksGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
        : base(services, kind, width, height, context, TextureFormat.Rgba8)
    {
        _label = new TextOverlay(services);
    }

    public override bool Animated => true;

    private string _labelText = "";

    protected override void OnConfigure()
    {
        _guard.Reset();
        _labelText = string.Create(CultureInfo.InvariantCulture, $"SYNC MARKS · every {Num("period"):0.##} s · {Int("flashMs")} ms");
    }

    /// <summary>The white level of the field at a leader time (before the guard).</summary>
    public static float Level(double seconds, double period, double flashSeconds) =>
        SyncMarks.IsFlash(seconds, period, flashSeconds) ? 1f : 0f;

    protected override void Draw(in GeneratorFrame frame)
    {
        var period = Num("period");
        var flashSeconds = Int("flashMs") / 1000.0;
        var wanted = Level(frame.Seconds, period, flashSeconds);
        var level = frame.FlashGuard ? _guard.Limit(wanted, frame.Seconds) : wanted;
        var p = Device.Program("syncmarks");
        p.Set("uRes", Width, Height);
        p.Set("uLevel", level);
        p.Set("uPhase", (float)(SyncMarks.SinceMark(frame.Seconds, period) / period));
        Fill(p);
        var size = Px.ChipText(Height);
        _label.Set(_labelText, size, chip: true);
        _label.Draw(Target, (Width - _label.Width) / 2f, Math.Max(6f, Height * 0.02f));
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _label.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A fractal family through its shader: the view from <see cref="FractalView"/> (with a sting's
/// zoom, hue, rotation and morph folded in), the palette's five slots, and a sting's flash over the top
/// through the guard.</summary>
internal sealed class FractalGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
    : ShaderGenerator(services, kind, width, height, context, TextureFormat.Rgba16F)
{
    private readonly FlashGuard _guard = new();
    private FractalSettings _settings = FractalSettings.Resolve("fractal.mandelbrot", Core.Model.ParamSet.Empty);
    private ColorRgb[] _palette = [ColorRgb.White];

    public override bool Animated => true;

    public FractalSettings Settings => _settings;

    protected override void OnConfigure()
    {
        _settings = FractalSettings.Resolve(Kind.Id, Params);
        _palette = _settings.Palette();
        _guard.Reset();
    }

    protected override void Draw(in GeneratorFrame frame)
    {
        var surge = EffectEnvelope.At(frame.Impulse, frame.Seconds);
        var view = FractalView.Of(_settings, frame.Seconds, FractalView.ShaderIterationCap, surge);
        Apply(Device.Program(Kind.Id), view, _palette, Width, Height);
        Fill(Device.Program(Kind.Id));
        var flash = frame.FlashGuard ? _guard.Limit(surge.Flash, frame.Seconds) : surge.Flash;
        Services.DrawFlash(Target, flash);
    }

    /// <summary>Sets a fractal program's uniforms for a view at a size (the tests draw through this too).</summary>
    public static void Apply(ShaderProgram p, in FractalView view, ColorRgb[] palette, int width, int height)
    {
        p.Set("uRes", width, height);
        p.Set("uCenter", (float)view.CenterX, (float)view.CenterY);
        p.Set("uUpp", (float)view.UnitsPerPixel(height));
        p.Set("uC", (float)view.JuliaRe, (float)view.JuliaIm);
        p.Set("uIters", view.Iterations);
        p.Set("uOffset", (float)view.PaletteOffset);
        p.Set("uBright", (float)view.Brightness);
        p.Set("uNcol", Math.Min(palette.Length, FractalSettings.PaletteColors));
        SetPalette(p, palette);
        p.Set("uT", (float)view.Time);
        p.Set("uRot", (float)Math.Cos(view.Angle), (float)Math.Sin(view.Angle));
        p.Set("uWarp", (float)view.Warp);
    }

    internal static void SetPalette(ShaderProgram p, ColorRgb[] palette)
    {
        static ColorRgb At(ColorRgb[] pal, int i) => pal[Math.Min(i, pal.Length - 1)];
        p.Set("uP0", At(palette, 0).R, At(palette, 0).G, At(palette, 0).B);
        p.Set("uP1", At(palette, 1).R, At(palette, 1).G, At(palette, 1).B);
        p.Set("uP2", At(palette, 2).R, At(palette, 2).G, At(palette, 2).B);
        p.Set("uP3", At(palette, 3).R, At(palette, 3).G, At(palette, 3).B);
        p.Set("uP4", At(palette, 4).R, At(palette, 4).G, At(palette, 4).B);
    }
}

/// <summary>A reactive scene through its shader, the view from <see cref="ReactiveView"/>.</summary>
internal sealed class ReactiveGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
    : ShaderGenerator(services, kind, width, height, context, TextureFormat.Rgba16F)
{
    private readonly FlashGuard _guard = new();
    private ReactiveSettings _settings = ReactiveSettings.Resolve(Core.Model.ParamSet.Empty);
    private ColorRgb[] _palette = [ColorRgb.White];

    public override bool Animated => true;

    protected override void OnConfigure()
    {
        _settings = ReactiveSettings.Resolve(Params);
        _palette = _settings.Palette();
        _guard.Reset();
    }

    public static string ProgramFor(ReactiveScene scene) => scene switch
    {
        ReactiveScene.Tunnel => "reactive.tunnel",
        ReactiveScene.Kaleidoscope => "reactive.kaleidoscope",
        ReactiveScene.Pulse => "reactive.pulse",
        ReactiveScene.Vortex => "reactive.vortex",
        ReactiveScene.StarWarp => "reactive.starwarp",
        _ => "reactive.plasma",
    };

    protected override void Draw(in GeneratorFrame frame)
    {
        var surge = EffectEnvelope.At(frame.Impulse, frame.Seconds);
        var view = ReactiveView.Of(_settings, frame.Seconds, surge);
        var p = Device.Program(ProgramFor(_settings.Scene));
        Apply(p, view, _palette, Width, Height);
        Fill(p);
        var flash = frame.FlashGuard ? _guard.Limit(surge.Flash, frame.Seconds) : surge.Flash;
        Services.DrawFlash(Target, flash);
    }

    public static void Apply(ShaderProgram p, in ReactiveView view, ColorRgb[] palette, int width, int height)
    {
        p.Set("uRes", width, height);
        p.Set("uT", (float)view.Time);
        p.Set("uRot", (float)Math.Cos(view.Angle), (float)Math.Sin(view.Angle));
        p.Set("uDepth", (float)view.Depth);
        p.Set("uSym", Math.Max(2, view.Symmetry));
        p.Set("uLow", view.Low);
        p.Set("uAmount", view.Amount);
        p.Set("uBright", view.Brightness);
        p.Set("uNcol", Math.Min(palette.Length, ReactiveSettings.PaletteColors));
        FractalGenerator.SetPalette(p, palette);
    }
}
