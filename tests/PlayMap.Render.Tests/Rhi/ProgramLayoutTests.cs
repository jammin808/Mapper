using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;
using PlayMap.Core.Storage;
using PlayMap.Render.Generators;
using PlayMap.Render.Passes;
using PlayMap.Render.Projection;
using PlayMap.Render.Shaders;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>The program table (<c>src/PlayMap.Core/Gpu/Programs/programs.layout.json</c>, embedded in Core) is the GLSL library's
/// declarations: every program in the library's order, each listing exactly the uniforms and samplers its sources declare, by name and
/// type, packed by the block's rules, and the vertex inputs of the one mesh layout its passes draw it with, which its vertex stage
/// declares; no two programs share a name. GL itself agrees: every uniform the linked program keeps active is
/// in the table with the same type and count, in both dialects. PLAYMAP_WRITE_FIXTURES=1 writes the file from the sources; the table
/// changes only with them. Every name the renderer's sources set or bind in a draw is in the layout of the program that draw is of
/// (<see cref="DrawChainScan"/>).</summary>
[Collection("gpu")]
public sealed class ProgramLayoutTests(BackendFixture backends)
{
    private static string TablePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln");
        return Path.Combine(root, "src", "PlayMap.Core", "Gpu", "Programs", "programs.layout.json");
    }

    /// <summary>The mesh layout each program that reads a vertex buffer is drawn with, as the passes that draw it make their meshes:
    /// one layout per program (the stage's surfaces and the stage effect's plane make the same).</summary>
    private static readonly (string Program, VertexAttribute[] Layout)[] CallSites =
    [
        ("stage", SurfaceMesh.Layout),
        ("stage", EffectGenerator.Layout),
        ("output", GpuWarp.Layout),
        ("sprite", ParticleGenerator.Layout),
        ("lines", StageRenderer.LineLayout),
        ("pixelsurface", StageRenderer.PixelLayout),
        ("overlay.shapes", OverlayPass.Layout),
    ];

    /// <summary>Every library program's layout, read from its GLSL, in the library's order, with the vertex inputs its passes draw it
    /// with.</summary>
    private static List<ProgramLayout> FromGlsl() =>
        [.. ShaderLibrary.Names.Select(n =>
        {
            var (vs, fs) = ShaderLibrary.Source(n);
            var inputs = CallSites.FirstOrDefault(c => c.Program == n).Layout ?? [];
            return GlslDeclarations.Layout(n, vs, fs) with { Inputs = [.. inputs] };
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
    public void EveryMeshAPassDrawsIsItsProgramsInputsAndTheVertexStageDeclaresThem()
    {
        var table = new ProgramTable();
        // Each pass's mesh layout is the table's for the program it draws: one layout per program.
        foreach (var (program, layout) in CallSites)
            Assert.Equal(layout, table.Layout(table.Id(program)).Inputs.ToArray());
        // The vertex stage declares exactly those inputs (location and float count); a program with none reads no vertex buffer.
        foreach (var name in ShaderLibrary.Names)
        {
            var declared = GlslDeclarations.VertexInputs(ShaderLibrary.Source(name).Vertex);
            Assert.Equal(declared, table.Layout(table.Id(name)).Inputs.Select(a => (a.Location, a.Components)));
        }
        Assert.Equal(6, ProgramTable.Product.Count(p => !p.Inputs.IsEmpty));
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
        backends.RunGl(c =>
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
    public void EveryNameTheRenderersDrawsSetOrBindIsInItsProgramsLayout()
    {
        var sources = DrawChainScan.RenderSources();
        Assert.NotEmpty(sources);
        var drawn = new List<string>();
        var misses = sources.SelectMany(f => DrawChainScan.Misses(f.File, f.Source, drawn)).ToList();
        Assert.Empty(misses);
        Assert.All(DrawChainScan.Unresolved.Keys, k => Assert.Contains(sources, s => s.File == k));
        // The chains the frames draw, the LED rig's, the sends', HAP's and the effects' mix among them, are read.
        string[] expected = ["led_sample", "led_patch", "video.pack", "video.unpack", "hap", "fxmix", "yuv", "output", "quad", "blit", "mix",
            "stage", "pixelsurface", "lines", "overlay.shapes", "sprite", "content.pstep", "content.psprite", "content.raymarch", "checker",
            "motion", "colorcycle", "syncmarks", "fractal.mandelbrot", "reactive.plasma"];
        Assert.All(expected, p => Assert.Contains(p, drawn));
    }

    [Fact]
    public void TheScanCatchesAMisspeltNameAndReadsNoneInComments()
    {
        const string Good = """
            private readonly ProgramId _blit = device.Programs.Id("blit");
            private readonly ProgramId _cycle = device.Programs.Id("colorcycle");
            void Copy(ICommandList list, GpuTexture t) =>
                list.Draw(_blit, Copied).Set("uSrc", 0, 0, 1, 1).Texture("uTex", t).Fullscreen();
            void Cycle(ICommandList list)
            {
                var draw = list.Draw(_cycle, Copied);
                draw.Set("uColors[3]", 1f, 0f, 0f);
                draw.Fullscreen();
            }
            // list.Draw(_blit, Copied).Set("uSrcc", 0, 0, 1, 1).Fullscreen();
            /* list.Draw(_blit, Copied).Texture("uTx", t).Fullscreen(); */
            """;
        Assert.Empty(DrawChainScan.Misses("Sample.cs", Good));
        Assert.Equal(["Sample.cs:4: 'uScr' is no constant of blit"],
            DrawChainScan.Misses("Sample.cs", Good.Replace("Set(\"uSrc\"", "Set(\"uScr\"", StringComparison.Ordinal)));
        Assert.Equal(["Sample.cs:4: 'uTx' is no texture of blit"],
            DrawChainScan.Misses("Sample.cs", Good.Replace("Texture(\"uTex\"", "Texture(\"uTx\"", StringComparison.Ordinal)));
        Assert.Equal(["Sample.cs:8: 'uColor[3]' is no constant of colorcycle"],
            DrawChainScan.Misses("Sample.cs", Good.Replace("uColors[3]", "uColor[3]", StringComparison.Ordinal)));
        // A generated id of another program, in a chain.
        Assert.Equal(["Sample.cs:4: ProgramIds.Quad.uRect is quad's, set in a draw of blit"],
            DrawChainScan.Misses("Sample.cs", Good.Replace(".Set(\"uSrc\", 0, 0, 1, 1)", ".Set(ProgramIds.Quad.uRect, 0, 0, 1, 1)", StringComparison.Ordinal)));
        // A helper the chain hands its builder to: its names are the file's programs'.
        Assert.Equal(["Sample.cs:13: 'uNope' is no constant of blit or colorcycle"],
            DrawChainScan.Misses("Sample.cs", Good + "\nstatic void Helper(DrawBuilder d) => d.Set(\"uNope\", 1f);\n"));
        // A program the file does not name, in a file not listed.
        var unnamed = DrawChainScan.Misses("Sample.cs", "void Draw(ICommandList list, ProgramId p) => list.Draw(p, Copied).Set(\"uSrc\", 0f).Fullscreen();\n");
        Assert.Single(unnamed);
        Assert.StartsWith("Sample.cs:1: the draw's program (p) is not named in the file", unnamed[0], StringComparison.Ordinal);
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
