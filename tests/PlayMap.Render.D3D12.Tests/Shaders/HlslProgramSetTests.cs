using PlayMap.Render.Shaders;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The HLSL library's set, on both jobs: programs.json names every program of the GLSL library once, each in a family file
/// that holds its entries; the byte-exact programs are the two that only load (video.pack and video.unpack), the HAP encoder's four
/// are compiled and reflected but held to no pixels this round; and the committed layout table is the one the GLSL declarations give
/// (PLAYMAP_WRITE_FIXTURES=1 writes it).</summary>
public sealed class HlslProgramSetTests
{
    [Fact]
    public void ProgramsJsonNamesEveryLibraryProgramOnceInAFileThatHoldsItsEntries()
    {
        var programs = HlslLibrary.Programs();
        Assert.Equal(ShaderLibrary.Names.Order(StringComparer.Ordinal), programs.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(programs.Count, programs.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        var common = File.ReadAllText(Path.Combine(HlslLibrary.Folder, "Common.hlsli"));
        foreach (var p in programs)
        {
            Assert.True(File.Exists(Path.Combine(HlslLibrary.Folder, p.File)), $"{p.Name}: no {p.File}");
            var block = HlslLibrary.BlockOf(p);
            Assert.True(block.Contains($" {p.Ps}(", StringComparison.Ordinal), $"{p.Name}: {p.File} has no {p.Ps} in its block");
            Assert.True(block.Contains($" {p.Vs}(", StringComparison.Ordinal) || common.Contains($" {p.Vs}(", StringComparison.Ordinal),
                $"{p.Name}: neither its block nor Common.hlsli has {p.Vs}");
        }
    }

    [Fact]
    public void TheExactProgramsAreTheTwoThatOnlyLoadAndTheEncodersAreHeldToNoPixels()
    {
        var programs = HlslLibrary.Programs();
        Assert.Equal(["video.pack", "video.unpack"], programs.Where(p => p.Parity == "exact").Select(p => p.Name));
        Assert.Equal(["hapenc.bc1", "hapenc.bc3", "hapenc.bc4", "hapenc.ycocg"], programs.Where(p => p.Parity == "encoder").Select(p => p.Name));
        Assert.All(programs, p => Assert.Contains(p.Parity, new[] { "exact", "encoder", "pixels" }));
        foreach (var exact in programs.Where(p => p.Parity == "exact"))
        {
            var layout = GlslLayout.Of(exact.Name).Layout;
            Assert.All(layout.Textures, t => Assert.False(t.Sampled, $"{exact.Name} samples {t.Name}"));
            Assert.DoesNotContain(".Sample", HlslLibrary.BlockOf(exact), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCommittedLayoutTableIsTheOneTheGlslDeclarationsGive()
    {
        var table = GlslLayout.Json(GlslLayout.All());
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            PlayMap.Core.Storage.DurableFile.WriteAllText(HlslLibrary.LayoutPath, table);
        Assert.Equal(PlayMap.Core.Storage.DurableFile.ReadAllText(HlslLibrary.LayoutPath).ReplaceLineEndings("\n"), table);
    }
}
