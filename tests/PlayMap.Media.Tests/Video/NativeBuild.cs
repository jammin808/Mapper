using System.Globalization;
using PlayMap.Tests;

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
    public static readonly TimeSpan Deadline = ChildProcess.Deadline;

    /// <summary>Runs a compiler: "" when it made <paramref name="output"/>, else why not. A process the compiler starts (a linker's
    /// helper) inherits its output pipes and may hold them open long after, so the run ends with the compiler, never with the end of
    /// its pipes, and reads its words for <see cref="ChildProcess.Drain"/> more.</summary>
    public static string Run(string compiler, IEnumerable<string> args, string output, string what) =>
        Run(compiler, args, output, what, Deadline);

    /// <summary>Runs a compiler that is stopped, with every process it started, once <paramref name="deadline"/> has passed.</summary>
    internal static string Run(string compiler, IEnumerable<string> args, string output, string what, TimeSpan deadline)
    {
        var ran = ChildProcess.Run(compiler, args, deadline);
        if (!ran.Started)
            return $"{compiler} is not on this machine, so {what} cannot be built here: {ran.Why}";
        if (!ran.Finished)
            return string.Create(CultureInfo.InvariantCulture, $"{compiler} did not finish {what} within {deadline.TotalSeconds:0} s and was stopped");
        if (ran.ExitCode == 0 && File.Exists(output))
            return "";
        var words = ran.WordsIn ? ran.Errors + ran.Text : "(its words were held open by a process it started)";
        return string.Create(CultureInfo.InvariantCulture, $"{compiler} could not build {what} (exit code {ran.ExitCode}): {words}");
    }
}
