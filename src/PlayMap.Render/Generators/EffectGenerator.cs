using System.Numerics;
using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Generators;

/// <summary>A surface-aware effect's numbers for the stage shader: its mode (2 sweep, 3 outline, 4 relight),
/// its parameters and its colours. On a surface the projector pass draws it with the surface's own world
/// positions, normals and UVs; anywhere else it is previewed flat.</summary>
public readonly record struct EffectUniforms(int Mode, Vector4 Params, ColorRgb ColorA, ColorRgb ColorB)
{
    public static bool IsEffect(string kindId) => kindId.StartsWith("effect.", StringComparison.OrdinalIgnoreCase);

    public static EffectUniforms For(GeneratorKind kind, ParamSet set)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(set);
        ColorRgb Col(string name, ColorRgb fallback) => ColorRgb.Parse(ParamValues.Text(set, kind, name), fallback);
        float Num(string name) => (float)ParamValues.Number(set, kind, name);
        switch (kind.Id)
        {
            case "effect.sweep":
                var axis = ParamValues.Choice(set, kind, "axis") switch
                {
                    "y" => 1,
                    "z" => 2,
                    "radial" => 3,
                    _ => 0,
                };
                return new EffectUniforms(2, new Vector4(axis, Num("speed"), Num("width"), 0), Col("color", ColorRgb.White), Col("background", ColorRgb.Black));
            case "effect.outline":
                return new EffectUniforms(3, new Vector4(Num("width"), Num("pulse"), 0, 0), Col("color", ColorRgb.White), ColorRgb.Black);
            default:
                return new EffectUniforms(4, new Vector4(Num("speed"), Num("height"), Num("ambient"), 0), Col("color", ColorRgb.White), ColorRgb.Black);
        }
    }

    /// <summary>Sets the stage program's effect constants into a draw.</summary>
    public void Apply(DrawBuilder draw, double seconds) =>
        draw.SetInt("uMode", Mode)
            .Set("uParams", Params)
            .Set("uColorA", ColorA.R, ColorA.G, ColorA.B, 1)
            .Set("uColorB", ColorB.R, ColorB.G, ColorB.B, 1)
            .Set("uTime", (float)seconds);

    /// <summary>Sets the stage program's effect uniforms.</summary>
    public void Apply(ShaderProgram p, double seconds)
    {
        ArgumentNullException.ThrowIfNull(p);
        p.SetInt("uMode", Mode);
        p.Set("uParams", Params);
        p.Set("uColorA", ColorA.R, ColorA.G, ColorA.B, 1);
        p.Set("uColorB", ColorB.R, ColorB.G, ColorB.B, 1);
        p.Set("uTime", (float)seconds);
    }
}

/// <summary>An effect drawn flat (on a canvas, an output): the stage shader over a picture-shaped plane one
/// unit high in the world, facing the viewer, so a sweep crosses it, an outline frames it and a relight
/// shades it as the light goes round.</summary>
internal sealed class EffectGenerator : GeneratorRenderer
{
    private static readonly VertexAttribute[] Layout =
    [
        new(0, 3, 32, 0),
        new(1, 3, 32, 12),
        new(2, 2, 32, 24),
    ];

    /// <summary>The plane's state: opaque, no depth, no culling.</summary>
    private static readonly DrawState Flat = DrawState.Opaque;

    private readonly RenderTarget _target;
    private readonly GpuBuffer _vertices;
    private readonly GpuBuffer _indices;
    private readonly GpuMesh _mesh;
    private readonly ProgramId _stage;
    private EffectUniforms _uniforms;

    public EffectGenerator(RenderServices services, GeneratorKind kind, int width, int height, GeneratorContext context)
        : base(services, kind, width, height, context)
    {
        _stage = Device.Programs.Id("stage");
        _target = Device.CreateTarget(new TargetDesc(width, height, TextureFormat.Rgba16F));
        var aspect = width / (float)height;
        // x, y, z, normal, u, v — world y up, UV v down.
        float[] plane =
        [
            0, 1, 0, 0, 0, 1, 0, 0,
            aspect, 1, 0, 0, 0, 1, 1, 0,
            aspect, 0, 0, 0, 0, 1, 1, 1,
            0, 0, 0, 0, 0, 1, 0, 1,
        ];
        _vertices = Device.CreateBuffer(new BufferDesc(0, BufferUsage.Vertex));
        Device.SetBufferData<float>(_vertices, plane);
        _indices = Device.CreateBuffer(new BufferDesc(0, BufferUsage.Index));
        Device.SetBufferData<uint>(_indices, [0, 1, 2, 0, 2, 3]);
        _mesh = Device.CreateMesh(Layout, _vertices, _indices);
    }

    public override GpuTexture Output => _target.Color;

    public override bool Animated => true;

    protected override void OnConfigure() => _uniforms = EffectUniforms.For(Kind, Params);

    protected override void Draw(in GeneratorFrame frame)
    {
        var aspect = Width / (float)Height;
        // World (x right, y up) onto the target drawn top row first: x 0..aspect → −1..1, y 1..0 → −1..1.
        var viewProj = new Matrix4x4(2 / aspect, 0, 0, 0, 0, -2, 0, 0, 0, 0, 1, 0, -1, 1, 0, 1);
        var list = List;
        using (list.Pass(_target, PassDesc.ClearBlack))
        {
            var draw = list.Draw(_stage, Flat)
                .Set("uModel", Matrix4x4.Identity)
                .Set("uViewProj", viewProj)
                .Set("uProjector", Matrix4x4.Identity)
                .Set("uNormalMat", Matrix3x3.Identity)
                .Set("uRegion", 0, 0, 1, 1)
                .SetInt("uHasMask", 0)
                .Set("uOpacity", 1f)
                .Set("uBoundsMin", 0, 0, 0)
                .Set("uBoundsMax", aspect, 1, 0)
                .Set("uEye", aspect / 2, 0.5f, 2)
                .Texture("uSource", Services.Black)
                .Texture("uMask", Services.White);
            _uniforms.Apply(draw, frame.Seconds);
            draw.Indexed(_mesh, 6);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _mesh.Dispose();
        _vertices.Dispose();
        _indices.Dispose();
        _target.Dispose();
    }
}
