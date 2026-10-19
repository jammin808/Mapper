using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Bench.Probe;

/// <summary>The few Windows calls TerraFX does not declare, each with its header's signature: MMCSS (avrt.h), the GPU scheduling
/// class (d3dkmthk.h, exported by gdi32) and the user token of a session (wtsapi32.h). Each library is loaded from System32 only.</summary>
[SupportedOSPlatform("windows")]
internal static partial class Native
{
    /// <summary>AVRT_PRIORITY_HIGH.</summary>
    public const int AvrtPriorityHigh = 1;

    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS: IDLE, BELOW_NORMAL, NORMAL, ABOVE_NORMAL, HIGH, REALTIME, in that order from 0.</summary>
    public static readonly string[] SchedulingClasses = ["idle", "below-normal", "normal", "above-normal", "high", "realtime"];

    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH.</summary>
    public const int SchedulingHigh = 4;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint AvSetMmThreadCharacteristics(string task, ref uint taskIndex);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvSetMmThreadPriority(nint handle, int priority);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(nint handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priority);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTGetProcessSchedulingPriorityClass(nint process, out int priority);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQueryUserToken(uint session, out nint token);
}
