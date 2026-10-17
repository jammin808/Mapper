using System.Diagnostics;

namespace PlayMap.Media.Tests.Video;

/// <summary>The C and C++ compilers the native test doubles are built with: <c>cc</c> and <c>clang++</c> on Linux and macOS; on Windows
/// the LLVM clang the runner carries (beside Visual Studio's linker and the Windows SDK), named by <c>PLAYMAP_CLANG</c> /
/// <c>PLAYMAP_CLANGXX</c> when set (the CI job sets both), else <c>clang</c> / <c>clang++</c> on the path. Where
/// <c>PLAYMAP_NATIVE_REQUIRED</c> is 1 (the CI jobs), a double that cannot be built fails its tests with the words instead of
/// skipping them.</summary>
internal static class NativeBuild
{
    public const string RequiredVariable = "PLAYMAP_NATIVE_REQUIRED";

    /// <summary>A double that cannot be built fails its tests rather than skipping them.</summary>
    public static bool Required => Environment.GetEnvironmentVariable(RequiredVariable) == "1";

    /// <summary>The C compiler.</summary>
    public static string C => OperatingSystem.IsWindows() ? Named("PLAYMAP_CLANG", "clang") : "cc";

    /// <summary>The C++ compiler.</summary>
    public static string Cxx => OperatingSystem.IsWindows() ? Named("PLAYMAP_CLANGXX", "clang++") : "clang++";

    private static string Named(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } named ? named : fallback;

    /// <summary>Runs a compiler: "" when it made <paramref name="output"/>, else why not.</summary>
    public static string Run(string compiler, IEnumerable<string> args, string output, string what)
    {
        var start = new ProcessStartInfo(compiler) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(start)!;
            var err = p.StandardError.ReadToEndAsync();
            var outText = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(output))
                return $"{compiler} could not build {what}: {err.Result}{outText}";
            return "";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"{compiler} is not on this machine, so {what} cannot be built here: {ex.Message}";
        }
    }
}
