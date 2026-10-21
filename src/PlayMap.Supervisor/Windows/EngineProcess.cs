using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PlayMap.Core.Machine;

namespace PlayMap.Supervisor.Windows;

/// <summary>An engine process held by its handle: one the supervisor started, or one a pid record named that passed
/// <see cref="EngineIdentity"/> when its handle was opened. Its facts are read through that handle, it is waited on and ended through
/// it, so a pid another process takes later is never the one ended. The pid is for the record and the words only.</summary>
[SupportedOSPlatform("windows")]
public sealed class EngineProcess : IDisposable
{
    private const uint QueryLimited = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint Terminate = 0x0001;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;
    private const int ErrorInvalidParameter = 87;

    private readonly SafeProcessHandle _handle;
    private Action? _resume;

    private EngineProcess(SafeProcessHandle handle, int pid, Action? resume = null)
    {
        _handle = handle;
        Pid = pid;
        _resume = resume;
    }

    public int Pid { get; }

    /// <summary>A process this supervisor started, held by the handle Windows gave it; one started suspended carries what lets it run
    /// (<paramref name="resume"/>), which <see cref="Resume"/> does once.</summary>
    internal static EngineProcess Started(SafeProcessHandle handle, int pid, Action? resume = null) => new(handle, pid, resume);

    /// <summary>Lets a process started suspended run, once; a process started running, or one opened by its pid, already runs.</summary>
    public void Resume() => Interlocked.Exchange(ref _resume, null)?.Invoke();

    /// <summary>The process of that id, opened to read, wait on and end it; null with Windows' words when it may not be opened (gone,
    /// or not this process's to end).</summary>
    public static EngineProcess? Open(int pid, out string why)
    {
        TryEnterDebugMode();
        var handle = Native.OpenProcessHandle(QueryLimited | Synchronize | Terminate, false, pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            why = error == ErrorInvalidParameter
                ? string.Create(CultureInfo.InvariantCulture, $"pid {pid} is not running")
                : string.Create(CultureInfo.InvariantCulture, $"pid {pid} could not be opened: {new Win32Exception(error).Message}");
            return null;
        }
        why = "";
        return new EngineProcess(handle, pid);
    }

    /// <summary>The engine <paramref name="record"/> names, held by its handle, when its process passes <paramref name="expect"/>;
    /// null with why otherwise. The facts are read through the handle the caller then keeps.</summary>
    public static EngineProcess? Adopt(EnginePidRecord record, EngineExpectation expect, out string why)
    {
        ArgumentNullException.ThrowIfNull(record);
        var process = Open(record.Pid, out why);
        if (process is null)
            return null;
        if (EngineIdentity.Refusal(record, process.Facts(), expect) is { } refusal)
        {
            why = refusal;
            process.Dispose();
            return null;
        }
        return process;
    }

    /// <summary>What Windows says of the process of that id now (<see cref="ProcessFacts.Gone"/> when no process has it). The handle
    /// is opened to read and to wait on, as every handle <see cref="FactsOf"/> reads is: whether the process runs is a wait.</summary>
    public static ProcessFacts ReadFacts(int pid)
    {
        using var handle = Native.OpenProcessHandle(QueryLimited | Synchronize, false, pid);
        if (handle.IsInvalid)
        {
            // An id no process has is refused as a bad parameter; any other refusal is a process this one may not read.
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter ? ProcessFacts.Gone : new ProcessFacts(true, "", "", -1, "", "", -1, 0);
        }
        return FactsOf(handle, pid);
    }

    /// <summary>What Windows says of this process now, read through its handle.</summary>
    public ProcessFacts Facts() => FactsOf(_handle, Pid);

    /// <summary>Whether the process still runs.</summary>
    public bool IsAlive => Native.WaitForSingleObject(_handle, 0) == WaitTimeout;

    /// <summary>Waits up to <paramref name="within"/> for the process to end; true when it has.</summary>
    public bool WaitForExit(TimeSpan within) =>
        Native.WaitForSingleObject(_handle, (uint)Math.Clamp(within.TotalMilliseconds, 0, uint.MaxValue - 1)) == WaitObject0;

    /// <summary>Ends the process (exit code 1); false with Windows' words when it may not be ended.</summary>
    public bool End(out string why)
    {
        if (Native.TerminateProcess(_handle, 1) || !IsAlive)
        {
            why = "";
            return true;
        }
        why = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        return false;
    }

    /// <summary>Lets a process still suspended run (its thread's handle is closed so), then lets go of the process's handle.</summary>
    public void Dispose()
    {
        Resume();
        _handle.Dispose();
    }

    /// <summary>Core's facts of the process (<see cref="ProcessReader.FactsOf"/>: whether it runs, its image, start and command line), with
    /// the user, session and parent the supervisor reads through the same handle.</summary>
    private static ProcessFacts FactsOf(SafeProcessHandle handle, int pid)
    {
        var facts = ProcessReader.FactsOf(handle);
        if (!facts.Exists)
            return facts;
        var (sid, session) = UserOf(handle, pid);
        return facts with { UserSid = sid, Session = session, ParentPid = ParentOf(handle) };
    }

    /// <summary>The SID of the user the process runs as and its session, from its token ("" and −1 when the token may not be read).</summary>
    private static (string Sid, int Session) UserOf(SafeProcessHandle handle, int pid)
    {
        var session = Native.ProcessIdToSessionId(pid, out var s) ? (int)s : -1;
        if (!Native.OpenProcessToken(handle, Native.TokenQuery, out var token))
            return ("", session);
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            var sid = Native.GetTokenInformation(token, Native.TokenUser, buffer, 1024, out _)
                ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value
                : "";
            if (Native.GetTokenInformation(token, Native.TokenSessionId, buffer, 4, out _))
                session = Marshal.ReadInt32(buffer);
            return (sid, session);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Native.CloseHandle(token);
        }
    }

    /// <summary>The pid of the process that started this one (0 when it cannot be read): PROCESS_BASIC_INFORMATION's
    /// InheritedFromUniqueProcessId, its sixth pointer-sized field.</summary>
    private static int ParentOf(SafeProcessHandle handle)
    {
        var size = 6 * nint.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return Native.NtQueryInformationProcess(handle, Native.ProcessBasicInformation, buffer, size, out _) == 0
                ? (int)Marshal.ReadIntPtr(buffer, 5 * nint.Size)
                : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>An elevated caller may end a show user's process only with its debug right enabled; one that does not hold it is
    /// answered by Windows when the process is opened.</summary>
    private static void TryEnterDebugMode()
    {
        try
        {
            Process.EnterDebugMode();
        }
        catch (Win32Exception ex)
        {
            Trace.WriteLine("PlayMap.Supervisor: the debug right is not this process's: " + ex.Message);
        }
    }
}
