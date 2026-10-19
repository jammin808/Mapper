using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Render.Engine;
using SkiaSharp;

namespace PlayMap.Render.Generators;

/// <summary>A particle field: the deterministic CPU simulation (<see cref="ParticleSim"/>) stepped to the
/// leader time, its sprites uploaded as instance data each frame (a buffer reused frame to frame) and drawn
/// as instanced quads from a white shape atlas — tinted, rotated, premultiplied, additive when the field
/// glows. A sting's shake moves the field and its flash goes through the guard.</summary>
internal sealed class ParticleGenerator : GeneratorRenderer
{
    internal const int Sprite = 128;
    private static readonly VertexAttribute[] Layout =
    [
        new(0, 4, ParticleSim.InstanceFloats * 4, 0, Buffer: 1, Divisor: 1),
        new(1, 4, ParticleSim.InstanceFloats * 4, 16, Buffer: 1, Divisor: 1),
    ];

    private static readonly DrawState Sprites = FrameLists.Quads(BlendMode.Premultiplied);
    private static readonly DrawState GlowingSprites = FrameLists.Quads(BlendMode.AddPremultiplied);

    private readonly RenderTarget _target;
    private readonly GpuBuffer _instances;
    private readonly GpuMesh _mesh;
    private readonly GpuTexture _atlas;
    private readonly ProgramId _sprite;
    private readonly FlashGuard _guard = new();
    private readonly ParticleSim _sim = new();
    private float[] _data = [];
    private ColorRgb _background = ColorRgb.Black;

    public ParticleGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
        : base(services, kind, width, height, context)
    {
        _sprite = Device.Programs.Id("sprite");
        _target = Device.CreateTarget(new TargetDesc(width, height, TextureFormat.Rgba16F));
        _instances = Device.CreateBuffer(new BufferDesc(0, BufferUsage.Instance));
        Device.SetBufferData<float>(_instances, new float[ParticleSim.InstanceFloats]);
        _mesh = Device.CreateMesh(Layout, null, null, _instances);
        _atlas = Device.CreateTexture(new TextureDesc(Sprite * 5, Sprite, TextureFormat.Rgba8, FrameLists.Uploaded));
        DrawAtlas(List, _atlas);
    }

    public override GpuTexture Output => _target.Color;

    public override bool Animated => true;

    /// <summary>The field (tests, and a late joiner's leader).</summary>
    public ParticleSim Sim => _sim;

    protected override void OnConfigure()
    {
        var settings = ParticleSettings.Resolve(Params);
        _sim.Configure(settings, Width, Height);
        _background = ColorRgb.Parse(settings.Background, ColorRgb.Black);
        var need = settings.Count * ParticleSim.InstanceFloats;
        if (_data.Length < need)
            _data = new float[need];
        _guard.Reset();
    }

    protected override void Draw(in GeneratorFrame frame)
    {
        _sim.Advance(frame.Seconds, frame.Impulse);
        var n = _sim.WriteInstances(_data);
        var list = List;
        if (n > 0)
            Device.SetBufferData<float>(_instances, _data.AsSpan(0, n * ParticleSim.InstanceFloats));
        using (list.Pass(_target, FrameLists.Clear(_background.R, _background.G, _background.B, 1)))
        {
            if (n > 0)
            {
                var (dx, dy) = _sim.Surge.ShakeOffset(Height);
                list.Draw(_sprite, _sim.Additive ? GlowingSprites : Sprites)
                    .Set("uTarget", Width, Height)
                    .Set("uAtlas", ShapeIndex(_sim.Settings!.Shape) / 5f, 1f / 5f)
                    .Set("uOffset", dx, dy)
                    .Texture("uTex", _atlas)
                    .Instanced(_mesh, 4, n);
            }
        }
        var flash = frame.FlashGuard ? _guard.Limit(_sim.Surge.Flash, frame.Seconds) : _sim.Surge.Flash;
        Services.DrawFlash(_target, flash);
    }

    internal static int ShapeIndex(ParticleShape shape) => shape switch
    {
        ParticleShape.Square => 1,
        ParticleShape.Star => 2,
        ParticleShape.Streak => 3,
        ParticleShape.Bokeh => 4,
        _ => 0,
    };

    /// <summary>The five white sprites side by side, premultiplied: circle, square, star, streak, bokeh.</summary>
    internal static void DrawAtlas(ICommandList list, GpuTexture atlas)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Sprite * 5, Sprite, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bitmap))
        using (var fill = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill })
        {
            c.Clear(SKColors.Transparent);
            const float half = Sprite / 2f;
            c.DrawCircle(half, half, half * 0.9f, fill);

            c.DrawRect(SKRect.Create(Sprite + half * 0.25f, half * 0.25f, Sprite * 0.75f, Sprite * 0.75f), fill);

            using (var path = new SKPath())
            {
                const int points = 5;
                for (var i = 0; i < points * 2; i++)
                {
                    var r = (i & 1) == 0 ? half * 0.95f : half * 0.4f;
                    var a = i * MathF.PI / points - MathF.PI / 2;
                    var x = 2 * Sprite + half + r * MathF.Cos(a);
                    var y = half + r * MathF.Sin(a);
                    if (i == 0)
                        path.MoveTo(x, y);
                    else
                        path.LineTo(x, y);
                }
                path.Close();
                c.DrawPath(path, fill);
            }

            c.DrawRoundRect(SKRect.Create(3 * Sprite + 4, half - Sprite * 0.09f, Sprite - 8, Sprite * 0.18f), Sprite * 0.09f, Sprite * 0.09f, fill);

            using (var shader = SKShader.CreateRadialGradient(new SKPoint(4 * Sprite + half, half), half * 0.95f,
                [SKColors.White, SKColors.White.WithAlpha(0xA0), SKColors.White.WithAlpha(0)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp))
            {
                fill.Shader = shader;
                c.DrawRect(SKRect.Create(4 * Sprite, 0, Sprite, Sprite), fill);
                fill.Shader = null;
            }
            c.Flush();
        }
        RenderServices.Upload(list, atlas, bitmap);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _mesh.Dispose();
        _instances.Dispose();
        _atlas.Dispose();
        _target.Dispose();
    }
}
