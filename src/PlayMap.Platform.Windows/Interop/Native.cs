using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Platform.Windows.Interop;

/// <summary>The Win32 calls the platform makes, each declared by hand from the Windows SDK's headers (<c>LibraryImport</c>, the
/// system directory only). Thin: every answer is read by the caller, which turns a failure into a claim's words.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Native
{
    public const uint HighPriorityClass = 0x80, NormalPriorityClass = 0x20, AboveNormalPriorityClass = 0x8000, BelowNormalPriorityClass = 0x4000,
        IdlePriorityClass = 0x40, RealtimePriorityClass = 0x100;

    // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling and THREAD_INFORMATION_CLASS.ThreadPowerThrottling.
    public const int ProcessPowerThrottling = 4, ThreadPowerThrottling = 3;

    // PROCESS_POWER_THROTTLING_* and THREAD_POWER_THROTTLING_*: the state's version and its masks.
    public const uint PowerThrottlingCurrentVersion = 1, ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    public const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003, Infinite = 0xFFFFFFFF;

    public const uint DuplicateSameAccess = 0x2;

    public const int AvrtPriorityHigh = 1;

    public const uint TokenQuery = 0x8, TokenAdjustPrivileges = 0x20, SePrivilegeEnabled = 0x2;

    public const int TokenPrivileges = 3;

    // POWER_REQUEST_TYPE and the reason context's version and flags.
    public const int PowerRequestDisplayRequired = 0, PowerRequestSystemRequired = 1;
    public const uint PowerRequestContextVersion = 0, PowerRequestContextSimpleString = 0x1;

    public const uint SpiGetScreenSaveActive = 0x0010;

    // ---- kernel32: the process, the thread, the CPU sets, the timer --------------------------------------------------------

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPriorityClass(nint process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetPriorityClass(nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(nint process, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessInformation(nint process, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadInformation(nint thread, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetThreadInformation(nint thread, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadPriority(nint thread, int priority);

    /// <summary>THREAD_PRIORITY_ERROR_RETURN (0x7FFFFFFF) on failure.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int GetThreadPriority(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemCpuSetInformation(void* information, uint length, out uint returned, nint process, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadSelectedCpuSets(nint thread, uint* ids, uint count);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetThreadSelectedCpuSets(nint thread, uint* ids, uint count, out uint required);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial Microsoft.Win32.SafeHandles.SafeWaitHandle CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(Microsoft.Win32.SafeHandles.SafeWaitHandle timer, in long dueTime, int period, nint completion, nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);


    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProductInfo(uint osMajor, uint osMinor, uint spMajor, uint spMinor, out uint productType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint PowerCreateRequest(in ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PowerSetRequest(nint request, int type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PowerClearRequest(nint request, int type);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint LocalFree(nint memory);

    // ---- ntdll: the version Windows does not shim --------------------------------------------------------------------------

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int RtlGetVersion(ref OsVersionInfoEx info);

    // ---- avrt: MMCSS -------------------------------------------------------------------------------------------------------

    [LibraryImport("avrt.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint AvSetMmThreadCharacteristicsW(string task, ref uint taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvSetMmThreadPriority(nint avrt, int priority);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(nint avrt);

    // ---- gdi32: the GPU scheduling class (an NTSTATUS each) ---------------------------------------------------------------

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priority);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int D3DKMTGetProcessSchedulingPriorityClass(nint process, out int priority);

    // ---- advapi32: the token's privileges ---------------------------------------------------------------------------------

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint token, int informationClass, void* information, uint length, out uint returned);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, in TokenPrivilege newState,
        uint bufferLength, nint previousState, nint returnLength);

    // ---- powrprof: the power plan ------------------------------------------------------------------------------------------

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint PowerGetActiveScheme(nint rootKey, out Guid* scheme);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint PowerReadFriendlyName(nint rootKey, in Guid scheme, Guid* subgroup, Guid* setting, byte* buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint PowerReadACValueIndex(nint rootKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    // ---- user32 ----------------------------------------------------------------------------------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint action, uint param, void* value, uint winIni);

    // ---- ole32 and oleaut32: COM for the network list -----------------------------------------------------------------------

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CoInitializeEx(nint reserved, uint init);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("oleaut32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial void SysFreeString(nint text);

    // ---- dxgi ------------------------------------------------------------------------------------------------------------

    [LibraryImport("dxgi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CreateDXGIFactory2(uint flags, in Guid iid, out nint factory);

    /// <summary>A Win32 error as words: "error 5: Access is denied."</summary>
    public static string ErrorWords(int error) => $"error {error}: {Marshal.GetPInvokeErrorMessage(error)}";

    /// <summary>The last Win32 error of this thread's last call, as words.</summary>
    public static string LastErrorWords() => ErrorWords(Marshal.GetLastPInvokeError());

    /// <summary>An HRESULT or NTSTATUS as words: "0x8007000E".</summary>
    public static string StatusWords(int status) => "0x" + status.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
