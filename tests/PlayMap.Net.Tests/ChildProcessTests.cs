using System.Diagnostics;
using System.Text;
using PlayMap.Tests;

namespace PlayMap.Net.Tests;

/// <summary>The tests' one way to run a child process (<see cref="ChildProcess"/>) ends with the process itself, never with the end of
/// its output pipes, which a process it starts may hold open long after it has exited; a tool past its deadline is stopped and says
/// so; what a tool writes, on either stream, and its exit code are read, and its standard input can be written.</summary>
public sealed class ChildProcessTests
{
    /// <summary>Far longer than a run takes once its tool has exited; the helper holds the pipes until the test lets it go, so a run
    /// that waits for the pipes cannot end within it however the machine is paced.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ARunEndsWithItsToolWhileAProcessItStartedHoldsItsPipes()
    {
        await using var holder = new PipeHolder();
        var (tool, args) = holder.Tool(Path.Combine(holder.Folder, "made"), "said");
        var run = Task.Run(() => ChildProcess.Run(tool, args, ChildProcess.Deadline));
        Assert.True(ReferenceEquals(await Task.WhenAny(run, Task.Delay(Patience)), run), "the run was still waiting for the end of the tool's pipes");
        var ran = await run;
        Assert.Equal((true, true, 0, false), (ran.Started, ran.Finished, ran.ExitCode, ran.WordsIn));
        Assert.False(ran.Ok);
        Assert.True(File.Exists(Path.Combine(holder.Folder, "made")));
    }

    [Fact]
    public async Task AnAwaitedRunEndsWithItsToolWhileAProcessItStartedHoldsItsPipes()
    {
        await using var holder = new PipeHolder();
        var (tool, args) = holder.Tool(Path.Combine(holder.Folder, "made"), "said");
        var run = ChildProcess.RunAsync(tool, args, ChildProcess.Deadline);
        Assert.True(ReferenceEquals(await Task.WhenAny(run, Task.Delay(Patience)), run), "the run was still waiting for the end of the tool's pipes");
        var ran = await run;
        Assert.Equal((true, true, 0, false), (ran.Started, ran.Finished, ran.ExitCode, ran.WordsIn));
        Assert.EndsWith("its words were held open by a process it started", ran.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProcessTheTestStartedEndsWithItselfWhileAProcessItStartedHoldsItsPipes()
    {
        await using var holder = new PipeHolder();
        var (tool, args) = holder.Tool(Path.Combine(holder.Folder, "made"), "said");
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var said = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        var end = ChildProcess.EndAsync(p, ChildProcess.Deadline, said, errors);
        Assert.True(ReferenceEquals(await Task.WhenAny(end, Task.Delay(Patience)), end), "the end was still waiting for the end of the process's pipes");
        Assert.True(await end);
        Assert.Equal(0, p.ExitCode);
        Assert.False(said.IsCompleted);
    }

    [Fact]
    public async Task AToolPastItsDeadlineIsStoppedAndSaysSoWithWhatItWrote()
    {
        var (tool, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/d", "/c", "echo waiting 1>&2 & ping -n 600 127.0.0.1 >nul" })
            : ("/bin/sh", new[] { "-c", "echo waiting >&2; sleep 600" });
        var ran = ChildProcess.Run(tool, args, TimeSpan.FromSeconds(3));
        Assert.Equal((true, false, false), (ran.Started, ran.Finished, ran.Ok));
        Assert.Equal($"{tool} did not finish within 3 s and was stopped: waiting", ran.Words.TrimEnd());
        var awaited = await ChildProcess.RunAsync(tool, args, TimeSpan.FromSeconds(3));
        Assert.Equal((true, false, false), (awaited.Started, awaited.Finished, awaited.Ok));
        Assert.Equal($"{tool} did not finish within 3 s and was stopped: waiting", awaited.Words.TrimEnd());
    }

    /// <summary>A tool past its deadline is stopped with every process it started: here its helper holds the tool's pipes for ten
    /// minutes, so the tool's words are all in within the drain only once the stop has taken the helper too.</summary>
    [Fact]
    public async Task AToolPastItsDeadlineIsStoppedWithEveryProcessItStarted()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-childprocess-");
        try
        {
            string tool;
            string[] args;
            if (OperatingSystem.IsWindows())
            {
                tool = Path.Combine(dir.FullName, "tool.cmd");
                await File.WriteAllTextAsync(tool,
                    "@echo off\r\nstart \"\" /b cmd /d /c \"ping -n 600 127.0.0.1 >nul\"\r\necho said\r\nping -n 600 127.0.0.1 >nul\r\n");
                args = [];
            }
            else
            {
                tool = "/bin/sh";
                args = ["-c", "sleep 600 & echo said; sleep 600"];
            }
            // The tool says its word once its helper runs, so a run that read it was stopped with the helper started.
            var ran = ChildProcess.Run(tool, args, TimeSpan.FromSeconds(3));
            Assert.Equal((true, false, true), (ran.Started, ran.Finished, ran.WordsIn));
            Assert.Equal("said", ran.Text.Trim());
            var awaited = await ChildProcess.RunAsync(tool, args, TimeSpan.FromSeconds(3));
            Assert.Equal((true, false, true), (awaited.Started, awaited.Finished, awaited.WordsIn));
            Assert.Equal("said", awaited.Text.Trim());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AToolsInputOutputErrorsAndExitCodeAreItsOwn()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-childprocess-");
        try
        {
            string tool;
            string[] args;
            if (OperatingSystem.IsWindows())
            {
                tool = Path.Combine(dir.FullName, "echo.cmd");
                await File.WriteAllTextAsync(tool, "@echo off\r\nfindstr \"^\"\r\necho no such header 1>&2\r\nexit /b 3\r\n");
                args = [];
            }
            else
            {
                tool = "/bin/sh";
                args = ["-c", "cat; echo no such header >&2; exit 3"];
            }
            var ran = await ChildProcess.RunAsync(tool, args, ChildProcess.Deadline, input: s => s.WriteAsync(Encoding.UTF8.GetBytes("a picture's bytes\n")).AsTask());
            Assert.Equal((true, true, 3, true, false), (ran.Started, ran.Finished, ran.ExitCode, ran.WordsIn, ran.Ok));
            Assert.Equal("a picture's bytes", ran.Text.Trim());
            Assert.Equal("no such header", ran.Errors.Trim());
            Assert.StartsWith($"{tool} (exit code 3): no such header", ran.Words, StringComparison.Ordinal);
            var missing = ChildProcess.Run(Path.Combine(dir.FullName, "no-such-tool"), [], ChildProcess.Deadline);
            Assert.Equal((false, false), (missing.Started, missing.Ok));
            Assert.StartsWith($"{Path.Combine(dir.FullName, "no-such-tool")} could not be started: ", missing.Words, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
