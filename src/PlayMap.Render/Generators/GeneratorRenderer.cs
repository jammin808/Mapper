using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Render.Generators.Skia;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Generators;

/// <summary>What one frame of a generator is for: the leader time in seconds, the show's frame index at it
/// (leader time × the show's rate, floored — the same on every node), the show's rate, the sting firing,
/// and whether the photosensitivity guard is on.</summary>
public readonly record struct GeneratorFrame(double Seconds, long ShowFrame, double FrameRate, EffectImpulse Impulse, bool FlashGuard)
{
    public static GeneratorFrame At(double seconds, double frameRate = 60, bool flashGuard = true) =>
        new(seconds, (long)Math.Floor(Math.Max(0, seconds) * frameRate + 1e-6), frameRate, EffectImpulse.None, flashGuard);
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

    protected double Num(string name) => ParamValues.Number(Params, Kind, name);
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
