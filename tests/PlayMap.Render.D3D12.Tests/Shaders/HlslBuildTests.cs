using PlayMap.Engine.ShaderWorker;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>Every program of the HLSL library compiles to validated, signed DXIL with a PDB and its reflection, both entries, from
/// the sources and headers the backend's assembly embeds — on both jobs (M6: DXC's Linux build signs) — and the set is the GL
/// library's. On Windows the build's own compile (Microsoft.Direct3D.DXC's dxc.exe) embedded the same set: each entry's DXIL signed
/// and its reflection beside it, the reflection the sources make now (a stale blob fails); on Linux, where no DXC executable is
/// packaged, the build embeds none. The Windows job keeps every entry's PDB the build writes.</summary>
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

        // Each embedded entry is the one the sources and the program list make now: its reflection (constants, bindings, block sizes
        // and input signature) is the in-process compile's, so a blob the build left behind after an entry point, a file or a source
        // changed fails here.
        var stale = new List<string>();
        foreach (var c in HlslCompile.Programs)
        {
            foreach (var (stage, s) in new[] { ("vs", c.Vs), ("ps", c.Ps) })
            {
                var embedded = HlslCompile.Reflect(HlslCompile.Resource($"shaders/dxil/{c.Program.Name}.{stage}.refl")!, out var words);
                if (embedded is null || s.Reflection is null)
                {
                    stale.Add($"{c.Program.Name} {stage}: {words}{s.Words}");
                    continue;
                }
                if (!embedded.Constants.SequenceEqual(s.Reflection.Constants) || !embedded.Bindings.SequenceEqual(s.Reflection.Bindings)
                    || !embedded.Buffers.SequenceEqual(s.Reflection.Buffers) || !embedded.Inputs.SequenceEqual(s.Reflection.Inputs))
                    stale.Add($"{c.Program.Name} {stage}: the embedded entry reflects [{Describe(embedded)}], the sources make [{Describe(s.Reflection)}]");
            }
        }
        Assert.True(stale.Count == 0, string.Join("\n", stale));
    }

    /// <summary>Where the build writes each entry's PDB (Shaders.targets: the backend's intermediate folder, hlsl/&lt;program&gt;/), as the
    /// Windows job's upload names it.</summary>
    private const string SymbolsGlob = "src/PlayMap.Render.D3D12/obj/**/hlsl/**/*.pdb";

    /// <summary>The Windows job keeps the library's symbols: after its build, in a step that runs whenever the build passed, it uploads
    /// every PDB the build wrote as hlsl-symbols and fails when there is none. Where DXC's executable runs (Windows) that glob holds
    /// both entries' PDBs of every program; elsewhere the build writes none, and the job that would find none uploads nothing.</summary>
    [Fact]
    public void TheWindowsJobUploadsTheSymbolsOfEveryEntryTheBuildCompiles()
    {
        var steps = WorkflowSteps("windows");
        Assert.DoesNotContain(WorkflowSteps("test"), s => s.With.GetValueOrDefault("name") == "hlsl-symbols");
        var build = steps.FindIndex(s => s.Name == "Build");
        var upload = steps.FindIndex(s => s.With.TryGetValue("name", out var n) && n == "hlsl-symbols");
        Assert.True(build >= 0 && upload > build, $"the Windows job's Build is step {build}, the symbols' upload step {upload}");
        var step = steps[upload];
        Assert.Equal("actions/upload-artifact@v5", step.Uses);
        // No condition of its own: it runs whenever the build passed, the build that wrote the PDBs.
        Assert.Null(step.If);
        Assert.Equal((SymbolsGlob, "error"), (step.With.GetValueOrDefault("path"), step.With.GetValueOrDefault("if-no-files-found")));

        var obj = Path.Combine(HlslLibrary.RepoRoot(), "src", "PlayMap.Render.D3D12", "obj");
        var made = Directory.Exists(obj)
            ? Directory.EnumerateFiles(obj, "*.pdb", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(obj, f).Replace('\\', '/'))
                .Where(f => f.Contains("/hlsl/", StringComparison.Ordinal)).ToList()
            : [];
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(made);
            return;
        }
        var have = made.Select(f => f[(f.IndexOf("/hlsl/", StringComparison.Ordinal) + "/hlsl/".Length)..]).ToHashSet(StringComparer.Ordinal);
        var want = HlslLibrary.Programs().SelectMany(p => new[] { $"{p.Name}/vs.pdb", $"{p.Name}/ps.pdb" });
        var missing = want.Where(w => !have.Contains(w)).ToList();
        Assert.True(missing.Count == 0, "the build wrote no PDB for " + string.Join(", ", missing));
    }

    /// <summary>A step of a job in .github/workflows/build.yml: its name, the action it uses, its condition and its inputs.</summary>
    private sealed record Step
    {
        public string? Name { get; init; }
        public string? Uses { get; init; }
        public string? If { get; init; }
        public Dictionary<string, string> With { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>A job's steps, read from the workflow's lines: a job's key at two spaces, its steps at six ("- "), a step's keys at
    /// eight and its inputs at ten; comments and blank lines are skipped.</summary>
    private static List<Step> WorkflowSteps(string job)
    {
        var lines = File.ReadAllLines(Path.Combine(HlslLibrary.RepoRoot(), ".github", "workflows", "build.yml"));
        var start = Array.IndexOf(lines, $"  {job}:");
        Assert.True(start >= 0, $"build.yml has no job {job}");
        var steps = new List<Step>();
        var inWith = false;
        foreach (var raw in lines.Skip(start + 1))
        {
            if (raw.Length > 2 && raw.StartsWith("  ", StringComparison.Ordinal) && raw[2] != ' ')
                break;
            var text = raw.TrimStart();
            if (text.Length == 0 || text.StartsWith('#'))
                continue;
            var depth = raw.Length - text.Length;
            if (depth == 6 && text.StartsWith("- ", StringComparison.Ordinal))
            {
                steps.Add(new Step());
                inWith = false;
                text = text[2..];
                depth = 8;
            }
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (steps.Count == 0 || colon < 0)
                continue;
            var key = text[..colon];
            var value = text[(colon + 1)..].Trim().Trim('"');
            if (depth == 8)
            {
                inWith = key == "with";
                steps[^1] = key switch
                {
                    "name" => steps[^1] with { Name = value },
                    "uses" => steps[^1] with { Uses = value },
                    "if" => steps[^1] with { If = value },
                    _ => steps[^1],
                };
            }
            else if (depth == 10 && inWith)
            {
                steps[^1].With[key] = value;
            }
        }
        return steps;
    }

    private static string Describe(DxilReflection r) =>
        string.Join(", ", r.Constants.Select(k => $"{k.Buffer}.{k.Name}:{k.Type}{(k.RowMajor ? " row_major" : "")}@{k.Offset}")
            .Concat(r.Bindings.Select(b => $"{b.Kind} {b.Name}@{b.Register}"))
            .Concat(r.Buffers.Select(b => $"{b.Name} {b.Size} bytes"))
            .Concat(r.Inputs.Select(i => $"in {i.Semantic}{i.Index}:{i.Type}{i.Components}")));
}
