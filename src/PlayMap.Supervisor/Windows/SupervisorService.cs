using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using PlayMap.Core.Fleet;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Policy;
using PlayMap.Supervisor.Status;

namespace PlayMap.Supervisor.Windows;

/// <summary>The <c>PlayMapSupervisor</c> service (LocalSystem): every half second it reads the facts — the console session and its
/// user, the engine's process, HEALTH over loopback — steps <see cref="SupervisorCore"/> and does what it answers: starts
/// <c>PlayMap.exe engine --supervised</c> in the show user's session, sets the engine's stop event, terminates it after the grace.
/// It writes the status file at every change and answers <c>status</c>, <c>resume</c> and <c>stop</c> on its pipe. Stopping the
/// service leaves the engine running: the next supervisor adopts it by its pid record.</summary>
[SupportedOSPlatform("windows")]
public sealed class SupervisorService : ServiceBase
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HealthTime = TimeSpan.FromMilliseconds(400);

    private readonly SupervisorCore _core = new(SupervisorPolicy.Default);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _folder = EnginePidRecord.MachineFolder();
    private readonly CountdownEvent _ended = new(2);
    private EventWaitHandle? _stopEvent;
    private int _pid;
    private string _lastStatus = "";

    public SupervisorService()
    {
        ServiceName = MachinePlan.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        Directory.CreateDirectory(_folder);
        _stopEvent = StopEventFor(InstallValue("ShowUser"));
        Log("the supervisor started");
        // Both loops signal _ended as they end; OnStop waits on it.
        _ = LoopAsync(_stop.Token);
        _ = PipeAsync(_stop.Token);
    }

    protected override void OnStop() => Halt("the service was stopped: the engine keeps running and the next supervisor adopts it");

    protected override void OnShutdown() => Halt("Windows is shutting down");

    private void Halt(string why)
    {
        Log(why);
        _stop.Cancel();
        // The loops end by cancellation; ones that do not end within 5 s end with the process.
        if (!_ended.Wait(TimeSpan.FromSeconds(5), CancellationToken.None))
            Log("the loops did not end within 5 s");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopEvent?.Dispose();
            _stop.Dispose();
            _ended.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Off the service's start call at once: Windows waits for OnStart to return.
        await Task.Yield();
        try
        {
            await RunLoopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _ended.Signal();
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await StepAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log("a step failed and the next one runs: " + ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task StepAsync(CancellationToken ct)
    {
        var showUser = InstallValue("ShowUser");
        var session = SessionLauncher.ConsoleSession();
        var sessionUser = session is { } s ? SessionLauncher.UserOf(s) : "";
        var sessionOk = session is not null && sessionUser.Length > 0
            && (showUser.Length == 0 || string.Equals(Bare(showUser), sessionUser, StringComparison.OrdinalIgnoreCase));

        var record = EnginePidRecord.Named(_folder);
        if (_pid == 0 && record is not null && PreinstallCheck.IsAlive(record.Pid))
            _pid = record.Pid;
        var alive = _pid != 0 && PreinstallCheck.IsAlive(_pid);
        var health = alive && record is { WirePort: > 0 } r && r.Pid == _pid
            ? await LoopbackWire.AskAsync(r.WirePort, "HEALTH", HealthTime, ct).ConfigureAwait(false)
            : null;
        var facts = Facts(sessionOk, alive, health is { Ok: true } ? HealthJson.Parse(health.Payload) : null);

        SupervisorAction action;
        lock (_gate)
            action = _core.Step(Stopwatch.GetTimestamp() * (1_000_000_000 / Stopwatch.Frequency), facts);
        Act(action, session, sessionUser);
        WriteStatus();
    }

    /// <summary>The facts from what was read: HEALTH's open outputs' shown frames (−1 when any is unknown) and its GPU record.</summary>
    internal static SupervisorFacts Facts(bool sessionOk, bool alive, HealthRecord? health)
    {
        if (health is null)
            return new SupervisorFacts(sessionOk, alive, false, -1, false, GpuPhase.Unknown, 0, -1);
        long frames = 0;
        var open = false;
        foreach (var o in health.Facts.Outputs)
        {
            if (!o.Open)
                continue;
            open = true;
            if (o.ShownFrames < 0)
            {
                frames = -1;
                break;
            }
            frames += o.ShownFrames;
        }
        var gpu = health.Facts.Gpu;
        var now = Stopwatch.GetTimestamp() * (1_000_000_000 / Stopwatch.Frequency);
        var sinceNs = gpu.SinceUnixMs < 0 ? now : now - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - gpu.SinceUnixMs) * 1_000_000;
        return new SupervisorFacts(sessionOk, alive, true, frames, open, gpu.Phase, sinceNs, gpu.Epoch);
    }

    private void Act(SupervisorAction action, uint? session, string user)
    {
        switch (action)
        {
            case SupervisorAction.Start when session is { } s:
                var exe = Path.Combine(InstallValue("InstallDir") is { Length: > 0 } dir ? dir : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "PlayMap.exe");
                try
                {
                    _pid = SessionLauncher.Start(s, exe, "engine --supervised");
                    Log(string.Create(CultureInfo.InvariantCulture, $"started the engine (pid {_pid}) in session {s} as {user}"));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
                {
                    _pid = 0;
                    Log("the engine did not start: " + ex.Message);
                }
                break;
            case SupervisorAction.RequestStop:
                _stopEvent?.Set();
                Log(string.Create(CultureInfo.InvariantCulture, $"asked the engine (pid {_pid}) to stop: {_core.LastCause}"));
                break;
            case SupervisorAction.Terminate:
                Terminate(_pid);
                Log(string.Create(CultureInfo.InvariantCulture, $"terminated the engine (pid {_pid}) after the grace: {_core.LastCause}"));
                break;
        }
        if (action == SupervisorAction.Start || _pid == 0 || PreinstallCheck.IsAlive(_pid))
            _stopEvent?.Reset();
    }

    internal static void Terminate(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already, or ending: there is nothing left to end.
            Trace.WriteLine("PlayMap.Supervisor: the engine ended before it was terminated: " + ex.Message);
        }
    }

    private void WriteStatus()
    {
        SupervisorStatus status;
        lock (_gate)
            status = SupervisorStatus.Of(_core, _pid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var key = status with { AtUnixMs = 0 };
        if (key.ToJson() == _lastStatus)
            return;
        _lastStatus = key.ToJson();
        try
        {
            status.Write(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("the status file could not be written: " + ex.Message);
        }
    }

    private async Task PipeAsync(CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            await ServePipeAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _ended.Signal();
        }
    }

    private async Task ServePipeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var server = NamedPipeServerStreamAcl.Create(MachinePlan.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, PipeAcl(InstallValue("ShowUser")));
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            var word = (await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? "").Trim();
            string answer;
            lock (_gate)
            {
                answer = word switch
                {
                    "status" => SupervisorStatus.Of(_core, _pid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToJson(),
                    "resume" => _core.Resume() ? "OK resumed: the supervisor starts the engine again" : "ERR there is nothing to resume: " + SupervisorStatus.Word(_core.State),
                    "stop" => StopAnswer(),
                    _ => "ERR the supervisor's pipe answers status, resume and stop",
                };
            }
            var bytes = Encoding.UTF8.GetBytes(answer + "\n");
            await server.WriteAsync(bytes, ct).ConfigureAwait(false);
            await server.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private string StopAnswer()
    {
        var action = _core.Hold(Stopwatch.GetTimestamp() * (1_000_000_000 / Stopwatch.Frequency), "stopped by a person (PlayMap.Supervisor.exe stop)");
        if (action == SupervisorAction.RequestStop)
            _stopEvent?.Set();
        Log("held: " + _core.LastCause);
        return "OK held: the engine is asked to stop and is not started again until resume";
    }

    private static PipeSecurity PipeAcl(string showUser)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        if (showUser.Length > 0)
            security.AddAccessRule(new PipeAccessRule(new NTAccount(showUser), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>The engine's stop event, which the show user's engine may wait on and the supervisor sets.</summary>
    private static EventWaitHandle StopEventFor(string showUser)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        if (showUser.Length > 0)
            security.AddAccessRule(new EventWaitHandleAccessRule(new NTAccount(showUser), EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, MachinePlan.StopEvent, out _, security);
    }

    /// <summary>A value of the install's registry key ("" when there is none).</summary>
    internal static string InstallValue(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachinePlan.InstallKey);
        return key?.GetValue(name) as string ?? "";
    }

    private static string Bare(string account) => account.Contains('\\', StringComparison.Ordinal) ? account[(account.IndexOf('\\', StringComparison.Ordinal) + 1)..] : account;

    private void Log(string line) => SupervisorLog.Append(_folder, line);
}
