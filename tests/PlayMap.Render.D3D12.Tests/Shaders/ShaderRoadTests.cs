using PlayMap.Engine.ShaderWorker;
using PlayMap.Render.Effects;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The whole road in the test process, on both jobs (DXC's Linux build emits validated, signed DXIL: M6): every built-in
/// and every corpus sample reaches signed DXIL with each entry's reflection; the layout the road answers is the ISF file's own names
/// at the offsets DXC's reflection reads, the images at their slots; the HLSL binds the default block at b0, the built-ins at b1 (when
/// an entry reads one) and image i at t i and s i; a stage that fails leaves the later stages not run, with the words why.</summary>
public sealed class ShaderRoadTests
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

        // DXC's own reflection of the pixel entry: the same offsets in the cbuffer at b0, the built-ins at b1, the image at t0 and s0.
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
