using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Render.Generators.Skia;
using PlayMap.Render.Gpu;
using SkiaSharp;

namespace PlayMap.Render.Generators;

/// <summary>A still test pattern: drawn by Skia on the CPU at exactly the consumer's raster, uploaded once,
/// and drawn again only when its parameters change.</summary>
internal sealed class SkiaPatternRenderer : GeneratorRenderer
{
    private readonly ISkiaPattern _pattern;
    private readonly SKBitmap _bitmap;
    private readonly GpuTexture _texture;

    public SkiaPatternRenderer(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context, ISkiaPattern pattern)
        : base(services, kind, width, height, context)
    {
        _pattern = pattern;
        _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        _texture = Device.CreateTexture(width, height, TextureFormat.Rgba8);
    }

    public override GpuTexture Output => _texture;

    public override bool Animated => false;

    protected override void Draw(in GeneratorFrame frame)
    {
        using (var canvas = new SKCanvas(_bitmap))
        {
            var p = new PatternCanvas(canvas, Services.Kit, Width, Height, Kind, Params, Context);
            _pattern.Draw(in p);
            canvas.Flush();
        }
        Device.UploadPointer(_texture, _bitmap.GetPixels(), UploadFormat.Rgba8, _bitmap.RowBytes / 4);
    }

    /// <summary>The pattern for a still kind, or null when the kind is not a Skia pattern.</summary>
    public static ISkiaPattern? PatternFor(string kindId) => kindId switch
    {
        "pattern.testcard" => new TestCardPattern(),
        "pattern.grid" => new GridPattern(),
        "pattern.bars" => new BarsPattern(),
        "pattern.ramp" => new RampPattern(),
        "pattern.focus" => new FocusPattern(),
        "pattern.geometry" => new GeometryPattern(),
        "pattern.flat" => new FlatFieldPattern(),
        "pattern.ledwall" => new LedWallPattern(),
        "pattern.videowall" => new VideoWallPattern(),
        "pattern.blend" => new BlendPattern(),
        "pattern.identify" => new IdentifyPattern(),
        "pattern.convergence" => new ConvergencePattern(),
        "pattern.uvchecker" => new UvCheckerPattern(),
        _ => null,
    };

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _texture.Dispose();
        _bitmap.Dispose();
    }
}

/// <summary>Text drawn by Skia into a small texture and laid over a picture — a moving pattern's label and
/// counter, the identification card. Re-drawn only when its text, size or style change; the bitmap is
/// reused while the text fits it.</summary>
internal sealed class TextOverlay : IDisposable
{
    private readonly RenderServices _services;
    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private GpuTexture? _texture;
    private string _text = "";
    private float _size;
    private bool _chip;

    public TextOverlay(RenderServices services)
    {
        _services = services;
    }

    /// <summary>The drawn text's box, pixels.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Sets the text (white glyphs; a chip puts them on a translucent rounded ground).</summary>
    public void Set(string text, float size, bool chip)
    {
        if (string.Equals(text, _text, StringComparison.Ordinal) && size == _size && chip == _chip && _texture is not null)
            return;
        _text = text;
        _size = size;
        _chip = chip;
        var font = _services.Kit.Bold;
        font.Size = size;
        var padX = chip ? size * 0.7f : 2;
        var padY = chip ? size * 0.42f : 2;
        var w = (int)MathF.Ceiling(font.MeasureText(text) + padX * 2);
        var h = (int)MathF.Ceiling(size * 1.25f + padY * 2);
        // Round the bitmap up so a counter whose width wobbles keeps its texture.
        var bw = Math.Max(16, (w + 63) / 64 * 64);
        var bh = Math.Max(16, (h + 15) / 16 * 16);
        if (_bitmap is null || _bitmap.Width != bw || _bitmap.Height != bh)
        {
            _canvas?.Dispose();
            _bitmap?.Dispose();
            _texture?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul));
            _canvas = new SKCanvas(_bitmap);
            _texture = _services.Device.CreateTexture(bw, bh, TextureFormat.Rgba8);
        }
        Width = w;
        Height = h;
        var c = _canvas!;
        c.Clear(SKColors.Transparent);
        var kit = _services.Kit;
        if (chip)
            c.DrawRoundRect(SKRect.Create(0, 0, w, h), h * 0.24f, h * 0.24f, kit.FillAA(Ink.ChipBg));
        font.Size = size;
        Px.TextCentered(c, text, w / 2f, h / 2f, font, kit.Text(SKColors.White));
        c.Flush();
        _services.Device.UploadPointer(_texture!, _bitmap.GetPixels(), UploadFormat.Rgba8, _bitmap.RowBytes / 4);
    }

    /// <summary>Lays the text over a target with its top-left at (x, y), tinted (straight colour).</summary>
    public void Draw(RenderTarget target, float x, float y, float r = 1, float g = 1, float b = 1, float a = 1)
    {
        if (_texture is null || _bitmap is null)
            return;
        var d = _services.Device;
        d.SetBlend(BlendMode.Premultiplied);
        var p = d.Program("quad");
        d.BindTarget(target);
        p.Set("uRect", MathF.Round(x), MathF.Round(y), _bitmap.Width, _bitmap.Height);
        p.Set("uTarget", target.Width, target.Height);
        p.Set("uUv", 0, 0, 1, 1);
        p.SetInt("uMode", 4);
        p.Set("uOpacity", 1f);
        p.Set("uTint", r * a, g * a, b * a, a);
        d.BindSampler(p, "uTex", 0, _texture);
        d.DrawGeneratedQuads(p);
        d.SetBlend(BlendMode.Opaque);
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _canvas?.Dispose();
        _bitmap?.Dispose();
    }
}
