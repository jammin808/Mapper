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

    /// <summary>The probe runs on a thread of its own, so the patience counts the probe alone and never a wait for a pool worker.</summary>
    [Fact]
    public async Task AProbeEndsWithItsToolWhileAProcessItStartedHoldsItsOutput()
    {
        await using var holder = new PipeHolder();
        var (tool, args) = holder.Tool(Path.Combine(holder.Folder, "made"), "the-global-root");
        var answer = OwnThread.Run("probe, still waiting for the end of the tool's output,", Patience,
            () => ToolProbe.Run(tool, args, ToolProbe.Deadline));
        // What the tool said before it exited is behind the helper's hold of the pipe, so the probe says it has no answer.
        Assert.Null(answer);
    }

    [Fact]
    public void AProbeReadsWhatItsToolSaid()
    {
        var (tool, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/d", "/c", "echo the-global-root" })
            : ("/bin/sh", new[] { "-c", "echo the-global-root" });
        Assert.Equal("the-global-root", ToolProbe.Run(tool, args, ToolProbe.Deadline));
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
