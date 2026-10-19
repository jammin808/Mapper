using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using PlayMap.Core.Fleet;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Policy;
using PlayMap.Supervisor.Status;

namespace PlayMap.Supervisor.Windows;

/// <summary>Where a supervisor keeps its files and names, and how it reads the console session and starts an engine: this machine's
/// for the service, a test's own for a supervisor a test runs.</summary>
[SupportedOSPlatform("windows")]
public sealed record SupervisorPlaces(
    string Folder,
    string PipeName,
    string StopEventName,
    Func<string> InstallDir,
    Func<string> ShowUser,
    Func<uint?> ConsoleSession,
    Func<uint, string> UserOf,
    Func<uint, string, string, EngineProcess> Launch)
{
    /// <summary>This machine's: <c>%ProgramData%\PlayMap\supervisor</c>, the service's pipe and stop event, the install's key, the
    /// active console session, and an engine started there as its user.</summary>
    public static SupervisorPlaces ThisMachine() => new(EnginePidRecord.MachineFolder(), MachinePlan.PipeName, MachinePlan.StopEvent,
        () => SupervisorHost.InstallValue("InstallDir"), () => SupervisorHost.InstallValue("ShowUser"), SessionLauncher.ConsoleSession,
        SessionLauncher.UserOf, SessionLauncher.Start);
}

/// <summary>The supervisor: every half second it reads the facts — the console session and its user, the engine's process (held by its
/// handle), HEALTH over loopback — steps <see cref="SupervisorCore"/> and does what it answers: starts <c>PlayMap.exe engine
/// --supervised</c> in the show user's session, sets the engine's stop event, ends it after the grace. An engine it did not start is
/// adopted only when the pid record names the installed engine, run as the show user in the console session; any other record is
/// removed with words, and nothing is ever ended on a record's word alone. It writes the status file at every change and answers
/// <c>status</c>, <c>resume</c> and <c>stop</c> on its pipe.</summary>
[SupportedOSPlatform("windows")]
public sealed class SupervisorHost : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HealthTime = TimeSpan.FromMilliseconds(400);

    /// <summary>The pipe's instances at most: three connections served at once and the one that listens.</summary>
    public const int MaxPipeInstances = 4;

    /// <summary>The longest line the pipe reads: its words are at most six bytes.</summary>
    public const int WordBytes = 64;

    /// <summary>How long a connection has to send its line and read its answer.</summary>
    public static readonly TimeSpan ConnectionTime = TimeSpan.FromSeconds(2);

    /// <summary>The answer to anything but a word the pipe takes.</summary>
    public const string NotAWord = "ERR the supervisor's pipe answers status, resume and stop";

    private readonly SupervisorPlaces _places;
    private readonly SupervisorCore _core = new(SupervisorPolicy.Default);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CountdownEvent _ended = new(2);
    private EventWaitHandle? _stopEvent;
    private EngineProcess? _engine;
    private readonly object _pipeGate = new();
    private readonly SemaphoreSlim _connections = new(MaxPipeInstances - 1);
    private int _pipeInstances;
    private bool _pipeRefused;
    private string _unmappedSaid = "";
    private string _lastStatus = "";
    private string _refusedRecord = "";

    public SupervisorHost(SupervisorPlaces places)
    {
        ArgumentNullException.ThrowIfNull(places);
        _places = places;
    }

    /// <summary>The pid of the engine held now (0 none).</summary>
    public int EnginePid
    {
        get
        {
            lock (_gate)
                return _engine?.Pid ?? 0;
        }
    }

    /// <summary>The policy's state now.</summary>
    public SupervisorState State
    {
        get
        {
            lock (_gate)
                return _core.State;
        }
    }

    /// <summary>The folder and the stop event, made before anything runs.</summary>
    public void Prepare()
    {
        Directory.CreateDirectory(_places.Folder);
        _stopEvent ??= StopEventFor(_places.StopEventName, _places.ShowUser());
    }

    /// <summary>Prepares, then runs the step loop and the pipe until <see cref="Halt"/>.</summary>
    public void Start()
    {
        Prepare();
        Log("the supervisor started");
        // Both loops signal _ended as they end; Halt waits on it.
        _ = LoopAsync(_stop.Token);
        _ = PipeAsync(_stop.Token);
    }

    public void Halt(string why)
    {
        Log(why);
        _stop.Cancel();
        // The loops end by cancellation; ones that do not end within 5 s end with the process.
        if (!_ended.Wait(TimeSpan.FromSeconds(5), CancellationToken.None))
            Log("the loops did not end within 5 s");
    }

    public void Dispose()
    {
        _stopEvent?.Dispose();
        _engine?.Dispose();
        _connections.Dispose();
        _stop.Dispose();
        _ended.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Off the service's start call at once: Windows waits for OnStart to return.
        await Task.Yield();
        try
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The supervisor is halting.
        }
        finally
        {
            _ended.Signal();
        }
    }

    /// <summary>One step: read the facts, step the policy, act, write the status.</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        var showUser = _places.ShowUser();
        var session = _places.ConsoleSession();
        var sessionUser = session is { } s ? _places.UserOf(s) : "";
        var sessionOk = session is not null && sessionUser.Length > 0
            && (showUser.Length == 0 || string.Equals(Bare(showUser), sessionUser, StringComparison.OrdinalIgnoreCase));

        var record = ReadRecord();
        EngineProcess? engine;
        lock (_gate)
        {
            if (_engine is { IsAlive: false } gone)
            {
                gone.Dispose();
                _engine = null;
            }
            if (_engine is null && record is not null && session is { } console)
                _engine = AdoptOrRemove(record, (int)console, showUser);
            engine = _engine;
        }
        var health = engine is not null && record is { WirePort: > 0 } r && r.Pid == engine.Pid
            ? await LoopbackWire.AskAsync(r.WirePort, "HEALTH", HealthTime, ct).ConfigureAwait(false)
            : null;
        var facts = Facts(sessionOk, engine is not null, health is { Ok: true } ? HealthJson.Parse(health.Payload) : null);

        lock (_gate)
        {
            var action = _core.Step(NowNs(), facts);
            Act(action, session, sessionUser);
        }
        WriteStatus();
    }

    /// <summary>The record's engine when it is the installed engine, run as the show user in the console session; otherwise the
    /// record is removed (said once), since a record that names another process would keep the started engine from writing its own.
    /// </summary>
    private EngineProcess? AdoptOrRemove(EnginePidRecord record, int consoleSession, string showUser)
    {
        var key = record.ToJson();
        if (!EngineProcess.ReadFacts(record.Pid).Exists)
        {
            // An engine that ended without taking its record back (a crash, a termination): nothing to adopt.
            if (key != _refusedRecord)
            {
                _refusedRecord = key;
                Log(string.Create(CultureInfo.InvariantCulture, $"the pid record names pid {record.Pid}, which no longer runs: the record is removed"));
            }
            RemoveRecord(key);
            return null;
        }
        var expect = new EngineExpectation(InstalledExe(), SidOf(showUser), consoleSession);
        if (EngineProcess.Adopt(record, expect, out var why) is { } adopted)
        {
            _refusedRecord = "";
            Log(string.Create(CultureInfo.InvariantCulture, $"adopted the running engine (pid {adopted.Pid}) its record names"));
            return adopted;
        }
        if (key != _refusedRecord)
        {
            _refusedRecord = key;
            Log(string.Create(CultureInfo.InvariantCulture, $"the pid record names pid {record.Pid}, which is not the installed engine ({why}): not adopted, and the record is removed"));
        }
        RemoveRecord(key);
        return null;
    }

    private void RemoveRecord(string key)
    {
        var path = Path.Combine(_places.Folder, EnginePidRecord.FileName);
        try
        {
            // Only the record that was refused: one an engine wrote since stays.
            if (File.Exists(path) && EnginePidRecord.Named(_places.Folder)?.ToJson() == key)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("the refused pid record could not be removed: " + ex.Message);
        }
    }

    private EnginePidRecord? ReadRecord()
    {
        try
        {
            return Directory.Exists(_places.Folder) ? EnginePidRecord.Named(_places.Folder) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("the pid record could not be read: " + ex.Message);
            return null;
        }
    }

    /// <summary>The installed exe: the install's folder, or the folder above this supervisor's when the key names none.</summary>
    private string InstalledExe() => Path.Combine(_places.InstallDir() is { Length: > 0 } dir
        ? dir
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "PlayMap.exe");

    /// <summary>The show user's SID ("" when no show user is named; an account Windows cannot find is the null SID, which no engine
    /// runs as, so nothing is adopted).</summary>
    private string SidOf(string showUser) =>
        showUser.Length == 0 ? "" : Sid(showUser)?.Value ?? new SecurityIdentifier(WellKnownSidType.NullSid, null).Value;

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
        var now = NowNs();
        var sinceNs = gpu.SinceUnixMs < 0 ? now : now - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - gpu.SinceUnixMs) * 1_000_000;
        return new SupervisorFacts(sessionOk, alive, true, frames, open, gpu.Phase, sinceNs, gpu.Epoch);
    }

    /// <summary>The monotonic clock in nanoseconds, exact at any timer frequency.</summary>
    internal static long NowNs() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000_000 / Stopwatch.Frequency);

    private void Act(SupervisorAction action, uint? session, string user)
    {
        switch (action)
        {
            case SupervisorAction.Start when session is { } s:
                // Cleared before a start, so a new engine never sees an old stop.
                _stopEvent?.Reset();
                try
                {
                    _engine = _places.Launch(s, InstalledExe(), "engine --supervised");
                    Log(string.Create(CultureInfo.InvariantCulture, $"started the engine (pid {_engine.Pid}) in session {s} as {user}"));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
                {
                    _engine = null;
                    Log("the engine did not start: " + ex.Message);
                }
                break;
            case SupervisorAction.RequestStop:
                _stopEvent?.Set();
                Log(string.Create(CultureInfo.InvariantCulture, $"asked the engine (pid {_engine?.Pid ?? 0}) to stop: {_core.LastCause}"));
                break;
            case SupervisorAction.Terminate when _engine is { } engine:
                Log(engine.End(out var why)
                    ? string.Create(CultureInfo.InvariantCulture, $"terminated the engine (pid {engine.Pid}) after the grace: {_core.LastCause}")
                    : string.Create(CultureInfo.InvariantCulture, $"the engine (pid {engine.Pid}) could not be terminated: {why}"));
                break;
        }
        if (action != SupervisorAction.Start && SupervisorCore.ShouldResetStopEvent(action, _core.State, _engine is { IsAlive: true }))
            _stopEvent?.Reset();
    }

    private void WriteStatus()
    {
        SupervisorStatus status;
        lock (_gate)
            status = SupervisorStatus.Of(_core, _engine?.Pid ?? 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var key = status with { AtUnixMs = 0 };
        if (key.ToJson() == _lastStatus)
            return;
        _lastStatus = key.ToJson();
        try
        {
            status.Write(_places.Folder);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The supervisor is halting.
        }
        finally
        {
            _ended.Signal();
        }
    }

    /// <summary>The pipe: one word a line (at most <see cref="WordBytes"/> bytes, within <see cref="ConnectionTime"/>), answered with
    /// one line. The name is this supervisor's only: its first instance is made with FirstPipeInstance (a name another process holds
    /// is said once in the log and tried again each half second, never served on), and the next instance is made before a connection
    /// is answered, so the name never has no instance of this supervisor's that another process could take. Each connection is served
    /// on its own task, at most three at once beside the listening one, and one that fails, sends too much or sends nothing ends
    /// alone.</summary>
    public async Task ServePipeAsync(CancellationToken ct)
    {
        NamedPipeServerStream? listening = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                listening ??= PipeInstance();
                if (listening is null)
                {
                    await Task.Delay(Tick, ct).ConfigureAwait(false);
                    continue;
                }
                await listening.WaitForConnectionAsync(ct).ConfigureAwait(false);
                // A slot for this connection; while every slot is taken, this connected instance keeps the name.
                await _connections.WaitAsync(ct).ConfigureAwait(false);
                var connected = listening;
                listening = PipeInstance();
                _ = ServeConnectionAsync(connected, ct);
            }
        }
        finally
        {
            if (listening is not null)
                ClosePipe(listening);
        }
    }

    /// <summary>A new instance of the pipe: the first with FirstPipeInstance; others only while one of this supervisor's exists, which
    /// the lock keeps from ending between the count and the make. Null, said once, when Windows refuses it.</summary>
    private NamedPipeServerStream? PipeInstance()
    {
        lock (_pipeGate)
        {
            var first = _pipeInstances == 0;
            try
            {
                var server = NamedPipeServerStreamAcl.Create(_places.PipeName, PipeDirection.InOut, MaxPipeInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, PipeAcl(_places.ShowUser()));
                _pipeInstances++;
                _pipeRefused = false;
                return server;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_pipeRefused)
                {
                    _pipeRefused = true;
                    Log(first
                        ? "the pipe name " + _places.PipeName + " is held by another process (" + ex.Message + "): the pipe is not served until the name is free"
                        : "the pipe took no further instance (" + ex.Message + "): it is made again when one ends");
                }
                return null;
            }
        }
    }

    private void ClosePipe(NamedPipeServerStream pipe)
    {
        lock (_pipeGate)
        {
            pipe.Dispose();
            _pipeInstances--;
        }
    }

    /// <summary>One connection: its word, read within the time and the cap, and its answer; any failure ends this connection alone.</summary>
    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ConnectionTime);
            var word = await ReadWordAsync(pipe, deadline.Token).ConfigureAwait(false);
            var answer = word is null ? NotAWord : Answer(word);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"), deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log("a pipe client was cut off: no line within " + ConnectionTime.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s, or its answer not read");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log("a pipe client left before its answer: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            // The supervisor is halting.
        }
        finally
        {
            ClosePipe(pipe);
            _connections.Release();
        }
    }

    /// <summary>The line a client sent, up to its newline (or its end), or null when it runs past <see cref="WordBytes"/> bytes.</summary>
    internal static async Task<string?> ReadWordAsync(Stream pipe, CancellationToken ct)
    {
        var buffer = new byte[WordBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0)
                return Encoding.UTF8.GetString(buffer, 0, count).Trim();
            var newline = Array.IndexOf(buffer, (byte)'\n', count, read);
            if (newline >= 0)
                return Encoding.UTF8.GetString(buffer, 0, newline).Trim();
            count += read;
        }
        return null;
    }

    /// <summary>The pipe's answer to one word.</summary>
    private string Answer(string word)
    {
        lock (_gate)
        {
            return word switch
            {
                "status" => SupervisorStatus.Of(_core, _engine?.Pid ?? 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToJson(),
                "resume" => _core.Resume() ? "OK resumed: the supervisor starts the engine again" : "ERR there is nothing to resume: " + SupervisorStatus.Word(_core.State),
                "stop" => StopAnswer(),
                _ => NotAWord,
            };
        }
    }

    private string StopAnswer()
    {
        var action = _core.Hold(NowNs(), "stopped by a person (PlayMap.Supervisor.exe stop)");
        if (action == SupervisorAction.RequestStop)
        {
            _stopEvent?.Set();
            Log(string.Create(CultureInfo.InvariantCulture, $"asked the engine (pid {_engine?.Pid ?? 0}) to stop: {_core.LastCause}"));
        }
        Log("held: " + _core.LastCause);
        return "OK held: the engine is asked to stop and is not started again until resume";
    }

    /// <summary>The pipe's ACL: SYSTEM, administrators and the show user; a show user Windows cannot find is left out, said once.</summary>
    private PipeSecurity PipeAcl(string showUser)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        if (showUser.Length > 0 && Sid(showUser) is { } user)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>An account's SID, or null, said once, when Windows cannot find the account.</summary>
    private SecurityIdentifier? Sid(string account)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException ex)
        {
            if (_unmappedSaid != account)
            {
                _unmappedSaid = account;
                Log("the show user " + account + " cannot be found (" + ex.Message + "): SYSTEM and administrators alone may use the pipe, and no engine is adopted");
            }
            return null;
        }
    }

    /// <summary>The engine's stop event: SYSTEM sets and clears it, administrators may set it (an elevated <c>stop</c> or uninstall
    /// with no service) and read its ACL, and the show user's engine may only wait on it. One that exists already is kept only when
    /// SYSTEM or administrators made it (an engine waiting on it holds it open across a supervisor's restart); one another account
    /// made could be set or cleared by that account, so the supervisor refuses to start beside it, with words.</summary>
    private EventWaitHandle StopEventFor(string name, string showUser)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions, AccessControlType.Allow));
        if (showUser.Length > 0 && Sid(showUser) is { } user)
            security.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        EventWaitHandle stop;
        try
        {
            stop = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out var createdNew, security);
            if (createdNew)
                return stop;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log("the engine's stop event " + name + " exists already and is not the supervisor's to open (" + ex.Message + "): the supervisor does not start beside it");
            throw;
        }
        var owner = (stop.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
        if (!MachinePlan.StopEventOwnerTrusted(owner))
        {
            stop.Dispose();
            var words = "the engine's stop event " + name + " exists already, made by " + (owner.Length > 0 ? owner : "an owner Windows does not name")
                + ", which could set or clear it: the supervisor does not start beside it";
            Log(words);
            throw new InvalidOperationException(words);
        }
        Log("the engine's stop event " + name + " is held open already (by an engine waiting on it): kept");
        return stop;
    }


    /// <summary>A value of the install's registry key ("" when there is none).</summary>
    internal static string InstallValue(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachinePlan.InstallKey);
        return key?.GetValue(name) as string ?? "";
    }

    private static string Bare(string account) => account.Contains('\\', StringComparison.Ordinal) ? account[(account.IndexOf('\\', StringComparison.Ordinal) + 1)..] : account;

    private void Log(string line) => SupervisorLog.Append(_places.Folder, line);
}
