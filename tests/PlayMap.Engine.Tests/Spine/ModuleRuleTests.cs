using System.Xml.Linq;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The module rules, read from the project files: Core references only the runtime; an edge references Core and its own
/// packages, no other PlayMap project (vendored source under src/ThirdParty aside), so only the composition roots — the engine and
/// the desk — put edges together; the Direct3D 12 backend never references the GL backend; only the desk references Avalonia; and
/// the solution builds every project under src, tests and tools and no installer project: the installer is Inno Setup's script, and
/// it and its Windows-only tests (installer/) are built by the installer job alone.</summary>
public sealed class ModuleRuleTests
{
    /// <summary>A project file's references: the PlayMap projects by name, and the packages with the version each pins.</summary>
    private sealed record Project(string Name, string Path, IReadOnlyList<string> Projects, IReadOnlyDictionary<string, string> Packages);

    private static readonly string Root = NodeExe.RepoRoot();

    private static Project Read(string csproj)
    {
        var doc = XDocument.Load(csproj);
        var projects = doc.Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include") ?? "")
            .Select(p => System.IO.Path.GetFileNameWithoutExtension(p.Replace('\\', System.IO.Path.DirectorySeparatorChar))).ToList();
        var packages = doc.Descendants("PackageReference")
            .ToDictionary(e => (string?)e.Attribute("Include") ?? "", e => (string?)e.Attribute("Version") ?? "", StringComparer.Ordinal);
        return new Project(System.IO.Path.GetFileNameWithoutExtension(csproj), csproj, projects, packages);
    }

    /// <summary>Every project file under a top folder of the tree, build outputs aside.</summary>
    private static List<Project> Under(string folder) =>
        [.. Directory.EnumerateFiles(System.IO.Path.Combine(Root, folder), "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(Read)];

    private static List<Project> Source => Under("src");

    private static bool Vendored(Project p) => p.Path.Contains($"{System.IO.Path.DirectorySeparatorChar}ThirdParty{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>The composition roots: the node, which puts the edges together, and the desk with its one exe.</summary>
    private static readonly string[] Roots = ["PlayMap.Engine", "PlayMap.App"];

    /// <summary>Round 3's new projects, each an edge of Core.</summary>
    private static readonly string[] NewEdges = ["PlayMap.Render.D3D12", "PlayMap.Platform.Windows", "PlayMap.Supervisor"];

    [Fact]
    public void CoreReferencesOnlyTheRuntime()
    {
        var core = Source.Single(p => p.Name == "PlayMap.Core");
        Assert.Empty(core.Projects);
        Assert.Empty(core.Packages);
    }

    [Fact]
    public void AnEdgeReferencesCoreAndNoOtherPlayMapProject()
    {
        var edges = Source.Where(p => !Vendored(p) && p.Name != "PlayMap.Core" && !Roots.Contains(p.Name)).ToList();
        Assert.True(edges.Count >= 9, string.Join(", ", edges.Select(e => e.Name)));
        var vendored = Source.Where(Vendored).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            Assert.Contains("PlayMap.Core", edge.Projects);
            foreach (var other in edge.Projects.Where(r => r != "PlayMap.Core"))
                Assert.True(vendored.Contains(other), $"{edge.Name} references {other}: an edge references Core and its own packages only");
        }
    }

    [Fact]
    public void EachNewProjectReferencesCoreAndItsOwnPackagesOnly()
    {
        var source = Source;
        foreach (var name in NewEdges)
        {
            var project = Assert.Single(source, p => p.Name == name);
            Assert.Equal(["PlayMap.Core"], project.Projects);
            foreach (var (package, version) in project.Packages)
            {
                // Its own: no other project of the tree references the package, so an edge's native bindings stay its own.
                var others = source.Where(p => p.Name != name && p.Packages.ContainsKey(package)).Select(p => p.Name).ToList();
                Assert.True(others.Count == 0, $"{name}'s {package} is also referenced by {string.Join(", ", others)}");
                // Pinned: an exact released version, never a range, a float or a prerelease.
                Assert.Matches(@"^\d+(\.\d+){1,3}$", version);
            }
        }
        var backend = source.Single(p => p.Name == "PlayMap.Render.D3D12");
        Assert.Contains("TerraFX.Interop.Windows", backend.Packages.Keys);
    }

    [Fact]
    public void TheDirect3D12BackendNeverReferencesTheGlBackend()
    {
        var backend = Source.Single(p => p.Name == "PlayMap.Render.D3D12");
        Assert.DoesNotContain("PlayMap.Render", backend.Projects);
        Assert.DoesNotContain(backend.Packages.Keys, k => k.StartsWith("Silk.NET", StringComparison.Ordinal) || k.StartsWith("SkiaSharp", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheDeskReferencesAvalonia()
    {
        var projects = Source.Concat(Under("tools")).ToList();
        Assert.Contains(projects, p => p.Name == "PlayMap.App" && p.Packages.Keys.Any(k => k.StartsWith("Avalonia", StringComparison.Ordinal)));
        foreach (var p in projects.Where(p => p.Name != "PlayMap.App"))
        {
            Assert.DoesNotContain(p.Packages.Keys, k => k.StartsWith("Avalonia", StringComparison.Ordinal));
            Assert.DoesNotContain("PlayMap.App", p.Projects);
        }
    }

    [Fact]
    public void TheSolutionBuildsEveryProjectAndNoInstallerProject()
    {
        var sln = File.ReadAllText(System.IO.Path.Combine(Root, "PlayMap.sln"));
        foreach (var p in Source.Concat(Under("tests")).Concat(Under("tools")))
        {
            var relative = System.IO.Path.GetRelativePath(Root, p.Path).Replace(System.IO.Path.DirectorySeparatorChar, '\\');
            Assert.True(sln.Contains($"\"{relative}\"", StringComparison.Ordinal), $"PlayMap.sln does not build {relative}");
        }
        Assert.DoesNotContain(".wixproj", sln, StringComparison.OrdinalIgnoreCase);
    }
}
