using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Shaders;
using Silk.NET.OpenGL;
using Xunit.Abstractions;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>The ES dialect on a real OpenGL ES context (EGL surfaceless on Mesa): every library program compiles and links, the
/// caps come from the context, BGRA uploads sample right through the swizzle, and an output renders through the whole frame.</summary>
[Collection("es")]
public sealed class GlDialectTests(EsFixture es, ITestOutputHelper log)
{
    [Fact]
    public void TheContextIsEsAndSaysSo()
    {
        es.Run(c =>
        {
            log.WriteLine($"{c.Device.Version} · {c.Device.Renderer} · {c.Api}");
            Assert.Equal(GlDialect.Es300, c.Dialect);
            Assert.StartsWith("OpenGL ES 3.", c.Device.Version, StringComparison.Ordinal);
            Assert.Equal("es300", c.Device.Caps.Dialect);
            Assert.False(c.Device.Caps.IsCore);
            Assert.Equal("egl-surfaceless", c.Api);
            Assert.False(c.HasWindows);
            Assert.Empty(c.Monitors());
            Assert.True(c.Device.GlVersion >= 30, $"GL_VERSION reads {c.Device.GlVersion}");
        });
    }

    [Fact]
    public void EveryLibraryProgramCompilesAndLinksOnEs300()
    {
        var failed = es.Run(c =>
        {
            var bad = new List<string>();
            foreach (var name in ShaderLibrary.Names)
            {
                try
                {
                    c.Device.Program(name);
                }
                catch (GlException ex)
                {
                    bad.Add($"{name}: {ex.Message}");
                }
            }
            return bad;
        });
        Assert.True(failed.Count == 0, string.Join("\n", failed));
        log.WriteLine($"{ShaderLibrary.Names.Count} programs compiled and linked on #version 300 es");
    }

    [Fact]
    public void ABgraUploadSamplesAsItsColourOnEs()
    {
        es.Run(c =>
        {
            var d = c.Device;
            using var tex = d.CreateTexture(4, 2, TextureFormat.Rgba8);
            var bgra = new byte[4 * 2 * 4];
            for (var i = 0; i < 8; i++)
            {
                bgra[i * 4] = 0x99;     // B
                bgra[i * 4 + 1] = 0x66; // G
                bgra[i * 4 + 2] = 0x33; // R
                bgra[i * 4 + 3] = 0xFF;
            }
            d.Upload(tex, bgra, UploadFormat.Bgra8);
            var px = Scene.ReadTexture(d, tex);
            Assert.Equal((0x33, 0x66, 0x99), Pixels.At(px, 4, 1, 1));

            // The same texture filled as RGBA afterwards samples as RGBA: the swizzle went with the BGRA upload.
            var rgba = new byte[4 * 2 * 4];
            for (var i = 0; i < 8; i++)
            {
                rgba[i * 4] = 0x10;
                rgba[i * 4 + 1] = 0x20;
                rgba[i * 4 + 2] = 0x30;
                rgba[i * 4 + 3] = 0xFF;
            }
            d.Upload(tex, rgba, UploadFormat.Rgba8);
            Assert.Equal((0x10, 0x20, 0x30), Pixels.At(Scene.ReadTexture(d, tex), 4, 2, 1));
            Assert.Empty(d.TakeErrors());
        });
    }

    [Fact]
    public void FloatTargetsRenderWhereTheCapsSaySo()
    {
        es.Run(c =>
        {
            var d = c.Device;
            log.WriteLine($"norm16 {d.Caps.Norm16}, colour-buffer float {d.Caps.ColorBufferFloat}, timer {d.HasTimerQuery}, read BGRA {d.HasReadBgra}, polygon mode {d.HasPolygonMode}");
            var format = d.TargetFormat(TextureFormat.Rgba16F, out var words);
            Assert.Equal(d.Caps.ColorBufferFloat ? TextureFormat.Rgba16F : TextureFormat.Rgba8, format);
            Assert.Equal(d.Caps.ColorBufferFloat, words is null);
            using var target = d.CreateRenderTarget(8, 8, format);
            d.BindTarget(target);
            d.Clear(0.25f, 0.5f, 0.75f, 1);
            var px = Pixels.Read(d, target);
            Pixels.Near((64, 128, 191), Pixels.At(px, 8, 3, 3), 1, "a cleared float target");
            Assert.Empty(d.TakeErrors());
        });
    }

    [Fact]
    public void AnOutputRendersThroughTheWholeFrameOnEs()
    {
        var output = Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 64, 36);
        var project = Scene.Project([], [output]);
        es.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: c.Device.HasTimerQuery));
            engine.Render(Scene.Inputs(project, 1));
            engine.Render(Scene.Inputs(project, 1.02));
            var px = new byte[64 * 36 * 4];
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((0x33, 0x66, 0x99), Pixels.At(px, 64, 10, 10));

            // Offscreen, the output is counted against its own refresh.
            var present = engine.Present("op1");
            Assert.NotNull(present);
            Assert.Equal(Platform.PresentPath.Offscreen, present.Value.Path);
            Assert.Equal(2, present.Value.Presented);
            Assert.Empty(c.Device.TakeErrors());
        });
    }
}

/// <summary>The same library on the dialect every other GPU test runs on: desktop 3.30 core, or ES 3.00 on the suite's ES leg.</summary>
[Collection("gl")]
public sealed class GlDialectCoreTests(GlFixture gl)
{
    [Fact]
    public void EveryLibraryProgramCompilesAndLinksOnTheSuitesDialect()
    {
        gl.Run(c =>
        {
            Assert.Equal(GlFixture.Dialect, c.Dialect);
            Assert.Equal(GlFixture.Dialect == GlDialect.Core330, c.Device.Caps.IsCore);
            foreach (var name in ShaderLibrary.Names)
                c.Device.Program(name);
            Assert.Empty(c.Device.TakeErrors());
        });
    }
}

/// <summary>The dialect's pure parts: headers, words, the alias loader against a fake proc table, the format table's fallbacks.</summary>
public sealed class GlDialectTableTests
{
    [Fact]
    public void EachDialectWritesItsHeaderAndWord()
    {
        Assert.Equal("#version 330 core\n", GlDialects.Header(GlDialect.Core330));
        var es = GlDialects.Header(GlDialect.Es300);
        Assert.StartsWith("#version 300 es\nprecision highp float;\nprecision highp int;\n", es, StringComparison.Ordinal);
        Assert.Contains("precision highp sampler2D;", es, StringComparison.Ordinal);
        Assert.Equal("es300", GlDialects.Word(GlDialect.Es300));
        Assert.Equal(GlDialect.Es300, GlDialects.Parse("ES300"));
        Assert.Equal(GlDialect.Core330, GlDialects.Parse("core330"));
        Assert.Null(GlDialects.Parse("auto"));
        Assert.Equal(GlDialect.Es300, GlDialects.FromVersion("OpenGL ES 3.0 (ANGLE 2.1.27548 git hash: …)"));
        Assert.Equal(GlDialect.Core330, GlDialects.FromVersion("4.5 (Core Profile) Mesa 25.2.8"));
        Assert.Equal(30, GlDialects.VersionNumber("OpenGL ES 3.0 (ANGLE 2.1.27548)"));
        Assert.Equal(45, GlDialects.VersionNumber("4.5 (Core Profile) Mesa 25.2.8"));
        Assert.Equal(32, GlDialects.VersionNumber("OpenGL ES 3.2 Mesa 25.2.8"));
        Assert.Equal(0, GlDialects.VersionNumber(""));
    }

    [Fact]
    public void TheAliasLoaderResolvesThroughTheSuffixListInOrder()
    {
        var table = new Dictionary<string, nint>(StringComparer.Ordinal)
        {
            ["glFoo"] = 1,
            ["glFooEXT"] = 2,
            ["glDebugMessageCallbackKHR"] = 10,
            ["glGetQueryObjecti64vEXT"] = 20,
            ["glGetQueryObjecti64vKHR"] = 21,
            ["glPolygonModeANGLE"] = 30,
            ["glEGLImageTargetTexture2DOES"] = 40,
        };
        var asked = new List<string>();
        var loader = AliasLoader.Wrap(name =>
        {
            asked.Add(name);
            return table.GetValueOrDefault(name);
        });

        Assert.Equal(1, loader("glFoo"));
        Assert.Equal(["glFoo"], asked);
        Assert.Equal(10, loader("glDebugMessageCallback"));
        Assert.Equal(20, loader("glGetQueryObjecti64v"));
        Assert.Equal(30, loader("glPolygonMode"));
        Assert.Equal(40, loader("glEGLImageTargetTexture2D"));
        asked.Clear();
        Assert.Equal(0, loader("glNothing"));
        Assert.Equal(["glNothing", "glNothingEXT", "glNothingKHR", "glNothingOES", "glNothingANGLE"], asked);
        Assert.Equal(["EXT", "KHR", "OES", "ANGLE"], AliasLoader.Suffixes);

        Assert.Equal(20, AliasLoader.Resolve(table.GetValueOrDefault, "glGetQueryObjecti64v", out var resolvedAs));
        Assert.Equal("glGetQueryObjecti64vEXT", resolvedAs);
        // A name that already carries a suffix is not given a second one.
        Assert.Equal(2, AliasLoader.Resolve(table.GetValueOrDefault, "glFooEXT", out resolvedAs));
        Assert.Equal("glFooEXT", resolvedAs);
    }

    [Fact]
    public void ALoaderThatThrowsForAnUnknownNameCountsAsUnresolved()
    {
        var loader = AliasLoader.Wrap(name => name == "glBarOES" ? 7 : throw new EntryPointNotFoundException(name));
        Assert.Equal(7, loader("glBar"));
        Assert.Equal(0, loader("glBaz"));
    }

    [Fact]
    public void TheFormatTableFallsBackAsTheCapsSay()
    {
        var bare = GlCaps.From("es300", 3, 0, ImmutableHashSet<string>.Empty);
        Assert.Equal(TextureFormat.Rgba16F, GlFormatTable.Sampled(bare, TextureFormat.Rgba16, out var words));
        Assert.Contains("EXT_texture_norm16", words, StringComparison.Ordinal);
        Assert.Equal(TextureFormat.R16F, GlFormatTable.Sampled(bare, TextureFormat.R16, out _));
        Assert.Equal(TextureFormat.Rgba8, GlFormatTable.Sampled(bare, TextureFormat.Rgba8, out words));
        Assert.Null(words);
        // Float targets only with EXT_color_buffer_float; without it a float target is 8-bit, with words.
        Assert.Equal(TextureFormat.Rgba8, GlFormatTable.Target(bare, TextureFormat.Rgba16F, out words));
        Assert.Contains("EXT_color_buffer_float", words, StringComparison.Ordinal);
        Assert.Equal(TextureFormat.Rgba8, GlFormatTable.Target(bare, TextureFormat.Rgba16, out _));
        Assert.Equal(TextureFormat.R8, GlFormatTable.Target(bare, TextureFormat.R32F, out _));

        var floats = GlCaps.From("es300", 3, 0, ImmutableHashSet.Create("GL_EXT_color_buffer_float"));
        Assert.Equal(TextureFormat.Rgba16F, GlFormatTable.Target(floats, TextureFormat.Rgba16F, out words));
        Assert.Null(words);
        Assert.Equal(TextureFormat.Rgba16F, GlFormatTable.Target(floats, TextureFormat.Rgba16, out words));
        Assert.Contains("EXT_texture_norm16", words, StringComparison.Ordinal);

        var full = GlCaps.From("es300", 3, 2, ImmutableHashSet.Create("GL_EXT_color_buffer_float", "GL_EXT_texture_norm16"));
        Assert.Equal(TextureFormat.Rgba16, GlFormatTable.Target(full, TextureFormat.Rgba16, out _));
        Assert.Equal(TextureFormat.Rgba16, GlFormatTable.Sampled(full, TextureFormat.Rgba16, out _));

        var core = GlCaps.From("core330", 4, 5, ImmutableHashSet<string>.Empty);
        foreach (var f in new[] { TextureFormat.Rgba16, TextureFormat.Rgba16F, TextureFormat.Rgba32F, TextureFormat.R16 })
        {
            Assert.Equal(f, GlFormatTable.Target(core, f, out words));
            Assert.Null(words);
        }
        Assert.Throws<ArgumentException>(() => GlFormatTable.Target(full, TextureFormat.Bc1, out _));
    }

    [Fact]
    public void BgraUploadsSwapThroughTheSwizzleOnEsOnly()
    {
        Assert.Equal((GLEnum.Bgra, GLEnum.UnsignedByte, false), GlFormatTable.Upload(GlDialect.Core330, UploadFormat.Bgra8));
        Assert.Equal((GLEnum.Rgba, GLEnum.UnsignedByte, true), GlFormatTable.Upload(GlDialect.Es300, UploadFormat.Bgra8));
        foreach (var f in Enum.GetValues<UploadFormat>().Where(f => f != UploadFormat.Bgra8))
            Assert.Equal(GlFormatTable.Upload(GlDialect.Core330, f), GlFormatTable.Upload(GlDialect.Es300, f));
        foreach (var f in Enum.GetValues<TextureFormat>())
            Assert.Equal(GlFormatTable.Describe(GlDialect.Core330, f), GlFormatTable.Describe(GlDialect.Es300, f));
    }
}
