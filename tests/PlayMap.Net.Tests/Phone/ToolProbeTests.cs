namespace PlayMap.Net.Tests.Phone;

/// <summary>The probe of a tool that <see cref="NodeFactAttribute"/> here and the Engine suite's browser facts run while a suite is
/// discovered (npm's global root, <see cref="ToolProbe"/>) ends with the tool, never with its output pipe: a process the tool starts
/// inherits the pipe and may hold it open long after the tool has said what it was asked, and a wait for the pipe's end stalls the
/// whole test host before any test starts.</summary>
public sealed class ToolProbeTests
{
    /// <summary>Far longer than a probe takes once its tool has exited; the helper holds the pipe until the test lets it go, so a probe
    /// that waits for the pipe cannot end within it however the machine is paced.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task AProbeEndsWithItsToolWhileAProcessItStartedHoldsItsOutput()
    {
        await using var holder = new PipeHolder();
        var (tool, args) = holder.Tool(Path.Combine(holder.Folder, "made"), "the-global-root");
        var probe = Task.Run(() => ToolProbe.Run(tool, args, ToolProbe.Deadline));
        var ended = await Task.WhenAny(probe, Task.Delay(Patience));
        Assert.True(ReferenceEquals(ended, probe), "the probe was still waiting for the end of the tool's output");
        // What the tool said before it exited is behind the helper's hold of the pipe, so the probe says it has no answer.
        Assert.Null(await probe);
    }

    [Fact]
    public void AProbeReadsWhatItsToolSaid()
    {
        var (tool, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/d", "/c", "echo the-global-root" })
            : ("/bin/sh", new[] { "-c", "echo the-global-root" });
        Assert.Equal("the-global-root", ToolProbe.Run(tool, args, ToolProbe.Deadline));
    }

    /// <summary>The probes' tools are found by their absolute path: on Windows a tool's .exe, else its .cmd (npm is a batch file there),
    /// folder by folder; elsewhere its bare name; never in a PATH entry that is not an absolute folder.</summary>
    [Fact]
    public void AToolIsFoundByItsAbsolutePathAsItsExeThenItsCmdOnWindowsAndByItsNameElsewhere()
    {
        var first = Directory.CreateTempSubdirectory("playmap-find-");
        var second = Directory.CreateTempSubdirectory("playmap-find-");
        try
        {
            foreach (var (dir, file) in new[] { (first, "npm.cmd"), (first, "node.cmd"), (second, "node.exe"), (second, "npm"), (second, "tool.cmd"), (first, "tool.exe") })
                File.WriteAllText(Path.Combine(dir.FullName, file), "");
            var path = string.Join(Path.PathSeparator, "relative-folder", first.FullName, second.FullName);

            Assert.Equal(Path.Combine(first.FullName, "npm.cmd"), ToolProbe.Find("npm", path, windows: true));
            Assert.Equal(Path.Combine(first.FullName, "node.cmd"), ToolProbe.Find("node", path, windows: true));
            Assert.Equal(Path.Combine(first.FullName, "tool.exe"), ToolProbe.Find("tool", path, windows: true));
            Assert.Equal(Path.Combine(second.FullName, "npm"), ToolProbe.Find("npm", path, windows: false));
            Assert.Null(ToolProbe.Find("node", path, windows: false));
            Assert.Null(ToolProbe.Find("npm", "relative-folder", windows: true));
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Fact]
    public void AToolThatDoesNotFinishIsStoppedAndAnswersNothing()
    {
        var (tool, args) = OperatingSystem.IsWindows()
            ? ("ping.exe", new[] { "-n", "600", "127.0.0.1" })
            : ("/bin/sh", new[] { "-c", "sleep 600" });
        Assert.Null(ToolProbe.Run(tool, args, TimeSpan.FromSeconds(1)));
    }
}
