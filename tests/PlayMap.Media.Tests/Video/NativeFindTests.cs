using System.Diagnostics;
using System.Runtime.InteropServices;
using PlayMap.Core.Time;
using PlayMap.Media.Video.Ndi;
using PlayMap.Media.Video.Omt;

namespace PlayMap.Media.Tests.Video;

/// <summary>A test's process-wide surroundings while it runs: the current directory and the named variables (cleared at the start),
/// put back as they were when it ends. Used only in collections that run alone.</summary>
internal sealed class Surroundings : IDisposable
{
    private readonly string _directory = Environment.CurrentDirectory;
    private readonly Dictionary<string, string?> _variables = new(StringComparer.Ordinal);

    public Surroundings(params string[] variables)
    {
        foreach (var name in variables)
        {
            _variables[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Set(string variable, string? value)
    {
        Assert.True(_variables.ContainsKey(variable), variable + " is not one this test puts back");
        Environment.SetEnvironmentVariable(variable, value);
    }

    public void At(string directory) => Environment.CurrentDirectory = directory;

    public void Dispose()
    {
        Environment.CurrentDirectory = _directory;
        foreach (var (name, value) in _variables)
            Environment.SetEnvironmentVariable(name, value);
    }

    public static string Folder(string what) => Directory.CreateTempSubdirectory("playmap-find-" + what + "-").FullName;

    /// <summary>A copy of a library put in a folder under a name, as a user (or anyone else) could put one there.</summary>
    public static string Plant(string library, string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, fileName);
        File.Copy(library, file, overwrite: true);
        return file;
    }

    /// <summary>The planted library was never loaded: no module of this process is it — the same look sees one this process did load,
    /// on Linux, where the paths read back are the ones loaded — and it can be deleted, which Windows refuses for a loaded library.</summary>
    public static void NeverLoaded(string planted, string loaded)
    {
        var mapped = Mapped();
        if (OperatingSystem.IsLinux())
            Assert.Contains(loaded, mapped);
        Assert.DoesNotContain(planted, mapped);
        File.Delete(planted);
        Assert.False(File.Exists(planted));
    }

    private static List<string> Mapped()
    {
        using var self = Process.GetCurrentProcess();
        var files = new List<string>();
        foreach (ProcessModule module in self.Modules)
        {
            using (module)
                files.Add(module.FileName);
        }
        return files;
    }
}

/// <summary>Where the NDI runtime is looked for (<see cref="NdiRuntime"/>), with fake files: the setting (a file, then a folder) wins
/// over <c>PLAYMAP_NDI_DIR</c>, that over <c>NDI_RUNTIME_DIR_V6</c>, that over the exe's folder and the standard folders; every
/// candidate is an absolute path, loaded by that full path, and the bare file name is never one; a relative setting or variable is
/// refused with the words, never read against the current directory; and a runtime present only in the current directory is not
/// loaded.</summary>
[Collection("ndi")]
public sealed class NdiFindTests : IDisposable
{
    private readonly Surroundings _around = new(NdiRuntime.DirVariable, NdiRuntime.RuntimeVariable);

    private static string Lib(string folder) => Path.Combine(folder, NdiRuntime.FileName);

    private static string Plant(string folder) => Surroundings.Plant(FakeNdi.Lib(FakeNdi.Folder63), folder, NdiRuntime.FileName);

    [Fact]
    public void EveryCandidateIsAnAbsolutePathInTheOrderTheyAreTried()
    {
        var setting = Surroundings.Folder("ndi-setting");
        var dir = Surroundings.Folder("ndi-dir");
        var runtime = Surroundings.Folder("ndi-runtime");
        _around.Set(NdiRuntime.DirVariable, dir);
        _around.Set(NdiRuntime.RuntimeVariable, runtime);
        var search = NdiRuntime.Search(setting);
        Assert.Empty(search.Refused);
        Assert.Equal(search.Files, NdiRuntime.Candidates(setting));
        Assert.All(search.Files, f => Assert.True(Path.IsPathFullyQualified(f), f + " is not an absolute path"));
        Assert.DoesNotContain(NdiRuntime.FileName, search.Files);
        Assert.Equal([Lib(setting), Lib(dir), Lib(runtime), Lib(AppContext.BaseDirectory)], search.Files.Take(4));
        // Then the standard folders, as absolute paths.
        string[] standard = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NDI", "NDI 6 Runtime", "v6", NdiRuntime.FileName)]
            : OperatingSystem.IsMacOS()
                ? ["/usr/local/lib/libndi.dylib", "/Library/NDI SDK for Apple/lib/macOS/libndi.dylib"]
                : [.. NativePaths.SystemFolders().Select(Lib)];
        Assert.Equal(standard, search.Files.Skip(4));
        if (OperatingSystem.IsLinux())
            Assert.Equal(["/usr/lib/libndi.so.6", "/usr/local/lib/libndi.so.6"], search.Files.Skip(4).Take(2));
        // A setting that names a file is that file.
        Assert.Equal(Lib(setting), NdiRuntime.Candidates(Lib(setting))[0]);
    }

    [NdiFact]
    public void TheSettingWinsOverTheVariablesAndPlayMapsVariableOverTheRuntimesOwn()
    {
        // A copy of the fake runtime in every place the user can name, each its own library.
        var file = Plant(Surroundings.Folder("ndi-file"));
        var folder = Surroundings.Folder("ndi-folder");
        var dir = Surroundings.Folder("ndi-dir");
        var runtime = Surroundings.Folder("ndi-runtime");
        foreach (var place in new[] { folder, dir, runtime })
            Plant(place);
        _around.Set(NdiRuntime.DirVariable, dir);
        _around.Set(NdiRuntime.RuntimeVariable, runtime);
        Assert.Equal(file, Loaded(file));
        Assert.Equal(Lib(folder), Loaded(folder));
        Assert.Equal(Lib(dir), Loaded(""));
        _around.Set(NdiRuntime.DirVariable, null);
        Assert.Equal(Lib(runtime), Loaded(""));
    }

    private static string Loaded(string configured)
    {
        var runtime = NdiRuntime.Load(configured, out var why);
        Assert.True(runtime is not null, why);
        Assert.Equal("", why);
        return runtime.Path;
    }

    [NdiFact]
    public void ARelativeSettingOrVariableIsRefusedWithTheWordsAndNeverReadAgainstTheCurrentDirectory()
    {
        // Where each relative value would land if it were read against the current directory, a runtime is waiting.
        var here = Surroundings.Folder("ndi-cwd");
        var planted = Plant(Path.Combine(here, "ndi"));
        _around.At(here);
        _around.Set(NdiRuntime.DirVariable, "ndi");
        _around.Set(NdiRuntime.RuntimeVariable, Path.Combine(".", "ndi"));
        var search = NdiRuntime.Search("ndi");
        Assert.Equal(
            [
                "videoio.ndiRuntimePath \"ndi\" is refused: it is not an absolute path, and a relative one would be read against the current directory",
                NdiRuntime.DirVariable + " \"ndi\" is refused: it is not an absolute path, and a relative one would be read against the current directory",
                NdiRuntime.RuntimeVariable + " \"" + Path.Combine(".", "ndi") + "\" is refused: it is not an absolute path, and a relative one would be read against the current directory",
            ],
            search.Refused);
        Assert.DoesNotContain(search.Files, f => f.StartsWith(here, StringComparison.Ordinal));
        var installed = search.Files.Any(File.Exists);

        // Not found where it was not told to look, with the refusals in the words STATE carries (videoio.ndi.words).
        using (var absent = new NdiProvider(new NdiOptions("ndi"), MonoClock.Instance))
        {
            if (!installed)
                Assert.Equal("absent", absent.Status.State);
            Assert.All(search.Refused, refusal => Assert.Contains(refusal, absent.Status.Words, StringComparison.Ordinal));
        }

        // A runtime where it is told to look (an absolute PLAYMAP_NDI_DIR) is ready, and the refused setting is still said.
        _around.Set(NdiRuntime.DirVariable, FakeNdi.Folder63);
        using (var ready = new NdiProvider(new NdiOptions("ndi"), MonoClock.Instance))
        {
            Assert.Equal("ready", ready.Status.State);
            Assert.Equal(Lib(FakeNdi.Folder63), ready.Status.Path);
            Assert.Contains(search.Refused[0], ready.Status.Words, StringComparison.Ordinal);
        }
        Surroundings.NeverLoaded(planted, Lib(FakeNdi.Folder63));
    }

    [NdiFact]
    public void ARuntimePresentOnlyInTheCurrentDirectoryIsNotLoaded()
    {
        var loaded = Loaded(FakeNdi.Folder63);
        var here = Surroundings.Folder("ndi-cwd");
        var planted = Plant(here);
        _around.At(here);
        var candidates = NdiRuntime.Candidates("");
        Assert.DoesNotContain(candidates, c => c.StartsWith(here, StringComparison.Ordinal));
        var runtime = NdiRuntime.Load("", out var why);
        if (!candidates.Any(File.Exists))
        {
            Assert.Null(runtime);
            Assert.StartsWith("the NDI runtime was not found (searched " + string.Join(", ", candidates) + ")", why, StringComparison.Ordinal);
        }
        Assert.NotEqual(planted, runtime?.Path);
        Surroundings.NeverLoaded(planted, loaded);
    }

    public void Dispose() => _around.Dispose();
}

/// <summary>Where OMT's codec, libvmx, is looked for (<see cref="OmtSetup"/>), with fake files: <c>PLAYMAP_VMX_DIR</c>, beside the
/// exe, the exe's <c>runtimes/&lt;rid&gt;/native</c>, then the system's library folders (Linux, macOS); every candidate is an
/// absolute path, loaded by that full path, and the bare file name is never one; a relative <c>PLAYMAP_VMX_DIR</c> is refused with the
/// words, never read against the current directory; a codec present only in the current directory is not loaded; and the resolver
/// never hands libvmx to the runtime's own probing.</summary>
[Collection("omt")]
public sealed class VmxFindTests : IDisposable
{
    private readonly Surroundings _around = new(OmtSetup.VmxDirVariable);

    private static string Lib(string folder) => Path.Combine(folder, OmtSetup.VmxFileName);

    private static string Plant(string folder) => Surroundings.Plant(Lib(VmxBuild.Dir!), folder, OmtSetup.VmxFileName);

    [Fact]
    public void EveryCandidateIsAnAbsolutePathInTheOrderTheyAreTried()
    {
        var dir = Surroundings.Folder("vmx-dir");
        _around.Set(OmtSetup.VmxDirVariable, dir);
        var search = OmtSetup.VmxSearch();
        Assert.Empty(search.Refused);
        Assert.All(search.Files, f => Assert.True(Path.IsPathFullyQualified(f), f + " is not an absolute path"));
        Assert.DoesNotContain(OmtSetup.VmxFileName, search.Files);
        var exe = AppContext.BaseDirectory;
        Assert.Equal(
            [Lib(dir), Lib(exe), Lib(Path.Combine(exe, "runtimes", RuntimeInformation.RuntimeIdentifier, "native")), .. NativePaths.SystemFolders().Select(Lib)],
            search.Files);
        if (OperatingSystem.IsWindows())
            Assert.Equal(3, search.Files.Count);
        if (OperatingSystem.IsLinux())
            Assert.Equal(["/usr/lib/libvmx.so", "/usr/local/lib/libvmx.so"], search.Files.Skip(3).Take(2));
    }

    [OmtFact]
    public void TheVariablesFolderLoadsAndARelativeOneIsRefusedWithTheWords()
    {
        var dir = Surroundings.Folder("vmx-dir");
        var copy = Plant(dir);
        _around.Set(OmtSetup.VmxDirVariable, dir);
        var handle = OmtSetup.LoadVmx(OmtSetup.VmxSearch(), out var from, out var why);
        Assert.NotEqual(0, handle);
        Assert.Equal(copy, from);
        Assert.Equal("", why);

        // Where the relative value would land if it were read against the current directory, a codec is waiting.
        var here = Surroundings.Folder("vmx-cwd");
        var planted = Plant(Path.Combine(here, "vmx"));
        _around.At(here);
        _around.Set(OmtSetup.VmxDirVariable, "vmx");
        var search = OmtSetup.VmxSearch();
        var refusal = OmtSetup.VmxDirVariable + " \"vmx\" is refused: it is not an absolute path, and a relative one would be read against the current directory";
        Assert.Equal([refusal], search.Refused);
        Assert.Equal(refusal, search.RefusedWords);
        Assert.DoesNotContain(search.Files, f => f.StartsWith(here, StringComparison.Ordinal));
        var none = OmtSetup.LoadVmx(search, out from, out why);
        if (!search.Files.Any(File.Exists))
        {
            Assert.Equal(0, none);
            Assert.Equal("", from);
            Assert.Contains(refusal, why, StringComparison.Ordinal);
            Assert.Contains(OmtSetup.VmxDirVariable + " to its folder, an absolute path", why, StringComparison.Ordinal);
        }
        Assert.NotEqual(planted, from);
        Surroundings.NeverLoaded(planted, copy);
        NativeLibrary.Free(handle);
    }

    [OmtFact]
    public void ACodecPresentOnlyInTheCurrentDirectoryIsNotLoaded()
    {
        var dir = Surroundings.Folder("vmx-dir");
        var copy = Plant(dir);
        _around.Set(OmtSetup.VmxDirVariable, dir);
        var loaded = OmtSetup.LoadVmx(OmtSetup.VmxSearch(), out _, out _);
        Assert.NotEqual(0, loaded);
        _around.Set(OmtSetup.VmxDirVariable, null);
        var here = Surroundings.Folder("vmx-cwd");
        var planted = Plant(here);
        _around.At(here);
        var search = OmtSetup.VmxSearch();
        Assert.DoesNotContain(search.Files, f => f.StartsWith(here, StringComparison.Ordinal));
        var handle = OmtSetup.LoadVmx(search, out var from, out var why);
        if (!search.Files.Any(File.Exists))
        {
            Assert.Equal(0, handle);
            Assert.StartsWith("libvmx (OMT's video codec) was not found: searched " + string.Join(", ", search.Files) + " — ", why, StringComparison.Ordinal);
        }
        Assert.NotEqual(planted, from);
        Surroundings.NeverLoaded(planted, copy);
        NativeLibrary.Free(loaded);
    }

    [Fact]
    public void TheResolverNeverHandsLibvmxToTheRuntimesOwnProbing()
    {
        // Zero would let the runtime search by name (on Windows the current directory and PATH): a codec that is not loaded is refused.
        var refused = Assert.Throws<DllNotFoundException>(() => OmtSetup.Resolved("libvmx", 0, "libvmx (OMT's video codec) was not found"));
        Assert.Equal("libvmx (OMT's video codec) was not found", refused.Message);
        Assert.Equal(42, OmtSetup.Resolved("libvmx", 42, ""));
        // OMT's other imports are the system's own (dnsapi, avahi, libc): left to the runtime.
        Assert.Equal(0, OmtSetup.Resolved("dnsapi.dll", 0, ""));
    }

    public void Dispose() => _around.Dispose();
}
