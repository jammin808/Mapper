using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PlayMap.Engine.ShaderWorker;

namespace PlayMap.Installer.Tests;

/// <summary>What ships, held to its licences (docs/round3/PLAN.md §5.6 item 4, its lines on DXC and on the notices), read from the
/// folder PLAYMAP_SHIPPED_DIR names: in the installer job the installer's payload, every file the installer lays down; in the
/// Windows job the portable folder, publish\PlayMap once its notices are laid down. The folder holds DXC's separate validator
/// library (dxil.dll, libdxil.so) nowhere, in any case of its name, and no file of Microsoft.Direct3D.DXC — its binaries and its
/// root licence files by name, every other file of it by content — but its x64 dxcompiler.dll beside the assemblies, the worker's
/// pin, and its LICENSE-LLVM.txt in licenses\DXC\, which holds that file and DXC's own LICENSE.TXT and ThirdPartyNotices.txt as the
/// engine carries them, and nothing else (§9 decision 24). It carries THIRD-PARTY-NOTICES.md as the repository holds it, which names
/// every package of the folder's PlayMap.deps.json with files in it (by its id, or a family as <c>Prefix.*</c>) and, by its file
/// name, every native library no library of PlayMap.deps.json lists, the shader worker's among them, each worker native beside the
/// assemblies at its pin; and each such package's own licence and notice files lie under licenses\&lt;package id&gt;\ as the restore
/// holds them, the rule installer/notices.ps1 lays them down by. Both rules are proven on folders made for the purpose, on any
/// platform.</summary>
public sealed partial class LicenceTests
{
    private const string DxcPackage = "Microsoft.Direct3D.DXC";

    /// <summary>DXC's separate validator library, on Windows and on Linux.</summary>
    private static readonly string[] Validators = ["dxil.dll", "libdxil.so"];

    /// <summary>What licenses\DXC\ holds, in ordinal order, and nothing else: the package's licence, then DXC's own licence and
    /// third-party notices.</summary>
    private static readonly string[] DxcNotices = ["LICENSE-LLVM.txt", "LICENSE.TXT", "ThirdPartyNotices.txt"];

    /// <summary>DXC's own licence files at tag v1.9.2609, which the engine carries in src/PlayMap.Engine/ShaderWorker/DxcNotices.</summary>
    private static readonly string[] EngineNotices = ["LICENSE.TXT", "ThirdPartyNotices.txt"];

    /// <summary>The kinds of a deps.json entry that lay files in the folder: managed, native and satellite assemblies.</summary>
    private static readonly string[] Kinds = ["runtime", "native", "resources"];

    /// <summary>The worker's DXC on Windows: dxcompiler.dll at its pin.</summary>
    private static readonly NativePin Compiler = CompilerNatives.PinsFor("win-x64").Single(p => p.Name == CompilerNatives.Dxc);

    private static string Shipped => Environment.GetEnvironmentVariable("PLAYMAP_SHIPPED_DIR") is { Length: > 0 } d
        ? d
        : throw new InvalidOperationException("PLAYMAP_SHIPPED_DIR is not set: the installer job names the installer's payload, the Windows job the portable folder");

    /// <summary>The repository the tests are built from: the folder above them that holds PlayMap.sln.</summary>
    private static string Repository
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>The restore's package folder, as this project's build recorded it.</summary>
    private static string PackageRoot =>
        typeof(LicenceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "NuGetPackageRoot").Value is { Length: > 0 } root
            ? root
            : throw new InvalidOperationException("the build recorded no NuGetPackageRoot");

    /// <summary>The packs of the .NET install these tests run on, where a runtime pack the SDK carries is read from.</summary>
    private static string Packs => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", "packs"));

    /// <summary>Where the restore holds a package, as installer/notices.ps1 looks for it: the NuGet cache, then the SDK's packs;
    /// null when neither holds it.</summary>
    private static string? Restored(string id, string version) =>
        new[] { Path.Combine(PackageRoot, id.ToLowerInvariant(), version), Path.Combine(Packs, id, version) }.FirstOrDefault(Directory.Exists);

    /// <summary>Microsoft.Direct3D.DXC in the NuGet cache, at the version the engine downloads (its PackageDownload).</summary>
    private static string DxcPackageFolder()
    {
        var engine = XDocument.Load(Path.Combine(Repository, "src", "PlayMap.Engine", "PlayMap.Engine.csproj"));
        var download = engine.Descendants("PackageDownload").Single(e => (string?)e.Attribute("Include") == DxcPackage);
        var version = ((string?)download.Attribute("Version") ?? "").Trim('[', ']');
        var folder = Path.Combine(PackageRoot, DxcPackage.ToLowerInvariant(), version);
        Assert.True(Directory.Exists(folder), $"{DxcPackage} {version} is not in the NuGet cache at {folder}: the engine's restore downloads it");
        return folder;
    }

    [Fact]
    public void TheShippedFolderHoldsNoValidatorLibraryAndOfDxcItsCompilerAloneWithItsLicenceAndNotices()
    {
        var folder = Shipped;
        Assert.True(File.Exists(Path.Combine(folder, "PlayMap.deps.json")), $"{folder} holds no PlayMap.deps.json: PLAYMAP_SHIPPED_DIR names the app's publish");
        var problems = DxcProblems(folder, DxcPackageFolder());
        Assert.True(problems.Count == 0, Describe(folder, problems));
    }

    [Fact]
    public void TheShippedFolderCarriesTheNoticesNamingEveryPackageAndEveryOtherNativeInIt()
    {
        var folder = Shipped;
        var found = NoticeProblems(folder, File.ReadAllBytes(Path.Combine(Repository, "THIRD-PARTY-NOTICES.md")), Restored);
        Assert.True(found.Problems.Count == 0, Describe(folder, found.Problems));
        // The rule saw the natives that come from the engine's build and from no package: DXC's compiler and SPIRV-Tools'. Each native
        // the worker loads lies beside the assemblies at its pin.
        Assert.Contains("dxcompiler.dll", found.Natives);
        Assert.Contains("SPIRV-Tools-shared.dll", found.Natives);
        foreach (var pin in CompilerNatives.PinsFor("win-x64"))
        {
            var path = Path.Combine(folder, pin.File);
            Assert.True(File.Exists(path), $"{folder} carries no {pin.File} beside its assemblies (it comes from {pin.From})");
            var hash = CompilerNatives.HashOf(path);
            Assert.True(hash == pin.Sha256, $"{folder}'s {pin.File} is not the worker's pin of {pin.From}: its SHA-256 is {hash}");
        }
    }

    [Fact]
    public void TheNoticesNameEachNativeTheShaderWorkerLoadsOnWindows()
    {
        var notices = File.ReadAllText(Path.Combine(Repository, "THIRD-PARTY-NOTICES.md"));
        var files = CompilerNatives.PinsFor("win-x64").Select(p => p.File).ToList();
        Assert.Contains("dxcompiler.dll", files);
        Assert.Contains("SPIRV-Tools-shared.dll", files);
        Assert.All(files, file => Assert.Contains('`' + file + '`', notices, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDxcRuleFindsAValidatorLibraryOrAnotherFileOfDxcsPackageAnywhereInAFolder()
    {
        var package = DxcPackageFolder();
        var x64 = Path.Combine(package, "build", "native", "bin", "x64");
        using (var clean = new Folder())
        {
            LayDxc(clean.Path, package);
            var problems = DxcProblems(clean.Path, package);
            Assert.True(problems.Count == 0, Describe(clean.Path, problems));
        }

        // Each change to a folder laid down as the engine's build lays DXC down, and the words the rule finds it by.
        var changes = new (string Words, Action<string> Change)[]
        {
            ("carries sub/DXIL.dll, DXC's validator library", f => Write(f, "sub/DXIL.dll", "a library")),
            ("carries libdxil.so, DXC's validator library", f => Write(f, "libdxil.so", "a library")),
            ("carries tools/compiler.bin, a file of Microsoft.Direct3D.DXC", f => CopyInto(f, "tools/compiler.bin", Path.Combine(x64, "dxil.dll"))),
            ("carries dxc.exe, a file of Microsoft.Direct3D.DXC", f => Write(f, "dxc.exe", "a program")),
            ("carries LICENSE-MS.txt, a file of Microsoft.Direct3D.DXC", f => Write(f, "LICENSE-MS.txt", "a licence")),
            ("carries notes/ReleaseNotes.md, a file of Microsoft.Direct3D.DXC", f => CopyInto(f, "notes/ReleaseNotes.md", Path.Combine(package, "ReleaseNotes.md"))),
            ("carries sub/dxcompiler.dll, a file of Microsoft.Direct3D.DXC", f => CopyInto(f, "sub/dxcompiler.dll", Path.Combine(x64, "dxcompiler.dll"))),
            ("carries dxcompiler.dll, which is not the worker's pin", f => Write(f, "dxcompiler.dll", "another build")),
            ("carries no dxcompiler.dll beside its assemblies", f => File.Delete(Path.Combine(f, "dxcompiler.dll"))),
            ("carries licenses/DXC/LICENSE-LLVM.txt, which is not", f => Write(f, "licenses/DXC/LICENSE-LLVM.txt", "another licence")),
            ("carries licenses/DXC/LICENSE.TXT, which is not", f => Write(f, "licenses/DXC/LICENSE.TXT", "another licence")),
            ("carries licenses/DXC/ holding LICENSE-LLVM.txt, LICENSE-MS.txt, LICENSE.TXT, ThirdPartyNotices.txt", f => CopyInto(f, "licenses/DXC/LICENSE-MS.txt", Path.Combine(package, "LICENSE-MS.txt"))),
            ("carries licenses/DXC/ holding LICENSE-LLVM.txt, LICENSE.TXT:", f => File.Delete(Path.Combine(f, "licenses", "DXC", "ThirdPartyNotices.txt"))),
        };
        foreach (var (words, change) in changes)
        {
            using var folder = new Folder();
            LayDxc(folder.Path, package);
            change(folder.Path);
            var problems = DxcProblems(folder.Path, package);
            Assert.True(problems.Exists(p => p.Contains(words, StringComparison.Ordinal)), $"\"{words}\" is not among what the rule found: {Describe(folder.Path, problems)}");
        }
    }

    [Fact]
    public void TheNoticeRuleFindsWhatTheNoticesDoNotNameAndNoticesNotBesideTheirFiles()
    {
        // The restore: a package in the NuGet cache's shape with two notices and a file that is neither, a runtime pack in the SDK's.
        using var restore = new Folder();
        Write(restore.Path, "cache/acme.widgets/2.0.0/LICENSE.txt", "Acme's licence");
        Write(restore.Path, "cache/acme.widgets/2.0.0/NOTICE.md", "Acme's notice");
        Write(restore.Path, "cache/acme.widgets/2.0.0/README.md", "Acme's readme");
        Write(restore.Path, "packs/Contoso.Runtime.win-x64/1.0.0/THIRD-PARTY-NOTICES.TXT", "Contoso's notices");
        string? Restore(string id, string version) =>
            new[] { Path.Combine(restore.Path, "cache", id.ToLowerInvariant(), version), Path.Combine(restore.Path, "packs", id, version) }.FirstOrDefault(Directory.Exists);

        // The publish: PlayMap's own files, a package's managed, native and satellite files, a package the restore does not hold, a
        // runtime pack's files, and a native library no library lists.
        using var folder = new Folder();
        Write(folder.Path, "PlayMap.deps.json", Deps);
        foreach (var file in new[] { "PlayMap.dll", "Acme.Widgets.dll", "acme_native.dll", "de/Acme.Widgets.resources.dll", "Missing.Pkg.dll", "System.Private.CoreLib.dll", "coreclr.dll", "sub/extra.dll" })
            Write(folder.Path, file, file);
        Write(folder.Path, "THIRD-PARTY-NOTICES.md", "an older file");

        var none = Encoding.UTF8.GetBytes("# Third-party notices\n");
        var unnamed = NoticeProblems(folder.Path, none, Restore);
        Assert.Equal(["sub/extra.dll"], unnamed.Natives);
        AssertFound(unnamed.Problems,
            "THIRD-PARTY-NOTICES.md is not the repository's",
            "carries Acme.Widgets 2.0.0, which THIRD-PARTY-NOTICES.md does not name",
            "carries Missing.Pkg 1.0.0, which THIRD-PARTY-NOTICES.md does not name",
            "carries Contoso.Runtime.win-x64 1.0.0, which THIRD-PARTY-NOTICES.md does not name",
            "carries sub/extra.dll, which no library of PlayMap.deps.json lists and THIRD-PARTY-NOTICES.md does not name");

        // Named, by a family, by an id and by a file name; the packages' notices not yet beside the files.
        var named = Encoding.UTF8.GetBytes("Acme's widgets (`Acme.*`), `Missing.Pkg`, Contoso's runtime (`Contoso.Runtime.win-x64`) and `extra.dll`.\n");
        File.WriteAllBytes(Path.Combine(folder.Path, "THIRD-PARTY-NOTICES.md"), named);
        AssertFound(NoticeProblems(folder.Path, named, Restore).Problems,
            "carries Acme.Widgets 2.0.0 with no licenses/Acme.Widgets/LICENSE.txt",
            "carries Acme.Widgets 2.0.0 with no licenses/Acme.Widgets/NOTICE.md",
            "carries Missing.Pkg 1.0.0, which the restore does not hold",
            "carries Contoso.Runtime.win-x64 1.0.0 with no licenses/Contoso.Runtime.win-x64/THIRD-PARTY-NOTICES.TXT");

        // Laid down as notices.ps1 lays them, one of them another text; then all of them, with the last package restored.
        CopyInto(folder.Path, "licenses/Acme.Widgets/LICENSE.txt", Path.Combine(restore.Path, "cache", "acme.widgets", "2.0.0", "LICENSE.txt"));
        Write(folder.Path, "licenses/Acme.Widgets/NOTICE.md", "another notice");
        CopyInto(folder.Path, "licenses/Contoso.Runtime.win-x64/THIRD-PARTY-NOTICES.TXT", Path.Combine(restore.Path, "packs", "Contoso.Runtime.win-x64", "1.0.0", "THIRD-PARTY-NOTICES.TXT"));
        Directory.CreateDirectory(Path.Combine(restore.Path, "cache", "missing.pkg", "1.0.0"));
        AssertFound(NoticeProblems(folder.Path, named, Restore).Problems, "carries licenses/Acme.Widgets/NOTICE.md, which is not Acme.Widgets 2.0.0's");
        CopyInto(folder.Path, "licenses/Acme.Widgets/NOTICE.md", Path.Combine(restore.Path, "cache", "acme.widgets", "2.0.0", "NOTICE.md"));
        var laid = NoticeProblems(folder.Path, named, Restore);
        Assert.True(laid.Problems.Count == 0, Describe(folder.Path, laid.Problems));
        Assert.Equal(["sub/extra.dll"], laid.Natives);
    }

    /// <summary>A publish's PlayMap.deps.json as a self-contained win-x64 publish writes it, with one of each kind of library.</summary>
    private const string Deps = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/win-x64", "signature": "" },
          "targets": {
            ".NETCoreApp,Version=v10.0/win-x64": {
              "PlayMap/1.0.0": { "dependencies": { "Acme.Widgets": "2.0.0" }, "runtime": { "PlayMap.dll": {} } },
              "Acme.Widgets/2.0.0": {
                "runtime": { "lib/net8.0/Acme.Widgets.dll": { "assemblyVersion": "2.0.0.0" } },
                "native": { "runtimes/win-x64/native/acme_native.dll": { "fileVersion": "0.0.0.0" } },
                "resources": { "lib/net8.0/de/Acme.Widgets.resources.dll": { "locale": "de" } }
              },
              "Empty.Pkg/1.0.0": {},
              "Missing.Pkg/1.0.0": { "runtime": { "lib/net8.0/Missing.Pkg.dll": {} } },
              "runtimepack.Contoso.Runtime.win-x64/1.0.0": { "runtime": { "System.Private.CoreLib.dll": {} }, "native": { "coreclr.dll": {} } }
            }
          },
          "libraries": {
            "PlayMap/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "Acme.Widgets/2.0.0": { "type": "package", "serviceable": true, "sha512": "", "path": "acme.widgets/2.0.0" },
            "Empty.Pkg/1.0.0": { "type": "package", "serviceable": true, "sha512": "", "path": "empty.pkg/1.0.0" },
            "Missing.Pkg/1.0.0": { "type": "package", "serviceable": true, "sha512": "", "path": "missing.pkg/1.0.0" },
            "runtimepack.Contoso.Runtime.win-x64/1.0.0": { "type": "runtimepack", "serviceable": false, "sha512": "" }
          }
        }
        """;

    /// <summary>What a folder holds against §9 decision 24, in words, none when it holds what it should: DXC's validator library
    /// anywhere; a file of DXC's package (its binaries and its root licence files but LICENSE-LLVM.txt by name, every other file of it
    /// by content) but its compiler beside the assemblies and its licence in licenses\DXC\; a compiler that is not the worker's pin,
    /// or none; a licence or notice in licenses\DXC\ that is not the package's or the engine's; and licenses\DXC\ holding anything
    /// but its three files.</summary>
    private static List<string> DxcProblems(string folder, string package)
    {
        var problems = new List<string>();
        var x64 = Path.Combine(package, "build", "native", "bin", "x64", Compiler.File);
        if (CompilerNatives.HashOf(x64) != Compiler.Sha256)
            problems.Add($"{DxcPackage}'s x64 {Compiler.File} in the restore is not the worker's pin");
        var licence = File.ReadAllBytes(Path.Combine(package, "LICENSE-LLVM.txt"));
        var byName = new[] { "bin", "lib" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(package, "build", "native", d), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(package, "LICEN*").Where(f => Path.GetFileName(f) != "LICENSE-LLVM.txt"))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contents = new PackageContents(package);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Relative(folder, file);
            var name = Path.GetFileName(file);
            if (Validators.Contains(name, StringComparer.OrdinalIgnoreCase))
                problems.Add($"carries {relative}, DXC's validator library");
            else if (string.Equals(relative, Compiler.File, StringComparison.OrdinalIgnoreCase))
            {
                var hash = CompilerNatives.HashOf(file);
                if (hash != Compiler.Sha256)
                    problems.Add($"carries {relative}, which is not the worker's pin of {Compiler.From}: its SHA-256 is {hash}");
            }
            else if (string.Equals(relative, "licenses/DXC/LICENSE-LLVM.txt", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(licence))
                    problems.Add($"carries {relative}, which is not {DxcPackage}'s LICENSE-LLVM.txt");
            }
            else if (byName.Contains(name) || contents.Holds(file))
                problems.Add($"carries {relative}, a file of {DxcPackage} the engine does not take");
        }
        if (!File.Exists(Path.Combine(folder, Compiler.File)))
            problems.Add($"carries no {Compiler.File} beside its assemblies (the worker's DXC, from {Compiler.From})");
        var notices = Path.Combine(folder, "licenses", "DXC");
        List<string> held = Directory.Exists(notices)
            ? Directory.EnumerateFiles(notices, "*", SearchOption.AllDirectories).Select(f => Relative(notices, f)).Order(StringComparer.Ordinal).ToList()
            : [];
        if (!held.SequenceEqual(DxcNotices, StringComparer.Ordinal))
            problems.Add($"carries licenses/DXC/ holding {(held.Count == 0 ? "nothing" : string.Join(", ", held))}: it holds {string.Join(", ", DxcNotices)} and nothing else");
        foreach (var file in EngineNotices)
        {
            var path = Path.Combine(notices, file);
            if (File.Exists(path) && !File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Repository, "src", "PlayMap.Engine", "ShaderWorker", "DxcNotices", file))))
                problems.Add($"carries licenses/DXC/{file}, which is not DXC's as the engine carries it (src/PlayMap.Engine/ShaderWorker/DxcNotices)");
        }
        return problems;
    }

    /// <summary>What the notices rule finds in a folder: its problems in words, none when it holds what it should, and the native
    /// libraries no library of its PlayMap.deps.json lists, by their paths in it.</summary>
    private sealed record NoticeFindings(List<string> Problems, List<string> Natives);

    /// <summary>A folder's notices against <paramref name="notices"/> (THIRD-PARTY-NOTICES.md as the repository holds it): the folder's
    /// copy is the same bytes; every package and runtime pack of its PlayMap.deps.json with files in it is named, the restore holds
    /// it, and its own licence and notice files lie under licenses\&lt;package id&gt;\ byte for byte; and every native library in the
    /// folder that no library of PlayMap.deps.json lists is named by its file name.</summary>
    private static NoticeFindings NoticeProblems(string folder, byte[] notices, Func<string, string, string?> restored)
    {
        var problems = new List<string>();
        var text = Encoding.UTF8.GetString(notices);
        var copy = Path.Combine(folder, "THIRD-PARTY-NOTICES.md");
        if (!File.Exists(copy))
            problems.Add("carries no THIRD-PARTY-NOTICES.md");
        else if (!File.ReadAllBytes(copy).AsSpan().SequenceEqual(notices))
            problems.Add("THIRD-PARTY-NOTICES.md is not the repository's");
        var deps = Path.Combine(folder, "PlayMap.deps.json");
        if (!File.Exists(deps))
        {
            problems.Add("carries no PlayMap.deps.json: the folder is not the app's publish");
            return new(problems, []);
        }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(deps));
        var root = doc.RootElement;
        var target = root.GetProperty("targets").GetProperty(root.GetProperty("runtimeTarget").GetProperty("name").GetString()!);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in target.EnumerateObject())
        {
            foreach (var kind in Kinds)
            {
                if (!entry.Value.TryGetProperty(kind, out var assets))
                    continue;
                foreach (var asset in assets.EnumerateObject())
                    listed.Add(Path.GetFileName(asset.Name));
            }
        }
        foreach (var library in root.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() is not ("package" or "runtimepack") || !target.TryGetProperty(library.Name, out var entry)
                || !Kinds.Any(k => entry.TryGetProperty(k, out var files) && files.EnumerateObject().Any()))
                continue;
            var slash = library.Name.IndexOf('/', StringComparison.Ordinal);
            var id = library.Name[..slash];
            if (id.StartsWith("runtimepack.", StringComparison.Ordinal))
                id = id["runtimepack.".Length..];
            var version = library.Name[(slash + 1)..];
            if (!Named(text, id))
            {
                problems.Add($"carries {id} {version}, which THIRD-PARTY-NOTICES.md does not name");
                continue;
            }
            if (restored(id, version) is not { } dir)
            {
                problems.Add($"carries {id} {version}, which the restore does not hold: its licence and notices cannot be compared");
                continue;
            }
            foreach (var own in Directory.EnumerateFiles(dir).Where(f => OwnNotice().IsMatch(Path.GetFileName(f))))
            {
                var name = Path.GetFileName(own);
                var laid = Path.Combine(folder, "licenses", id, name);
                if (!File.Exists(laid))
                    problems.Add($"carries {id} {version} with no licenses/{id}/{name}");
                else if (!File.ReadAllBytes(laid).AsSpan().SequenceEqual(File.ReadAllBytes(own)))
                    problems.Add($"carries licenses/{id}/{name}, which is not {id} {version}'s");
            }
        }
        var natives = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetExtension(f), ".dll", StringComparison.OrdinalIgnoreCase) && !listed.Contains(Path.GetFileName(f)))
            .Select(f => Relative(folder, f))
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var native in natives)
        {
            if (!text.Contains('`' + Path.GetFileName(native) + '`', StringComparison.Ordinal))
                problems.Add($"carries {native}, which no library of PlayMap.deps.json lists and THIRD-PARTY-NOTICES.md does not name");
        }
        return new(problems, natives);
    }

    /// <summary>THIRD-PARTY-NOTICES.md names a package by its id in backticks, or by a family of ids in backticks as <c>Prefix.*</c>:
    /// the rule installer/notices.ps1 holds the publish to.</summary>
    private static bool Named(string notices, string id)
    {
        if (notices.Contains('`' + id + '`', StringComparison.Ordinal))
            return true;
        var parts = id.Split('.');
        for (var n = parts.Length; n >= 1; n--)
        {
            if (notices.Contains('`' + string.Join('.', parts[..n]) + ".*`", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>A package's own licence and notice files, as installer/notices.ps1 finds them at the top of its folder.</summary>
    [GeneratedRegex(@"^(LICEN[CS]E|THIRD-PARTY-NOTICES|ThirdPartyNotices|NOTICE)(\.(txt|md))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnNotice();

    /// <summary>A folder as the engine's build lays DXC down beside a publish's assemblies: its compiler, and its licence and notices
    /// in licenses\DXC\.</summary>
    private static void LayDxc(string folder, string package)
    {
        Write(folder, "PlayMap.dll", "PlayMap");
        CopyInto(folder, Compiler.File, Path.Combine(package, "build", "native", "bin", "x64", Compiler.File));
        CopyInto(folder, "licenses/DXC/LICENSE-LLVM.txt", Path.Combine(package, "LICENSE-LLVM.txt"));
        foreach (var file in EngineNotices)
            CopyInto(folder, "licenses/DXC/" + file, Path.Combine(Repository, "src", "PlayMap.Engine", "ShaderWorker", "DxcNotices", file));
    }

    private static void Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void CopyInto(string folder, string relative, string from)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(from, path, true);
    }

    private static string Relative(string folder, string file) => Path.GetRelativePath(folder, file).Replace('\\', '/');

    private static void AssertFound(List<string> problems, params string[] expected)
    {
        Assert.True(problems.Count == expected.Length, $"{expected.Length} problems expected: {Describe("the folder", problems)}");
        foreach (var words in expected)
            Assert.True(problems.Exists(p => p.StartsWith(words, StringComparison.Ordinal)), $"\"{words}\" is not among what the rule found: {Describe("the folder", problems)}");
    }

    private static string Describe(string folder, List<string> problems) =>
        problems.Count == 0 ? folder + ": nothing" : folder + ":\n" + string.Join("\n", problems);

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

    /// <summary>A folder of this test's own, removed with everything in it.</summary>
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("playmap-licence-").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }
}
