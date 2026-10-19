using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;
using PlayMap.Core.Storage;
using PlayMap.Render.Shaders;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>The program table (<c>src/PlayMap.Core/Gpu/Programs/programs.layout.json</c>, embedded in Core) is the GLSL library's
/// declarations: every program in the library's order, each listing exactly the uniforms and samplers its sources declare, by name and
/// type, packed by the block's rules; no two programs share a name. GL itself agrees: every uniform the linked program keeps active is
/// in the table with the same type and count, in both dialects. PLAYMAP_WRITE_FIXTURES=1 writes the file from the sources; the table
/// changes only with them.</summary>
[Collection("gl")]
public sealed class ProgramLayoutTests(GlFixture gl)
{
    private static string TablePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln");
        return Path.Combine(root, "src", "PlayMap.Core", "Gpu", "Programs", "programs.layout.json");
    }

    /// <summary>Every library program's layout, read from its GLSL, in the library's order.</summary>
    private static List<ProgramLayout> FromGlsl() =>
        [.. ShaderLibrary.Names.Select(n =>
        {
            var (vs, fs) = ShaderLibrary.Source(n);
            return GlslDeclarations.Layout(n, vs, fs);
        })];

    [Fact]
    public void TheTableIsTheLibrarysGlslDeclarationsByteForByte()
    {
        var path = TablePath();
        var expected = ProgramTable.Write(FromGlsl());
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            DurableFile.WriteAllText(path, expected);
        Assert.True(File.Exists(path), "the program table is generated: run this test once with PLAYMAP_WRITE_FIXTURES=1");
        Assert.Equal(expected, DurableFile.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TheEmbeddedTableListsExactlyEachProgramsUniformsAndSamplersByNameAndType()
    {
        var glsl = FromGlsl();
        var table = ProgramTable.Product;
        Assert.Equal(glsl.Select(p => p.Name), table.Select(p => p.Name));
        for (var i = 0; i < glsl.Count; i++)
        {
            var (want, got) = (glsl[i], table[i]);
            Assert.Equal(want.Constants.Select(c => (c.Name, c.Type, c.Count)), got.Constants.Select(c => (c.Name, c.Type, c.Count)));
            Assert.Equal(want.Textures.Select(t => (t.Name, t.Sampled)), got.Textures.Select(t => (t.Name, t.Sampled)));
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public void NoTwoProgramsShareANameAndEveryLibraryProgramHasALayout()
    {
        var table = new ProgramTable();
        Assert.Equal(table.Names.Count, table.Names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ShaderLibrary.Names, table.Names);
        foreach (var name in ShaderLibrary.Names)
            Assert.Equal(name, table.Layout(table.Id(name)).Name);
    }

    [Fact]
    public void EveryUniformGlKeepsActiveIsInTheTableWithItsTypeAndCount()
    {
        var table = new ProgramTable();
        gl.Run(c =>
        {
            var api = c.Device.Gl;
            foreach (var name in ShaderLibrary.Names)
            {
                var layout = table.Layout(table.Id(name));
                var program = c.Device.Program(name).Id;
                api.GetProgram(program, GLEnum.ActiveUniforms, out var count);
                for (var i = 0u; i < (uint)count; i++)
                {
                    var uniform = api.GetActiveUniform(program, i, out var size, out UniformType type);
                    // An array reports its first element ("uColors[0]") and its length.
                    var bare = uniform.EndsWith("[0]", StringComparison.Ordinal) ? uniform[..^3] : uniform;
                    if (type == UniformType.Sampler2D)
                    {
                        Assert.True(layout.TryTexture(bare, out _), $"{name}: sampler {bare} is not in the table");
                        continue;
                    }
                    Assert.True(layout.TryConstant(bare, out var slot), $"{name}: uniform {bare} is not in the table");
                    Assert.Equal((bare, Of(type), size), (slot.Name, slot.Type, slot.Count));
                }
            }
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void TheDeclarationsAreReadPastCommentsAndAcrossDeclaratorsAndALoadOnlySamplerIsNotSampled()
    {
        const string Vs = """
            uniform vec4 uRect; // uniform float uCommented;
            uniform mat4 uView;
            void main() { gl_Position = uRect * uView; }
            """;
        const string Fs = """
            /* uniform int uBlock; */
            uniform vec4 uRect;
            uniform float uA, uB;
            uniform vec3 uColors[8];
            uniform sampler2D uLoaded;
            uniform sampler2D uSampled;
            out vec4 o;
            void main() {
                ivec2 s = textureSize(uLoaded, 0);
                o = texelFetch(uLoaded, s - 1, 0) + texture(uSampled, vec2(uA, uB)) + vec4(uColors[1], 1.0);
            }
            """;
        var l = GlslDeclarations.Layout("t", Vs, Fs);
        Assert.Equal(new[] { ("uRect", ConstantType.Float4, 1), ("uView", ConstantType.Float4x4, 1), ("uA", ConstantType.Float, 1),
            ("uB", ConstantType.Float, 1), ("uColors", ConstantType.Float3, 8) }, l.Constants.Select(c => (c.Name, c.Type, c.Count)));
        Assert.Equal(new[] { new TextureSlot("uLoaded", 0, false), new TextureSlot("uSampled", 1, true) }, l.Textures);
        Assert.Throws<InvalidOperationException>(() => GlslDeclarations.Layout("t", "uniform bool uFlag;\nvoid main() {}", "void main() {}"));
        Assert.Throws<InvalidOperationException>(() => GlslDeclarations.Layout("t", "uniform float uX;\n", "uniform vec2 uX;\n"));
    }

    private static ConstantType Of(UniformType type) => type switch
    {
        UniformType.Float => ConstantType.Float,
        UniformType.FloatVec2 => ConstantType.Float2,
        UniformType.FloatVec3 => ConstantType.Float3,
        UniformType.FloatVec4 => ConstantType.Float4,
        UniformType.Int => ConstantType.Int,
        UniformType.IntVec2 => ConstantType.Int2,
        UniformType.IntVec4 => ConstantType.Int4,
        UniformType.FloatMat3 => ConstantType.Float3x3,
        UniformType.FloatMat4 => ConstantType.Float4x4,
        _ => throw new InvalidOperationException($"GL reports a uniform of type {type}, which the program table has no word for"),
    };
}
