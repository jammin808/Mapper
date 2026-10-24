using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using PlayMap.Core.Machine;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>The engine's folder is the show user's to write, and the supervisor reads its pid record at every step: a record file the
/// show user grows to gigabytes — made sparse and its end set, as any account may do to a file it made, so it takes no room on the disk —
/// is refused by its length before any of it is read, so the step goes on as with no record: it starts the engine and writes the status,
/// and writes nothing in the engine's folder.</summary>
[SupportedOSPlatform("windows")]
public sealed class GrownRecordTests
{
    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, int inSize, nint outBuffer, int outSize,
        out int returned, nint overlapped);

    [WindowsFact]
    public async Task ARecordFileGrownToGigabytesKeepsNoStepFromStartingTheEngineAndWritingTheStatus()
    {
        using var place = new EngineIdentityWindowsTests.Place();
        var path = Path.Combine(place.EngineFolder, EnginePidRecord.FileName);
        const long grownTo = 2L << 30;
        using (var grown = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            // FSCTL_SET_SPARSE with no input marks the file sparse; its end is then set with nothing written.
            Assert.True(DeviceIoControl(grown.SafeFileHandle, FsctlSetSparse, 0, 0, 0, 0, out _, 0),
                "FSCTL_SET_SPARSE was refused: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            grown.SetLength(grownTo);
        }
        using var host = place.Host();
        host.Prepare();

        await host.StepAsync(CancellationToken.None);

        // The step reached its act (it asked for an engine, which this test's launcher refuses) and its status.
        Assert.Contains("the engine did not start: this test starts no engine", place.Log(), StringComparison.Ordinal);
        Assert.NotNull(SupervisorStatus.Read(place.Folder));
        Assert.DoesNotContain("a step failed", place.Log(), StringComparison.Ordinal);
        Assert.Equal(grownTo, new FileInfo(path).Length);
        Assert.Equal([EnginePidRecord.FileName], Directory.GetFiles(place.EngineFolder).Select(Path.GetFileName));
    }
}
