using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PlayMap.Core.Machine;

/// <summary>Windows' facts of a process through its handle, opened to read and to wait on: whether it runs is a wait, its image is
/// QueryFullProcessImageNameW's, its start GetProcessTimes', its command line NtQueryInformationProcess's. The handle needs no more than
/// PROCESS_QUERY_LIMITED_INFORMATION and SYNCHRONIZE, which a process of the same user grants.</summary>
public static partial class ProcessReader
{
    private const uint QueryLimited = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x102;
    private const int ErrorInvalidParameter = 87;
    private const int ProcessCommandLineInformation = 60;

    [SupportedOSPlatform("windows")]
    private static ProcessFacts ReadWindows(int pid)
    {
        using var handle = OpenProcess(QueryLimited | Synchronize, false, pid);
        if (handle.IsInvalid)
        {
            // An id no process has is refused as a bad parameter; any other refusal is a process this one may not read.
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter ? ProcessFacts.Gone : new ProcessFacts(true, "", "", -1, "", "", -1, 0);
        }
        return FactsOf(handle);
    }

    /// <summary>What Windows says now of the process <paramref name="handle"/> holds (opened with at least
    /// PROCESS_QUERY_LIMITED_INFORMATION and SYNCHRONIZE): <see cref="ProcessFacts.Gone"/> once it has ended; its user, session and
    /// parent are not read (empty, −1 and 0).</summary>
    [SupportedOSPlatform("windows")]
    public static ProcessFacts FactsOf(SafeProcessHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (WaitForSingleObject(handle, 0) != WaitTimeout)
            return ProcessFacts.Gone;
        var image = ImageOf(handle);
        var started = GetProcessTimes(handle, out var creation, out _, out _, out _)
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(creation)).ToUnixTimeMilliseconds()
            : -1;
        return new ProcessFacts(true, image.Length > 0 ? Path.GetFileNameWithoutExtension(image) : "", image, started, CommandLineOf(handle), "", -1, 0);
    }

    /// <summary>The command line of the process <paramref name="handle"/> holds ("" when it cannot be read).</summary>
    [SupportedOSPlatform("windows")]
    public static string CommandLineOf(SafeProcessHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        const int size = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out _) != 0)
                return "";
            // A UNICODE_STRING: its length in bytes, then a pointer to the characters (inside the same buffer).
            var bytes = (ushort)Marshal.ReadInt16(buffer);
            var chars = Marshal.ReadIntPtr(buffer, nint.Size);
            return chars == 0 ? "" : Marshal.PtrToStringUni(chars, bytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ImageOf(SafeProcessHandle handle)
    {
        const int chars = 32 * 1024;
        var buffer = Marshal.AllocHGlobal(chars * 2);
        try
        {
            var size = chars;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? Marshal.PtrToStringUni(buffer, size) : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, nint name, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, nint buffer, int length, out int returned);
}
