using PlayMap.Engine.ShaderWorker;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>Every program of the HLSL library compiles to validated, signed DXIL with a PDB and its reflection, both entries, from
/// the sources and headers the backend's assembly embeds — on both jobs (M6: DXC's Linux build signs) — and the set is the GL
/// library's. On Windows the build's own compile (Microsoft.Direct3D.DXC's dxc.exe) embedded the same set: each entry's DXIL signed
/// and its reflection beside it; on Linux, where no DXC executable is packaged, the build embeds none.</summary>
public sealed class HlslBuildTests
{
    [Fact]
    public void EveryProgramCompilesToSignedDxilWithAPdbAndReflection()
    {
        var failed = new List<string>();
        foreach (var c in HlslCompile.Programs)
        {
            foreach (var (stage, s) in new[] { ("vs", c.Vs), ("ps", c.Ps) })
            {
                if (!s.Output.Ok)
                {
                    failed.Add($"{c.Program.Name} {stage}: {s.Words}");
                    continue;
                }
                Assert.True(s.Output.Signed, $"{c.Program.Name} {stage}: unsigned DXIL");
                Assert.NotEmpty(s.Output.Pdb);
                Assert.NotEmpty(s.Output.Reflection);
                Assert.NotNull(s.Reflection);
            }
        }
        Assert.True(failed.Count == 0, string.Join("\n", failed));
        Assert.Equal(PlayMap.Render.Shaders.ShaderLibrary.Names.Order(StringComparer.Ordinal),
            HlslCompile.Programs.Select(c => c.Program.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheBuildEmbedsTheLibraryAndOnWindowsItsSignedDxil()
    {
        foreach (var p in HlslLibrary.Programs())
            Assert.NotNull(HlslCompile.Resource($"shaders/gen/{p.Name}/Layout.hlsli"));
        Assert.NotNull(HlslCompile.Resource("shaders/programs.json"));
        Assert.NotNull(HlslCompile.Resource("shaders/Common.hlsli"));
        var blobs = HlslCompile.Resources("shaders/dxil/").ToList();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(blobs);
            return;
        }
        var want = HlslLibrary.Programs().SelectMany(p => new[] { "vs.dxil", "vs.refl", "ps.dxil", "ps.refl" }.Select(f => $"shaders/dxil/{p.Name}.{f}"))
            .Order(StringComparer.Ordinal);
        Assert.Equal(want, blobs);
        foreach (var dxil in blobs.Where(b => b.EndsWith(".dxil", StringComparison.Ordinal)))
            Assert.True(new DxilOutput(true, HlslCompile.Resource(dxil)!, [], [], "").Signed, $"{dxil} is not signed");
    }
}
