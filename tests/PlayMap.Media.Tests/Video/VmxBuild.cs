using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PlayMap.Media.Video.Omt;

namespace PlayMap.Media.Tests.Video;

/// <summary>libvmx, OMT's native codec, built here from the vendored source (src/ThirdParty/libvmx) with the upstream command
/// (clang++, the x64 or arm64 flags of build/), once per source version into a folder of the temp directory, and handed to OMT through
/// PLAYMAP_VMX_DIR. Built only where clang++ is (Linux and macOS test machines); elsewhere the words say why.</summary>
internal static class VmxBuild
{
    private static readonly Lazy<(string? Dir, string Why)> Built = new(BuildOnce);

    /// <summary>The folder holding the built library, or null.</summary>
    public static string? Dir => Built.Value.Dir;

    /// <summary>Why it could not be built ("" when it was).</summary>
    public static string Why => Built.Value.Why;

    /// <summary>Builds (once) and points OMT at it; true when it is in place.</summary>
    public static bool Ensure()
    {
        if (Dir is not { } dir)
            return false;
        Environment.SetEnvironmentVariable(OmtSetup.VmxDirVariable, dir);
        return true;
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root (PlayMap.sln) is not above the test's folder");
    }

    private static (string? Dir, string Why) BuildOnce()
    {
        if (OperatingSystem.IsWindows())
            return (null, "libvmx is built for these tests with clang++ on Linux and macOS; on Windows the node uses a prebuilt libvmx.dll");
        var src = Path.Combine(RepoRoot(), "src", "ThirdParty", "libvmx", "src");
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        string[] files = arm ? ["vmxcodec_arm.cpp", "vmxcodec.cpp"] : ["vmxcodec_x86.cpp", "vmxcodec_avx2.cpp", "vmxcodec.cpp"];
        if (!arm && !System.Runtime.Intrinsics.X86.Avx2.IsSupported)
            return (null, "this CPU has no AVX2, which the upstream x64 build of libvmx needs");
        using var sha = SHA256.Create();
        foreach (var f in Directory.GetFiles(src).Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(f);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        var dir = Path.Combine(Path.GetTempPath(), "playmap-vmx-" + Convert.ToHexStringLower(sha.Hash!)[..12]);
        var lib = Path.Combine(dir, OmtSetup.VmxFileName);
        if (File.Exists(lib))
            return (dir, "");
        Directory.CreateDirectory(dir);
        var partial = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"building-{Environment.ProcessId}-{OmtSetup.VmxFileName}"));
        var args = new List<string> { "-O3", "-std=c++17", "-fdeclspec", "-fPIC", "-shared" };
        if (!arm)
            args.AddRange(["-mlzcnt", "-mavx2", "-mbmi"]);
        if (OperatingSystem.IsMacOS())
            args.Add("-dynamiclib");
        args.AddRange(files.Select(f => Path.Combine(src, f)));
        args.AddRange(["-o", partial]);
        var start = new ProcessStartInfo("clang++") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(start)!;
            var err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(partial))
                return (null, "clang++ could not build libvmx: " + err);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (null, "clang++ is not on this machine, so libvmx cannot be built here: " + ex.Message);
        }
        // Another test process may have finished the same build: either file is the same library.
        File.Move(partial, lib, overwrite: true);
        return (dir, "");
    }
}

/// <summary>A test that needs OMT's codec: skipped, with the reason, only where libvmx cannot be built.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class OmtFactAttribute : FactAttribute
{
    public OmtFactAttribute()
    {
        if (!VmxBuild.Ensure())
            Skip = VmxBuild.Why;
    }
}

/// <summary>A theory that needs OMT's codec.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class OmtTheoryAttribute : TheoryAttribute
{
    public OmtTheoryAttribute()
    {
        if (!VmxBuild.Ensure())
            Skip = VmxBuild.Why;
    }
}
