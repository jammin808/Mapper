using System.Globalization;
using System.Text;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;
using PlayMap.Render.Shaders;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The HLSL library's set, on both jobs: programs.json names every program of the GLSL library once, in the program table's
/// order, each in a family file that holds its entries; the byte-exact programs are the two that only load (video.pack and
/// video.unpack), the HAP encoder's four are compiled and reflected but held to no pixels this round; and the build's header for
/// each program is the one the render hardware interface's program table gives (its constants, in the table's order, and its
/// texture slots), one header per program of the table.</summary>
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
            var layout = ProgramTable.Product.Single(l => l.Name == exact.Name);
            Assert.All(layout.Textures, t => Assert.False(t.Sampled, $"{exact.Name} samples {t.Name}"));
            Assert.DoesNotContain(".Sample", HlslLibrary.BlockOf(exact), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EachProgramsHeaderIsTheOneTheProgramTableGives()
    {
        Assert.Equal(ProgramTable.Product.Select(l => l.Name), HlslLibrary.Programs().Select(p => p.Name));
        Assert.Equal(ProgramTable.Product.Select(l => $"shaders/gen/{l.Name}/Layout.hlsli").Order(StringComparer.Ordinal),
            HlslCompile.Resources("shaders/gen/"));
        foreach (var layout in ProgramTable.Product)
        {
            var header = HlslCompile.Resource($"shaders/gen/{layout.Name}/Layout.hlsli");
            Assert.True(header is not null, $"the build embedded no header for {layout.Name}");
            Assert.Equal(Header(layout), Encoding.UTF8.GetString(header!).ReplaceLineEndings("\n"));
        }
    }

    /// <summary>A program's header as the build writes it from its layout: the program's define, its constants as the cbuffer at b0
    /// in the table's order (a matrix row_major, an array with its length) and a SLOT_ define for each texture.</summary>
    private static string Header(ProgramLayout layout)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"// Made by the build from the program table (programs.layout.json) for the program {layout.Name}: never edited by hand.\n");
        text.Append(CultureInfo.InvariantCulture, $"#define {HlslLibrary.ProgramDefine(layout.Name)} 1\n");
        if (!layout.Constants.IsEmpty)
        {
            text.Append("cbuffer Program : register(b0)\n{\n");
            foreach (var c in layout.Constants)
            {
                var array = c.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"[{c.Count}]") : "";
                text.Append(CultureInfo.InvariantCulture,
                    $"    {(ConstantTypes.IsMatrix(c.Type) ? "row_major " : "")}{ConstantTypes.Word(c.Type)} {c.Name}{array}; // offset {c.Offset}\n");
            }
            text.Append("};\n");
        }
        foreach (var t in layout.Textures)
            text.Append(CultureInfo.InvariantCulture, $"#define SLOT_{t.Name} {t.Slot}\n");
        return text.ToString();
    }
}
