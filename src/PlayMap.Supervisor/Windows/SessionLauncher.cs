using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Supervisor.Windows;

/// <summary>The show user's session and a process started in it (M9): the active console session, its user, and
/// <c>WTSQueryUserToken</c>, <c>CreateEnvironmentBlock</c>, <c>CreateProcessAsUser</c> on <c>winsta0\default</c>. Only a LocalSystem
/// process may ask a session's token; anyone else is answered with Windows' words.</summary>
[SupportedOSPlatform("windows")]
public static class SessionLauncher
{
    /// <summary>The active console session's id, or null when no one sits at the console.</summary>
    public static uint? ConsoleSession()
    {
        var id = Native.WTSGetActiveConsoleSessionId();
        return id == Native.NoSession ? null : id;
    }

    /// <summary>The user signed in to <paramref name="session"/> ("" none).</summary>
    public static string UserOf(uint session)
    {
        if (!Native.WTSQuerySessionInformation(0, session, Native.WtsUserName, out var buffer, out _))
            return "";
        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    /// <summary>Starts <paramref name="exe"/> with <paramref name="arguments"/> as the user of <paramref name="session"/>, in that
    /// session, on its interactive desktop, with that user's environment; its process id. Throws with Windows' words when a call is
    /// refused.</summary>
    public static int Start(uint session, string exe, string arguments)
    {
        if (!Native.WTSQueryUserToken(session, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken refused session " + session.ToString(CultureInfo.InvariantCulture));
        var environment = nint.Zero;
        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        try
        {
            if (!Native.CreateEnvironmentBlock(out environment, token, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock refused the show user's token");
            var startup = new Native.StartupInfo { Cb = Marshal.SizeOf<Native.StartupInfo>(), Desktop = desktop };
            var line = ("\"" + exe + "\" " + arguments + "\0").ToCharArray();
            if (!Native.CreateProcessAsUser(token, exe, line, 0, 0, false, Native.CreateUnicodeEnvironment | Native.CreateNoWindow,
                    environment, Path.GetDirectoryName(exe), ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser refused " + exe);
            Native.CloseHandle(info.Thread);
            Native.CloseHandle(info.Process);
            return info.ProcessId;
        }
        finally
        {
            if (environment != 0)
                Native.DestroyEnvironmentBlock(environment);
            Marshal.FreeHGlobal(desktop);
            Native.CloseHandle(token);
        }
    }

    /// <summary>A process's command line ("" when it cannot be read: gone, or not this process's to read).</summary>
    public static string CommandLineOf(int pid)
    {
        var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
            return "";
        var buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            if (Native.NtQueryInformationProcess(process, Native.ProcessCommandLineInformation, buffer, 64 * 1024, out _) != 0)
                return "";
            // A UNICODE_STRING: its length in bytes, then a pointer to the characters (inside the same buffer).
            var bytes = (ushort)Marshal.ReadInt16(buffer);
            var chars = Marshal.ReadIntPtr(buffer, nint.Size);
            return chars == 0 ? "" : Marshal.PtrToStringUni(chars, bytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Native.CloseHandle(process);
        }
    }
}
