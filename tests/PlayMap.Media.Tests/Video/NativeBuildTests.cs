using PlayMap.Tests;

namespace PlayMap.Media.Tests.Video;

/// <summary>A native double's build ends with its compiler, never with the compiler's pipes: a process the compiler starts (a linker's
/// helper on Windows) inherits its output pipes and may hold them open long after the library is made; a compiler that does not
/// finish is stopped with its words, and one that fails says its exit code and what it wrote. The builds run from the doubles'
/// attributes while the suite is discovered, where a wait that does not end stalls the whole test host before any test starts.</summary>
public sealed class NativeBuildTests
{
    /// <summary>Far longer than a run takes once its compiler has exited; the helper holds the pipes until the test lets it go, so a
    /// run that waits for the pipes cannot end within it however the machine is paced.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ABuildEndsWithItsCompilerWhileAProcessItStartedHoldsItsPipes()
    {
        await using var holder = new PipeHolder();
        var output = Path.Combine(holder.Folder, "made.bin");
        var (compiler, args) = holder.Tool(output, "built");
        var run = Task.Run(() => NativeBuild.Run(compiler, args, output, "the test's output"));
        var ended = await Task.WhenAny(run, Task.Delay(Patience));
        Assert.True(ReferenceEquals(ended, run), "the build was still waiting for the end of the compiler's pipes");
        Assert.Equal("", await run);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void ACompilerThatDoesNotFinishIsStoppedWithTheWords()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-nativebuild-");
        var output = Path.Combine(dir.FullName, "never.bin");
        try
        {
            var (compiler, args) = OperatingSystem.IsWindows()
                ? ("ping.exe", new[] { "-n", "600", "127.0.0.1" })
                : ("/bin/sh", new[] { "-c", "sleep 600" });
            var words = NativeBuild.Run(compiler, args, output, "the test's output", TimeSpan.FromSeconds(1));
            Assert.Equal($"{compiler} did not finish the test's output within 1 s and was stopped", words);
            Assert.False(File.Exists(output));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ACompilerThatFailsSaysItsExitCodeAndItsWords()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-nativebuild-");
        var output = Path.Combine(dir.FullName, "never.bin");
        try
        {
            string compiler;
            string[] args;
            if (OperatingSystem.IsWindows())
            {
                compiler = Path.Combine(dir.FullName, "fail.cmd");
                File.WriteAllText(compiler, "@echo off\r\n>&2 echo no such header\r\nexit /b 3\r\n");
                args = [];
            }
            else
            {
                compiler = "/bin/sh";
                args = ["-c", "echo no such header >&2; exit 3"];
            }
            var words = NativeBuild.Run(compiler, args, output, "the test's output");
            Assert.StartsWith($"{compiler} could not build the test's output (exit code 3): no such header", words, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
