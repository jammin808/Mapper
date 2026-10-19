using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Windows;

/// <summary>An answer read from the supervisor's pipe: the server's words, or why its server is not the supervisor (then nothing it
/// said was read).</summary>
[SupportedOSPlatform("windows")]
public sealed record PipeAnswer(bool FromSupervisor, string Words);

/// <summary>The client of the supervisor's pipe: it connects for identification only (the server may learn who asks, never act as
/// them), names the process serving the pipe, holds it to the caller's rule before reading a word of its answer, and reads one line.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SupervisorPipe
{
    /// <summary>How long a client waits for a listening instance.</summary>
    public static readonly TimeSpan ConnectTime = TimeSpan.FromSeconds(2);

    /// <summary>Asks <paramref name="word"/>; null when no instance listens within the connect time (no supervisor runs).</summary>
    public static PipeAnswer? Ask(string pipeName, string word, Func<int, int, string?> refuseServer)
    {
        ArgumentNullException.ThrowIfNull(refuseServer);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            client.Connect((int)ConnectTime.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            return null;
        }
        if (!Native.GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid))
            return new PipeAnswer(false, "a process Windows does not name (" + Marshal.GetLastPInvokeError() + ")");
        var session = Native.GetNamedPipeServerSessionId(client.SafePipeHandle, out var s) ? (int)s : -1;
        if (refuseServer((int)pid, session) is { } why)
            return new PipeAnswer(false, why);
        client.Write(Encoding.UTF8.GetBytes(word + "\n"));
        client.Flush();
        using var reader = new StreamReader(client, Encoding.UTF8);
        return new PipeAnswer(true, reader.ReadLine() ?? "");
    }

    /// <summary>This machine's supervisor, as <see cref="PipeServerRule"/> holds it: SYSTEM, the installed supervisor's exe, or a
    /// process this account may not read in session 0.</summary>
    public static Func<int, int, string?> ThisMachinesSupervisor(string installedSupervisor) =>
        (pid, session) => PipeServerRule.Refusal(pid, EngineProcess.ReadFacts(pid), session, installedSupervisor);
}
