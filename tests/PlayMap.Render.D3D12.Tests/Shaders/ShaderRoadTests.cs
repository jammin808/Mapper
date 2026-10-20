using System.Text.RegularExpressions;
using PlayMap.Core.Effects;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Render.Effects;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The whole road in the test process, on both jobs (DXC's Linux build emits validated, signed DXIL: M6): every built-in
/// and every corpus sample reaches signed DXIL with each entry's reflection; the layout the road answers is the ISF file's own names
/// at the offsets DXC's reflection reads, the images at their slots; the HLSL binds the default block at b0, the built-ins at b1 (when
/// an entry reads one), image i at t i and every image's sampling through the one sampler at s0; a resource an ISF effect never binds
/// is refused at cross with its name, before DXC; a vertex entry's last writes of its position are the render hardware interface's
/// conversion to Direct3D's clip space (ClipSpace.ToDirect3D: y negated, then z to (z + w) / 2), whether the translator writes the
/// stage or a file of the user's own does, and a pixel entry writes none; a varying carries one TEXCOORD semantic in both entries
/// whatever order and subset the fragment file declares, and one the fragment file reads that no vertex output of its name and type
/// writes is refused at cross with its name; a stage that fails leaves the later stages not run, with the words why.</summary>
public sealed partial class ShaderRoadTests
{
    private static readonly Lazy<RoadNatives> Natives = new(() => RoadNatives.Load());

    private static ShaderRoad Road => new(Natives.Value);

    [Fact]
    public void EveryBuiltInAndCorpusSampleReachesSignedDxilWithReflection()
    {
        var catalog = FxCatalog.Builtin;
        var sources = catalog.Kinds.Select(k => (k.Id, catalog.Source(k.Id)!.Fragment, catalog.Source(k.Id)!.Vertex))
            .Concat(IsfCorpus.Samples.Select(s => (s.Name, s.Fs, s.Vs)));
        foreach (var (name, fs, vs) in sources)
        {
            var result = Road.Run(fs, vs);
            Assert.True(result.Ok, $"{name}: {result.Stage}: {result.Words}");
            Assert.Equal(ShaderRoad.StageNames, result.Stages.Select(s => s.Name));
            Assert.All(result.Stages, s => Assert.True(s.Ok));
            Assert.Equal(2, result.Dxil.Length);
            Assert.All(result.Dxil, d => Assert.True(new DxilOutput(true, d, [], [], "").Signed, $"{name}: unsigned DXIL"));
            Assert.All(result.Reflection, r => Assert.NotEmpty(r));
        }
    }

    [Fact]
    public void TheLayoutIsTheFilesOwnNamesAtTheOffsetsDxcReflects()
    {
        var source = FxCatalog.Builtin.Source("fx.levels")!;
        var result = Road.Run(source.Fragment, source.Vertex);
        Assert.True(result.Ok, result.Words);
        var names = result.Layout.Constants.Select(c => c.Name).ToList();
        Assert.Equal(["inBlack", "inWhite", "gamma", "outBlack", "outWhite", "_inputImage_imgSize", "_inputImage_imgRect", "_inputImage_flip"], names);
        Assert.Equal(["float", "float", "float", "float", "float", "float2", "float4", "int"], result.Layout.Constants.Select(c => c.Type));
        var texture = Assert.Single(result.Layout.Textures);
        Assert.Equal(new LayoutTexture("inputImage", 0, true), texture);

        // DXC's own reflection of the pixel entry: the same offsets in the cbuffer at b0, the built-ins at b1, the image at t0 and
        // the one sampler at s0.
        var reflection = Natives.Value.Dxc!.Reflect(result.Reflection[1], out var words);
        Assert.True(reflection is not null, words);
        var b0 = reflection.Bindings.Single(b => b.Kind == "cbuffer" && b.Register == 0);
        Assert.Equal(b0.Name, reflection.Constants.First(c => c.Name.EndsWith("inBlack", StringComparison.Ordinal)).Buffer);
        foreach (var c in result.Layout.Constants)
        {
            var reflected = Assert.Single(reflection.Constants, r => r.Buffer == b0.Name && r.Name.EndsWith(c.Name.TrimStart('_'), StringComparison.Ordinal));
            Assert.Equal(c.Offset, reflected.Offset);
            Assert.Equal(c.Type, reflected.Type);
        }
        Assert.Contains(reflection.Bindings, b => b.Kind == "texture" && b.Register == 0 && b.Name == "inputImage");
        Assert.Contains(reflection.Bindings, b => b.Kind == "sampler" && b.Register == 0);
        Assert.All(reflection.Bindings, b => Assert.Equal(0, b.Space));

        // An effect that reads a built-in binds their block at b1 (DXC binds only the blocks an entry reads).
        var (_, generator, _) = IsfCorpus.Samples.Single(x => x.Name == "generator");
        var timed = Road.Run(generator, null);
        Assert.True(timed.Ok, timed.Words);
        var timedReflection = Natives.Value.Dxc!.Reflect(timed.Reflection[1], out words);
        Assert.True(timedReflection is not null, words);
        var b1 = Assert.Single(timedReflection.Bindings, b => b.Kind == "cbuffer" && b.Register == 1);
        Assert.Contains(timedReflection.Constants, c => c.Buffer == b1.Name && c.Name.EndsWith("TIME", StringComparison.Ordinal) && c.Offset == 16);
    }

    [Fact]
    public void FiveImagesBindTheirTexturesAtT0ToT4AndSampleThroughTheOneSamplerAtS0()
    {
        const string fs = "/*{ \"INPUTS\": [ {\"NAME\":\"a\",\"TYPE\":\"image\"}, {\"NAME\":\"b\",\"TYPE\":\"image\"}, "
            + "{\"NAME\":\"c\",\"TYPE\":\"image\"}, {\"NAME\":\"d\",\"TYPE\":\"image\"}, {\"NAME\":\"e\",\"TYPE\":\"image\"} ] }*/\n"
            + "void main() { gl_FragColor = IMG_NORM_PIXEL(a, isf_FragNormCoord) + IMG_NORM_PIXEL(b, isf_FragNormCoord) + "
            + "IMG_NORM_PIXEL(c, isf_FragNormCoord) + IMG_NORM_PIXEL(d, isf_FragNormCoord) + IMG_NORM_PIXEL(e, isf_FragNormCoord); }\n";
        var result = Road.Run(fs, null);
        Assert.True(result.Ok, $"{result.Stage}: {result.Words}");
        Assert.Equal([0, 1, 2, 3, 4], result.Layout.Textures.Select(t => t.Slot));
        var reflection = Natives.Value.Dxc!.Reflect(result.Reflection[1], out var words);
        Assert.True(reflection is not null, words);
        Assert.Equal([("a", 0), ("b", 1), ("c", 2), ("d", 3), ("e", 4)],
            reflection.Bindings.Where(b => b.Kind == "texture").OrderBy(b => b.Register).Select(b => (b.Name, b.Register)));
        var sampler = Assert.Single(reflection.Bindings, b => b.Kind == "sampler");
        Assert.Equal((SpirvCross.SharedSampler, 0, 0), (sampler.Name, sampler.Register, sampler.Space));
    }

    [Fact]
    public void AVertexEntryEndsWithTheConversionToDirect3DsClipSpaceAndAPixelEntryWritesNoPosition()
    {
        const string own = "/*{ }*/\nvoid main() {\n    isf_vertShaderInit();\n    gl_Position.xy *= 0.5;\n    gl_Position.z = -0.25;\n}\n";
        var invert = FxCatalog.Builtin.Source("fx.invert")!;
        var (_, v1, v1Vertex) = IsfCorpus.Samples.Single(x => x.Name == "v1");
        (string Name, string Fs, string? Vs)[] effects = [("fx.invert", invert.Fragment, null), ("v1", v1, v1Vertex), ("own", invert.Fragment, own)];
        var glslang = Natives.Value.Glslang!;
        var cross = Natives.Value.Cross!;
        foreach (var (name, fs, vs) in effects)
        {
            var source = IsfTranslator.TranslateVulkan(IsfParser.Parse(fs), vs);
            Assert.Empty(source.Issues);
            var vertex = glslang.Compile(source.Vertex, vertex: true, "effect.vs");
            Assert.True(vertex.Ok, $"{name}: {vertex.Log}");
            var hlsl = cross.ToHlsl(vertex.Words);
            Assert.True(hlsl.Ok, $"{name}: {hlsl.Words}");
            var writes = PositionWrite().Matches(hlsl.Hlsl).Select(m => m.Groups["s"].Value).ToList();
            Assert.True(writes.Count >= 3, $"{name}: the vertex entry writes its position {writes.Count} times:\n{hlsl.Hlsl}");
            Assert.Equal(["gl_Position.y = -gl_Position.y;", "gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;"], writes[^2..]);

            var fragment = glslang.Compile(source.Fragment, vertex: false, "effect.fs");
            Assert.True(fragment.Ok, $"{name}: {fragment.Log}");
            var pixel = cross.ToHlsl(fragment.Words);
            Assert.True(pixel.Ok, $"{name}: {pixel.Words}");
            Assert.Empty(PositionWrite().Matches(pixel.Hlsl));
            Assert.True(Road.Run(fs, vs).Ok, name);
        }
    }

    [GeneratedRegex(@"^\s*(?<s>gl_Position\b[^=;]*[-+*/]?=[^=][^;]*;)", RegexOptions.Multiline)]
    private static partial Regex PositionWrite();

    private const string TwoVaryings = "/*{ }*/\nvarying vec2 a;\nvarying vec2 b;\nvoid main() {\n    isf_vertShaderInit();\n"
        + "    a = isf_FragNormCoord * 0.5;\n    b = isf_FragNormCoord * 0.25;\n}\n";

    [Theory]
    [InlineData("varying vec2 b;\nvarying vec2 a;\nvoid main() { gl_FragColor = vec4(a, b); }\n", "a,b")]
    [InlineData("varying vec2 b;\nvoid main() { gl_FragColor = vec4(b, 0.0, 1.0); }\n", "b")]
    public void SameNamedVaryingsMeetAtOneSemanticWhicheverOrderAndSubsetTheFragmentFileDeclares(string body, string reads)
    {
        var result = Road.Run("/*{ }*/\n" + body, TwoVaryings);
        Assert.True(result.Ok, $"{result.Stage}: {result.Words}");
        var written = Semantics(result.Hlsl[0]);
        var read = Semantics(result.Hlsl[1]);
        Assert.Equal(["a", "b", "isf_FragNormCoord"], written.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(reads.Split(',').Append("isf_FragNormCoord").Order(StringComparer.Ordinal), read.Keys.Order(StringComparer.Ordinal));
        Assert.All(read, r => Assert.Equal((r.Key, written.GetValueOrDefault(r.Key, -1)), (r.Key, r.Value)));
    }

    [Fact]
    public void AVaryingTheFragmentFileReadsAndNoVertexStageWritesIsRefusedAtCrossWithItsName()
    {
        const string fs = "/*{ }*/\nvarying vec2 c;\nvoid main() { gl_FragColor = vec4(c, 0.0, 1.0); }\n";
        foreach (var vs in new[] { TwoVaryings, null })
        {
            var result = Road.Run(fs, vs);
            Assert.Equal(ShaderRoad.Cross, result.Stage);
            Assert.Equal("c: the fragment shader reads a varying the vertex shader does not write", result.Words);
            Assert.Equal([true, true, true, false, null], result.Stages.Select(s => s.Ok));
        }

        const string wider = "/*{ }*/\nvarying vec3 a;\nvoid main() { gl_FragColor = vec4(a, 1.0); }\n";
        var typed = Road.Run(wider, TwoVaryings);
        Assert.Equal(ShaderRoad.Cross, typed.Stage);
        Assert.Equal("a: the fragment shader reads it as float3, the vertex shader writes float2", typed.Words);
    }

    /// <summary>Each stage variable's TEXCOORD index in an entry's HLSL (a vertex entry's are its outputs, a pixel entry's its
    /// inputs: neither writes another kind).</summary>
    private static Dictionary<string, int> Semantics(string hlsl) =>
        TexcoordMember().Matches(hlsl).ToDictionary(m => m.Groups["n"].Value,
            m => int.Parse(m.Groups["i"].Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);

    [GeneratedRegex(@"\b(?<n>\w+)\s*:\s*TEXCOORD(?<i>\d+)\s*;")]
    private static partial Regex TexcoordMember();

    [Fact]
    public void AResourceAnIsfEffectNeverBindsIsRefusedAtCrossWithItsNameBeforeDxc()
    {
        const string fs = "/*{ \"INPUTS\": [ {\"NAME\":\"inputImage\",\"TYPE\":\"image\"} ] }*/\n"
            + "layout(std430, binding = 9) buffer Extra { uint extra[]; };\n"
            + "void main() { gl_FragColor = IMG_NORM_PIXEL(inputImage, isf_FragNormCoord) * float(extra[0]); }\n";
        var result = Road.Run(fs, null);
        Assert.False(result.Ok);
        Assert.Equal(ShaderRoad.Cross, result.Stage);
        Assert.Equal("Extra: a storage buffer is not a resource an ISF effect binds", result.Words);
        Assert.Equal([true, true, true, false, null], result.Stages.Select(s => s.Ok));
        Assert.Equal("not run: the cross stage failed", result.Stages[4].Words);
        Assert.Empty(result.Dxil);
    }

    [Fact]
    public void AStageThatFailsLeavesTheLaterStagesNotRunWithWords()
    {
        var result = Road.Run("not an ISF file", null);
        Assert.False(result.Ok);
        Assert.Equal(ShaderRoad.Translate, result.Stage);
        Assert.False(result.Stages[0].Ok);
        Assert.All(result.Stages.Skip(1), s =>
        {
            Assert.Null(s.Ok);
            Assert.Equal("not run: the translate stage failed", s.Words);
        });
        Assert.Empty(result.Dxil);
    }

    [Fact]
    public void ANativeThatIsNotThePinnedBuildIsNeverLoaded()
    {
        // A folder holding a file by a pinned native's name that is not its build: the stage that needs it fails with the hash.
        var folder = Directory.CreateTempSubdirectory("playmap-natives-").FullName;
        try
        {
            var pin = CompilerNatives.Pins.Single(p => p.Name == CompilerNatives.SpirvCross);
            File.WriteAllText(Path.Combine(folder, pin.File), "not the library");
            Assert.False(CompilerNatives.TryLoad(CompilerNatives.SpirvCross, out var library, out var words, folder));
            Assert.Equal(0, library);
            Assert.Contains("is not the pinned build", words, StringComparison.Ordinal);
            Assert.Contains(CompilerNatives.HashOf(Path.Combine(folder, pin.File)), words, StringComparison.Ordinal);
            using var natives = RoadNatives.Load(folder);
            Assert.Null(natives.Cross);
            var source = FxCatalog.Builtin.Source("fx.invert")!;
            var result = new ShaderRoad(natives).Run(source.Fragment, null);
            Assert.False(result.Ok);
            // glslang and the validator are not in the folder either: the first native stage says so.
            Assert.Equal(ShaderRoad.GlslangStage, result.Stage);
            Assert.Contains("is not beside the worker", result.Words, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
