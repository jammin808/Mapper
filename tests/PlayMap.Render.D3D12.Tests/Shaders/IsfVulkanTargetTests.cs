using PlayMap.Core.Effects;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Render.Effects;
using Xunit.Abstractions;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The Vulkan target on the shader worker's own road, here in the test process: every built-in ISF file and every corpus
/// sample translates, compiles under glslang's relaxed rules and passes spirv-val in both stages; an error the translator finds and
/// one glslang finds each keep the ISF file's line; and — opt-in, with PLAYMAP_ISF_CORPUS naming a checkout of the public ISF
/// files — the count of the public corpus that passes those stages is recorded.</summary>
public sealed class IsfVulkanTargetTests(ITestOutputHelper log)
{
    private static readonly Lazy<RoadNatives> Natives = new(() => RoadNatives.Load());

    private static ShaderRoad Road => new(Natives.Value);

    private static void ThroughValidation(RoadResult result, string name)
    {
        foreach (var stage in new[] { ShaderRoad.Translate, ShaderRoad.GlslangStage, ShaderRoad.Validate })
        {
            var s = Assert.Single(result.Stages, x => x.Name == stage);
            Assert.True(s.Ok == true, $"{name}: {stage}: {s.Words}");
        }
    }

    [Fact]
    public void EveryNativeTheRoadNeedsIsPinnedAndLoads()
    {
        foreach (var n in Natives.Value.Loaded)
            Assert.True(n.Ok, $"{n.Name} ({n.File}): {n.Words}");
    }

    [Fact]
    public void EveryBuiltInTranslatesCompilesUnderRelaxedRulesAndValidates()
    {
        var catalog = FxCatalog.Builtin;
        Assert.NotEmpty(catalog.Kinds);
        foreach (var kind in catalog.Kinds)
        {
            var source = catalog.Source(kind.Id)!;
            ThroughValidation(Road.Run(source.Fragment, source.Vertex), kind.Id);
        }
    }

    [Fact]
    public void EveryCorpusSampleTranslatesCompilesUnderRelaxedRulesAndValidates()
    {
        foreach (var (name, fs, vs) in IsfCorpus.Samples)
            ThroughValidation(Road.Run(fs, vs), name);
    }

    [Fact]
    public void TheTargetDeclaresVersion450TheColourAtLocation0AndFixedImageBindings()
    {
        var (_, fs, vs) = IsfCorpus.Samples.Single(s => s.Name == "inputs");
        var t = IsfTranslator.TranslateVulkan(IsfParser.Parse(fs), vs);
        Assert.Empty(t.Issues);
        Assert.StartsWith("#version 450\n", t.Fragment, StringComparison.Ordinal);
        Assert.StartsWith("#version 450\n", t.Vertex, StringComparison.Ordinal);
        Assert.Contains("layout(location = 0) out vec4 isf_FragColor;", t.Fragment, StringComparison.Ordinal);
        Assert.Contains("\nin vec2 isf_FragNormCoord;", t.Fragment, StringComparison.Ordinal);
        Assert.Contains("\nout vec2 isf_FragNormCoord;", t.Vertex, StringComparison.Ordinal);
        Assert.Contains("uniform IsfBuiltins", t.Fragment, StringComparison.Ordinal);
        // inputImage, matte, wave, fft, grain: in the file's order, from binding 2.
        Assert.Contains("layout(set = 0, binding = 2) uniform sampler2D inputImage;", t.Fragment, StringComparison.Ordinal);
        Assert.Contains("layout(set = 0, binding = 6) uniform sampler2D grain;", t.Fragment, StringComparison.Ordinal);
        Assert.Contains("uniform vec4 tint;", t.Fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void ATranslatorErrorKeepsItsIsfLine()
    {
        const string fs = "/*{ \"INPUTS\": [ { \"NAME\": \"inputImage\", \"TYPE\": \"image\" } ] }*/\nvoid main() {\n\n    gl_FragColor = IMG_PIXEL(nothere, gl_FragCoord.xy);\n}\n";
        var result = Road.Run(fs, null);
        Assert.Equal(ShaderRoad.Translate, result.Stage);
        Assert.Equal(4, result.IsfLine);
        Assert.Contains("nothere", result.Words, StringComparison.Ordinal);
        Assert.All(result.Stages.Skip(1), s => Assert.Null(s.Ok));
    }

    [Fact]
    public void AGlslangErrorKeepsItsIsfLineInEitherStage()
    {
        const string fs = "/*{ \"INPUTS\": [ ] }*/\nvoid main() {\n    float x = 1.0;\n    gl_FragColor = vec4(undeclared, x, 0.0, 1.0);\n}\n";
        var result = Road.Run(fs, null);
        Assert.Equal(ShaderRoad.GlslangStage, result.Stage);
        Assert.Equal(4, result.IsfLine);
        Assert.Contains("undeclared", result.Words, StringComparison.Ordinal);
        Assert.Equal([true, false, null, null, null], result.Stages.Select(s => s.Ok));

        const string good = "/*{ \"INPUTS\": [ ] }*/\nvoid main() { gl_FragColor = vec4(1.0); }\n";
        const string vs = "/*{ }*/\nvoid main() {\n    isf_vertShaderInit();\n    nonsense = 1;\n}\n";
        var vertex = Road.Run(good, vs);
        Assert.Equal(ShaderRoad.GlslangStage, vertex.Stage);
        Assert.Equal(4, vertex.IsfLine);
        Assert.StartsWith("line 4: vertex shader:", vertex.Words, StringComparison.Ordinal);
    }

    /// <summary>The public ISF files (PLAYMAP_ISF_CORPUS: a folder of .fs files, with any .vs beside its .fs): each through
    /// translate, glslang and validate; the count is written to the test's output. The count on the whole road, to DXIL and one draw
    /// on the Direct3D 12 device, is wp3-shaders-2's.</summary>
    [Fact]
    public void ThePublicCorpusIsCountedWhenGiven()
    {
        var folder = Environment.GetEnvironmentVariable("PLAYMAP_ISF_CORPUS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            log.WriteLine("PLAYMAP_ISF_CORPUS names no folder: the public corpus is not counted here");
            return;
        }
        var files = Directory.EnumerateFiles(folder, "*.fs", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var passed = 0;
        foreach (var file in files)
        {
            var vsPath = Path.ChangeExtension(file, ".vs");
            var result = Road.Run(File.ReadAllText(file), File.Exists(vsPath) ? File.ReadAllText(vsPath) : null);
            if (result.Stages.Take(3).All(s => s.Ok == true))
                passed++;
            else
                log.WriteLine($"{Path.GetFileName(file)}: {result.Stage}: {result.Words}");
        }
        log.WriteLine($"{passed} of {files.Count} public ISF files pass translate, glslang -R and spirv-val");
        Assert.NotEmpty(files);
    }
}
