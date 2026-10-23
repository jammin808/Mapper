using System.Diagnostics;
using System.Globalization;

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

    /// <summary>The longest a compiler runs before it is stopped with every process it started: the slowest double (libvmx at -O3)
    /// builds in under a minute on the CI runners.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    /// <summary>How long a compiler's words are read after it has exited. A process it started (a linker's helper) inherits its output
    /// pipes and may hold them open long after, so the run ends with the compiler, never with the end of its pipes.</summary>
    public static readonly TimeSpan Drain = TimeSpan.FromSeconds(5);

    /// <summary>Runs a compiler: "" when it made <paramref name="output"/>, else why not.</summary>
    public static string Run(string compiler, IEnumerable<string> args, string output, string what) =>
        Run(compiler, args, output, what, Deadline);

    /// <summary>Runs a compiler that is stopped, with every process it started, once <paramref name="deadline"/> has passed.</summary>
    internal static string Run(string compiler, IEnumerable<string> args, string output, string what, TimeSpan deadline)
    {
        var start = new ProcessStartInfo(compiler) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        Process p;
        try
        {
            p = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"{compiler} is not on this machine, so {what} cannot be built here: {ex.Message}";
        }
        using (p)
        {
            var err = p.StandardError.ReadToEndAsync();
            var outText = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(deadline))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the wait and the kill.
                }
                return string.Create(CultureInfo.InvariantCulture,
                    $"{compiler} did not finish {what} within {deadline.TotalSeconds:0} s and was stopped");
            }
            if (p.ExitCode == 0 && File.Exists(output))
                return "";
            var words = Task.WhenAll(err, outText).Wait(Drain)
                ? err.Result + outText.Result
                : "(its words were held open by a process it started)";
            return string.Create(CultureInfo.InvariantCulture, $"{compiler} could not build {what} (exit code {p.ExitCode}): {words}");
        }
    }
}
