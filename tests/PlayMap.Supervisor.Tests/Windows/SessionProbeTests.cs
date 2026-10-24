using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using PlayMap.Supervisor.Windows;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>The M9 probe lets its child run before it waits on it. The child it starts is suspended, as the supervisor starts the
/// engine; a wait on a suspended child would run out with no report, so the probe resumes the child first. Driven here with a child
/// started suspended by the test (the probe's own launcher needs LocalSystem's token, which the Windows job has only in the installer
/// job's scheduled task), it reports within the wait.</summary>
[SupportedOSPlatform("windows")]
public sealed class SessionProbeTests
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, char[] commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory,
        ref Native.StartupInfo startup, out Native.ProcessInformation information);

    [WindowsFact]
    public void TheProbeLetsTheSuspendedChildRunBeforeItWaitsSoTheChildReports()
    {
        var said = new List<string>();
        // A launcher that starts the child suspended (as SessionLauncher.Start does), writing the report path the probe asked for.
        EngineProcess Launch(uint session, string exe, string arguments)
        {
            var first = arguments.IndexOf('"', StringComparison.Ordinal);
            var last = arguments.LastIndexOf('"');
            var report = arguments[(first + 1)..last];
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var line = (cmd + " /c echo probe-ran 1>\"" + report + "\"\0").ToCharArray();
            var startup = new Native.StartupInfo { Cb = Marshal.SizeOf<Native.StartupInfo>() };
            if (!CreateProcess(cmd, line, 0, 0, false, CreateSuspended | CreateNoWindow, 0, null, ref startup, out var info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "the test could not start a suspended child");
            var thread = info.Thread;
            return EngineProcess.Started(new SafeProcessHandle(info.Process, ownsHandle: true), info.ProcessId, () =>
            {
                _ = Native.ResumeThread(thread);
                Native.CloseHandle(thread);
            });
        }

        var code = SessionProbe.Run(said.Add, Launch);

        Assert.Equal(PlayMap.Supervisor.Program.Done, code);
        // The console session is read first; where a runner has one, the child reports within the wait. Where it has none the probe
        // says so and starts no child — then there is nothing to prove here, and nothing to refute the fix either.
        if (said.Any(l => l.Contains("no console session", StringComparison.Ordinal)))
            return;
        Assert.Contains(said, l => l.StartsWith("- the child reports:", StringComparison.Ordinal));
        Assert.DoesNotContain(said, l => l.Contains("the child wrote no report", StringComparison.Ordinal));
        Assert.Contains(said, l => l.Contains("probe-ran", StringComparison.Ordinal));
    }
}
