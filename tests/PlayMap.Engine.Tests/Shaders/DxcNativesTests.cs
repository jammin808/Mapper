using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Tests;

namespace PlayMap.Engine.Tests.Shaders;

/// <summary>The shader worker's DXC is the open-source compiler alone (round 3 §9 decision 24): dxcompiler, whose built-in validator
/// validates DXIL and writes its hash, and never DXC's separate validator library (dxil.dll, libdxil.so). The pins name dxcompiler on
/// each platform at the build the decision names, and no validator library. After a build, no output — the engine's, the desk's, the
/// node's or this suite's — holds a validator library or any file of Microsoft.Direct3D.DXC but its x64 dxcompiler.dll, and each holds
/// the pinned dxcompiler beside its assemblies. Nothing the desk's publish lays down can carry a validator library: no package the
/// desk restores holds one, no project, build or installer file of the tree names one, and the engine takes DXC's package at the
/// version the Direct3D 12 backend's build uses. The validator hash written here from the published algorithm is the reference
/// implementation's on both of its paths.</summary>
public sealed partial class DxcNativesTests
{
    /// <summary>DXC's separate validator library, on Windows and on Linux.</summary>
    private static readonly string[] Validators = ["dxil.dll", "libdxil.so"];

    private const string DxcPackage = "Microsoft.Direct3D.DXC";

    private static readonly string Root = NodeExe.RepoRoot();

    private static bool IsValidator(string path) => Validators.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ThePinsNameDxcompilerAtTheDecidedBuildAndNoValidatorLibrary()
    {
        var windows = CompilerNatives.PinsFor("win-x64");
        var linux = CompilerNatives.PinsFor("linux-x64");
        var w = Assert.Single(windows, p => p.Name == CompilerNatives.Dxc);
        Assert.Equal(("dxcompiler.dll", "0689e2fee0a1c9df8662a59680469f24e2a3ad7ce6bbbcc9284f7a83172bfc59"), (w.File, w.Sha256));
        var l = Assert.Single(linux, p => p.Name == CompilerNatives.Dxc);
        Assert.Equal(("libdxcompiler.so", "d40e4a7b3b46490313f4bfd08101946483a673831b3ea65c93d746de1c2f9216"), (l.File, l.Sha256));
        Assert.DoesNotContain(windows.Concat(linux), p => p.File.Contains("dxil", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("dxil", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(CompilerNatives.AllFiles, IsValidator);
        Assert.Contains("dxcompiler.dll", CompilerNatives.AllFiles);
        Assert.Contains("libdxcompiler.so", CompilerNatives.AllFiles);
    }

    [Fact]
    public void NoOutputHoldsAValidatorLibraryOrAnotherFileOfDxcsPackageAndEachHoldsThePinnedDxcompiler()
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tfm.Parent!.Name;
        var package = PackageFolder();
        // The package's binaries (its executables, libraries, symbols and import libraries): none reaches an output but the x64
        // dxcompiler.dll, on Windows.
        var x64 = Path.Combine(package, "build", "native", "bin", "x64", "dxcompiler.dll");
        var packaged = new[] { "bin", "lib" }.SelectMany(d => Directory.EnumerateFiles(Path.Combine(package, "build", "native", d), "*", SearchOption.AllDirectories))
            .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("dxil.dll", packaged);
        Assert.Contains("dxcompiler.dll", packaged);
        var pin = Assert.Single(CompilerNatives.Pins, p => p.Name == CompilerNatives.Dxc);
        var outputs = new (string Name, string Folder)[]
        {
            ("engine", Path.Combine(Root, "src", "PlayMap.Engine", "bin", configuration, tfm.Name)),
            ("desk", Path.Combine(Root, "src", "PlayMap.App", "bin", configuration, tfm.Name)),
            ("node", Path.Combine(Root, "tools", "PlayMap.Node", "bin", configuration, tfm.Name)),
            ("engine suite", AppContext.BaseDirectory),
        };
        var problems = new List<string>();
        foreach (var (name, folder) in outputs)
        {
            if (!Directory.Exists(folder))
            {
                problems.Add($"the {name}'s output {folder} is not there: the solution's build makes it");
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (IsValidator(file))
                    problems.Add($"the {name}'s output holds {Path.GetRelativePath(folder, file)}, DXC's validator library");
                else if (packaged.Contains(Path.GetFileName(file)) && !(OperatingSystem.IsWindows() && Path.GetFileName(file) == "dxcompiler.dll"
                    && Path.GetDirectoryName(file) == folder.TrimEnd(Path.DirectorySeparatorChar)
                    && CompilerNatives.HashOf(file) == CompilerNatives.HashOf(x64)))
                    problems.Add($"the {name}'s output holds {Path.GetRelativePath(folder, file)}, a file of {DxcPackage} the engine does not take");
            }
            var dxc = Path.Combine(folder, pin.File);
            if (!File.Exists(dxc))
                problems.Add($"the {name}'s output holds no {pin.File} beside its assemblies (it comes from {pin.From})");
            else if (CompilerNatives.HashOf(dxc) != pin.Sha256)
                problems.Add($"the {name}'s {pin.File} is not the pinned build: its SHA-256 is {CompilerNatives.HashOf(dxc)}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void NothingTheDesksPublishLaysDownCanCarryAValidatorLibrary()
    {
        // The packages the desk restores (its project.assets.json), the engine's among them: none holds a validator library, so no
        // runtime or native asset of theirs can lay one down beside the published exe.
        var assets = Path.Combine(Root, "src", "PlayMap.App", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"the desk's restore writes {assets}");
        using (var doc = JsonDocument.Parse(File.ReadAllText(assets)))
        {
            foreach (var library in doc.RootElement.GetProperty("libraries").EnumerateObject())
            {
                Assert.False(library.Name.StartsWith("Vortice.Dxc", StringComparison.OrdinalIgnoreCase), $"the desk restores {library.Name}");
                if (library.Value.TryGetProperty("files", out var files))
                    Assert.DoesNotContain(files.EnumerateArray().Select(f => f.GetString() ?? ""), IsValidator);
            }
        }

        // No project, build, publish or installer file of the tree names one (its comments aside): nothing copies one by name.
        var named = new List<string>();
        foreach (var file in BuildFiles())
        {
            var text = XmlComment().Replace(File.ReadAllText(file), "");
            if (Validators.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase)) || text.Contains("Vortice.Dxc", StringComparison.OrdinalIgnoreCase))
                named.Add(Path.GetRelativePath(Root, file));
        }
        Assert.True(named.Count == 0, "these name DXC's validator library or Vortice's DXC: " + string.Join(", ", named));

        // The engine takes DXC's package at the version the Direct3D 12 backend's build uses, downloaded for its one file and never
        // referenced, so none of its assets flows to the engine or past it; the backend's own reference stays build-time only.
        var engine = XDocument.Load(Path.Combine(Root, "src", "PlayMap.Engine", "PlayMap.Engine.csproj"));
        var backend = XDocument.Load(Path.Combine(Root, "src", "PlayMap.Render.D3D12", "PlayMap.Render.D3D12.csproj"));
        Assert.DoesNotContain(engine.Descendants("PackageReference"), e => (string?)e.Attribute("Include") == DxcPackage);
        var download = Assert.Single(engine.Descendants("PackageDownload"), e => (string?)e.Attribute("Include") == DxcPackage);
        var reference = Assert.Single(backend.Descendants("PackageReference"), e => (string?)e.Attribute("Include") == DxcPackage);
        Assert.Equal($"[{(string?)reference.Attribute("Version")}]", (string?)download.Attribute("Version"));
        Assert.Equal(("all", "all"), ((string?)reference.Attribute("ExcludeAssets"), (string?)reference.Attribute("PrivateAssets")));
    }

    /// <summary>The validator hash written here (tests/Shared/DxilHash.cs) is the reference implementation's (INF-0004 Appendix 1,
    /// as the research note's script computes it) on messages whose tail leaves room for the length in its last block (fewer than 56
    /// bytes past a whole block) and on those that need a block more.</summary>
    [Theory]
    [InlineData(0, "140d60f6b775e2ba4e4abed401b2e9a1")]
    [InlineData(1, "06f94cb281d1981af4f9a91b370ae308")]
    [InlineData(55, "9ab63592763f16cbf9faadc10871b3dc")]
    [InlineData(56, "9631e575809c7fec27e1a79c6dbf0704")]
    [InlineData(63, "69c87038086b06ae036a445580d52800")]
    [InlineData(64, "702b2563302d9691eda44cda5fef4b68")]
    [InlineData(100, "3c434ad9bed672b53f54445717861ab4")]
    [InlineData(119, "953cfe1c495908c5129cd5a6ae089ecd")]
    [InlineData(120, "71f04dacbba086d5aef734027044308c")]
    [InlineData(128, "1b561d90a4731383e7c8206bd3e08d58")]
    [InlineData(200, "5b549f280f46bcd28c57cf76172e3ba5")]
    public void TheRetailHashIsTheReferenceImplementations(int length, string hash)
    {
        var data = Enumerable.Range(0, length).Select(i => (byte)((i * 7 + 3) & 0xff)).ToArray();
        Assert.Equal(hash, Convert.ToHexStringLower(DxilHash.Retail(data)));
    }

    /// <summary>Microsoft.Direct3D.DXC in the NuGet cache, at the version the Direct3D 12 backend's build restores.</summary>
    private static string PackageFolder()
    {
        var assets = Path.Combine(Root, "src", "PlayMap.Render.D3D12", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"the Direct3D 12 backend's restore writes {assets}");
        using var doc = JsonDocument.Parse(File.ReadAllText(assets));
        var root = doc.RootElement;
        var library = root.GetProperty("libraries").EnumerateObject().Single(p => p.Name.StartsWith(DxcPackage + "/", StringComparison.OrdinalIgnoreCase));
        var folder = Path.Combine(root.GetProperty("project").GetProperty("restore").GetProperty("packagesPath").GetString()!,
            library.Value.GetProperty("path").GetString()!);
        Assert.True(Directory.Exists(folder), $"{library.Name} is in the NuGet cache at {folder}");
        return folder;
    }

    /// <summary>Every project, build, publish and installer file of the tree, build outputs and the Companion module's packages aside.</summary>
    private static IEnumerable<string> BuildFiles()
    {
        string[] patterns = ["*.csproj", "*.props", "*.targets", "*.pubxml", "*.iss", "*.wxs"];
        var sep = Path.DirectorySeparatorChar;
        return patterns.SelectMany(p => Directory.EnumerateFiles(Root, p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                && !f.Contains($"{sep}node_modules{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}.git{sep}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();
}
