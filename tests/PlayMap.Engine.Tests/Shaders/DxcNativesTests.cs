using System.Text;
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
/// node's, this suite's, and the desk's publish once one is made — holds a validator library or any file of Microsoft.Direct3D.DXC
/// (its binaries by name, every other file of it by content, LICENSE-MS.txt among them) but its x64 dxcompiler.dll beside the
/// assemblies and its LICENSE-LLVM.txt in licenses/DXC/; each holds the pinned dxcompiler beside its assemblies, and wherever an
/// output holds dxcompiler, licenses/DXC/ beside it holds the text its licence asks to travel with it: the package's
/// LICENSE-LLVM.txt (the copyright notice, the three conditions and the disclaimer) and DXC's LICENSE.TXT and ThirdPartyNotices.txt
/// at tag v1.9.2609 (the licences of the third-party code inside it). Nothing the desk's publish lays down can carry a validator
/// library: no package the desk restores holds one, no project, build, publish, installer, workflow or script file of the tree names
/// one, and the engine takes DXC's package at the version the Direct3D 12 backend's build uses. The validator hash written here from
/// the published algorithm is the reference implementation's on both of its paths.</summary>
public sealed partial class DxcNativesTests
{
    /// <summary>DXC's separate validator library, on Windows and on Linux.</summary>
    private static readonly string[] Validators = ["dxil.dll", "libdxil.so"];

    private const string DxcPackage = "Microsoft.Direct3D.DXC";

    private static readonly string Root = NodeExe.RepoRoot();

    /// <summary>Where DXC's licence and notices sit, relative to the folder of the dxcompiler they cover.</summary>
    private static readonly string Notices = Path.Combine("licenses", "DXC");

    /// <summary>DXC's own licence files at tag v1.9.2609 (https://github.com/microsoft/DirectXShaderCompiler/tree/v1.9.2609), as the
    /// engine carries them (src/PlayMap.Engine/ShaderWorker/DxcNotices), by SHA-256.</summary>
    private static readonly (string File, string Sha256)[] RepositoryNotices =
    [
        ("LICENSE.TXT", "27a49e35d1da96eba18fba54bc882667ff0ff8c0254f16f2b6e165d605ba7df8"),
        ("ThirdPartyNotices.txt", "19512a5d0a015ef16d167272c164da49a604614a786206212a80ad486ed0be6d"),
    ];

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
        var package = PackageFolder();
        var x64 = Path.Combine(package, "build", "native", "bin", "x64", "dxcompiler.dll");
        var licence = Path.Combine(package, "LICENSE-LLVM.txt");
        // The package's binaries (its executables, libraries, symbols and import libraries), by name.
        var binaries = new[] { "bin", "lib" }.SelectMany(d => Directory.EnumerateFiles(Path.Combine(package, "build", "native", d), "*", SearchOption.AllDirectories))
            .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("dxil.dll", binaries);
        Assert.Contains("dxcompiler.dll", binaries);
        // Every file of the package but NuGet's own, by content: its binaries, licences (LICENSE-MS.txt among them), release notes,
        // headers and MSBuild files.
        var contents = new PackageContents(package);
        Assert.True(contents.Holds(Path.Combine(package, "LICENSE-MS.txt")), "the package's LICENSE-MS.txt is among the files compared by content");
        var pin = Assert.Single(CompilerNatives.Pins, p => p.Name == CompilerNatives.Dxc);
        var problems = new List<string>();
        foreach (var (name, folder) in Outputs())
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
                else if ((binaries.Contains(Path.GetFileName(file)) || contents.Holds(file)) && !TakenDxcompiler(file, x64) && !TakenLicence(file, licence))
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
    public void WhereverAnOutputHoldsDxcompilerItsLicenceAndNoticesSitBesideIt()
    {
        // The package's licence is the University of Illinois/NCSA licence: its copyright notice, its three conditions — the second
        // asking a binary to carry them and the disclaimer — and its disclaimer.
        var licence = File.ReadAllBytes(Path.Combine(PackageFolder(), "LICENSE-LLVM.txt"));
        var text = Words(Encoding.UTF8.GetString(licence));
        Assert.Contains("Copyright (c) 2003-2015 University of Illinois at Urbana-Champaign. All rights reserved.", text, StringComparison.Ordinal);
        Assert.Contains("Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following "
            + "disclaimers in the documentation and/or other materials provided with the distribution.", text, StringComparison.Ordinal);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND", text, StringComparison.Ordinal);

        var pin = Assert.Single(CompilerNatives.Pins, p => p.Name == CompilerNatives.Dxc);
        var outputs = Outputs();
        var problems = new List<string>();
        var held = 0;
        foreach (var (name, folder) in outputs)
        {
            if (!Directory.Exists(folder))
            {
                problems.Add($"the {name}'s output {folder} is not there: the solution's build makes it");
                continue;
            }
            foreach (var dxc in Directory.EnumerateFiles(folder, pin.File, SearchOption.AllDirectories))
            {
                held++;
                var beside = Path.Combine(Path.GetDirectoryName(dxc)!, Notices);
                var what = $"the {name}'s output holds {Path.GetRelativePath(folder, dxc)}";
                var llvm = Path.Combine(beside, "LICENSE-LLVM.txt");
                if (!File.Exists(llvm))
                    problems.Add($"{what} with no {Path.GetRelativePath(folder, llvm)} beside it");
                else if (!File.ReadAllBytes(llvm).AsSpan().SequenceEqual(licence))
                    problems.Add($"{what} with a {Path.GetRelativePath(folder, llvm)} that is not {DxcPackage}'s LICENSE-LLVM.txt");
                foreach (var (file, sha256) in RepositoryNotices)
                {
                    var path = Path.Combine(beside, file);
                    if (!File.Exists(path))
                        problems.Add($"{what} with no {Path.GetRelativePath(folder, path)} beside it");
                    else if (CompilerNatives.HashOf(path) != sha256)
                        problems.Add($"{what} with a {Path.GetRelativePath(folder, path)} that is not DXC v1.9.2609's: its SHA-256 is {CompilerNatives.HashOf(path)}");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(held >= outputs.Count, $"{held} dxcompiler files in {outputs.Count} outputs");
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

        // No project, build, publish, installer, workflow or script file of the tree names one (its comments aside, each in its own
        // format's words): nothing copies one by name.
        var build = BuildFiles().ToList();
        Assert.Contains(build, f => Path.GetFileName(f) == "build.yml");
        Assert.Contains(build, f => Path.GetExtension(f) == ".ps1");
        Assert.Contains(build, f => Path.GetExtension(f) == ".sh");
        var named = new List<string>();
        foreach (var file in build)
        {
            var text = WithoutComments(file);
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

    /// <summary>The outputs a build makes — the engine's, the desk's, the node's and this suite's, in this suite's configuration — and,
    /// once a publish has made it, the desk's published folder (publish/PlayMap, the Windows job's portable exe).</summary>
    private static List<(string Name, string Folder)> Outputs()
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tfm.Parent!.Name;
        var outputs = new List<(string Name, string Folder)>
        {
            ("engine", Path.Combine(Root, "src", "PlayMap.Engine", "bin", configuration, tfm.Name)),
            ("desk", Path.Combine(Root, "src", "PlayMap.App", "bin", configuration, tfm.Name)),
            ("node", Path.Combine(Root, "tools", "PlayMap.Node", "bin", configuration, tfm.Name)),
            ("engine suite", tfm.FullName),
        };
        var published = Path.Combine(Root, "publish", "PlayMap");
        if (Directory.Exists(published))
            outputs.Add(("desk's publish", published));
        return outputs;
    }

    /// <summary>A folder beside .NET assemblies: one that holds an application's or a library's dependency file.</summary>
    private static bool BesideAssemblies(string? folder) =>
        folder is not null && Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.deps.json").Any();

    /// <summary>The one binary of DXC's package the engine takes: the x64 dxcompiler.dll, on Windows, beside the assemblies.</summary>
    private static bool TakenDxcompiler(string file, string x64) =>
        OperatingSystem.IsWindows() && Path.GetFileName(file) == "dxcompiler.dll" && BesideAssemblies(Path.GetDirectoryName(file))
        && CompilerNatives.HashOf(file) == CompilerNatives.HashOf(x64);

    /// <summary>The package's LICENSE-LLVM.txt, which the engine lays down in licenses/DXC/ beside the dxcompiler it covers.</summary>
    private static bool TakenLicence(string file, string licence)
    {
        var folder = Path.GetDirectoryName(file)!;
        var owner = Path.GetDirectoryName(Path.GetDirectoryName(folder));
        return Path.GetFileName(file) == "LICENSE-LLVM.txt"
            && string.Equals(Path.GetRelativePath(owner ?? folder, folder), Notices, StringComparison.Ordinal)
            && BesideAssemblies(owner) && File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(licence));
    }

    /// <summary>Every file of a package but NuGet's own (the package, its hash, signature, manifest and metadata), found by content:
    /// a file whose length and SHA-256 are one of them.</summary>
    private sealed class PackageContents
    {
        private readonly ILookup<long, string> _byLength;
        private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);

        public PackageContents(string package)
        {
            string[] nuget = [".nupkg", ".sha512", ".p7s", ".nuspec", ".metadata"];
            _byLength = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories)
                .Where(f => !nuget.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToLookup(f => new FileInfo(f).Length);
        }

        public bool Holds(string file)
        {
            var length = new FileInfo(file).Length;
            if (!_byLength.Contains(length))
                return false;
            var hash = CompilerNatives.HashOf(file);
            return _byLength[length].Any(p => Hash(p) == hash);
        }

        private string Hash(string path)
        {
            if (!_hashes.TryGetValue(path, out var hash))
                _hashes[path] = hash = CompilerNatives.HashOf(path);
            return hash;
        }
    }

    /// <summary>A text with every run of white space one space.</summary>
    private static string Words(string text) => Space().Replace(text, " ").Trim();

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

    /// <summary>Every project, build, publish and installer file of the tree — MSBuild's, Inno Setup's and WiX's — with CI's workflows
    /// and every script (PowerShell and shell), build outputs, a publish and the Companion module's packages aside.</summary>
    private static IEnumerable<string> BuildFiles()
    {
        string[] patterns = ["*.csproj", "*.props", "*.targets", "*.pubxml", "*.iss", "*.wxs", "*.yml", "*.yaml", "*.ps1", "*.psm1", "*.sh"];
        var sep = Path.DirectorySeparatorChar;
        string[] aside = ["bin", "obj", "publish", "node_modules", ".git"];
        return patterns.SelectMany(p => Directory.EnumerateFiles(Root, p, SearchOption.AllDirectories))
            .Where(f => !aside.Any(a => f.Contains($"{sep}{a}{sep}", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>A build file's text without its comments, in its own format's words: XML's &lt;!-- --&gt;; Inno Setup's lines that start
    /// with a semicolon; PowerShell's &lt;# #&gt; blocks; and from a # that starts a line or follows white space to the line's end in
    /// PowerShell, shell scripts and YAML.</summary>
    private static string WithoutComments(string file)
    {
        var text = File.ReadAllText(file);
        return Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".iss" => IssComment().Replace(text, ""),
            ".ps1" or ".psm1" => HashComment().Replace(PowerShellBlock().Replace(text, ""), ""),
            ".sh" or ".yml" or ".yaml" => HashComment().Replace(text, ""),
            _ => XmlComment().Replace(text, ""),
        };
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    [GeneratedRegex("^[ \\t]*;.*$", RegexOptions.Multiline)]
    private static partial Regex IssComment();

    [GeneratedRegex("<#.*?#>", RegexOptions.Singleline)]
    private static partial Regex PowerShellBlock();

    [GeneratedRegex("(^|[ \\t])#.*$", RegexOptions.Multiline)]
    private static partial Regex HashComment();

    [GeneratedRegex("\\s+")]
    private static partial Regex Space();
}
