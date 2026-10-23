using System.Diagnostics;
using System.Runtime.Versioning;
using PlayMap.Supervisor.Windows;
using PlayMap.Tests;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>The programs running from an install's folder, as the upgrade and the uninstall look for them before anything is stopped:
/// a program running from the folder is named, and the command's own callers (the uninstaller that waits in the folder for its copy
/// that runs the command) are not.</summary>
[SupportedOSPlatform("windows")]
public sealed class RunningFromInstallTests
{
    [WindowsFact]
    public void AProgramRunningFromTheFolderIsNamedAndTheCommandsOwnCallersAreNot()
    {
        // A folder whose path has no short-name form, as Program Files' has none: the image Windows names is this path.
        var dir = Path.Combine(AppContext.BaseDirectory, "running-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, "ping-copy.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), copy);
        var start = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-n", "120", "127.0.0.1" })
            start.ArgumentList.Add(a);
        using var ping = Process.Start(start)!;
        try
        {
            var found = Assert.Single(WindowsMachine.RunningUnder(dir, exceptTheEngine: true, new HashSet<int>()));
            Assert.Equal(ping.Id, found.Pid);
            Assert.Equal(copy, found.Image, ignoreCase: true);
            // The same process as one of the command's callers is left out.
            Assert.Empty(WindowsMachine.RunningUnder(dir, exceptTheEngine: true, new HashSet<int> { ping.Id }));
        }
        finally
        {
            ChildProcess.Stop(ping);
            Directory.Delete(dir, true);
        }
    }

    [WindowsFact]
    public void ThisProcesssCallersAreItsParentsChain()
    {
        var callers = WindowsMachine.CallersOfThisProcess();
        Assert.Contains(EngineProcess.ReadFacts(Environment.ProcessId).ParentPid, callers);
        Assert.DoesNotContain(Environment.ProcessId, callers);
    }
}
