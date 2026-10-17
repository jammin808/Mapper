using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PlayMap.Engine.Optimise;

/// <summary>How the worker process is started: an executable and the words before "optimise-worker" (the dll when the host is
/// the dotnet launcher).</summary>
public sealed record WorkerLaunch(string FileName, IReadOnlyList<string> Prefix)
{
    /// <summary>This process's own exe in its worker role: "PlayMap optimise-worker", or "dotnet PlayMap.Node.dll optimise-worker".</summary>
    public static WorkerLaunch ThisProcess()
    {
        var exe = Environment.ProcessPath ?? "dotnet";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Environment.GetCommandLineArgs().FirstOrDefault() ?? "";
            return new WorkerLaunch(exe, [dll]);
        }
        return new WorkerLaunch(exe, []);
    }

    /// <summary>A managed dll run by the dotnet launcher (the node host the tests build).</summary>
    public static WorkerLaunch Dll(string dll) => new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", [dll]);
}

/// <summary>The worker process's life on the engine's side: started on demand, its standard output read line by line into
/// <see cref="Event"/>, its standard error into the log, its exit reported once (<see cref="Exited"/>: the exit code and whether
/// the engine asked for it). A kill is how a running job is cancelled; closing its input is how it is asked to end. Every wait on a
/// process is bounded: a killed worker that has not gone <see cref="OptimiseOptions.KillWait"/> later (a thread held in a driver,
/// a lost disk) is said once (<see cref="Stuck"/>) and the engine goes on without it. A worker replaced on its way out is the
/// reader's: once it has gone the reader lets the object go and says so (<see cref="LetGo"/>), for only then are its files free.</summary>
internal sealed class OptimiseSupervisor(OptimiseOptions options, ILogger log) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Process? _process;

    // The current worker was asked to end: a stop or a kill.
    private bool _expected;

    // The current worker's end was reported by its reader: nothing else waits on its object.
    private bool _reported;

    // The worker a stop was begun for, and that stop: one stop per worker.
    private Process? _stopping;
    private Task _stop = Task.CompletedTask;

    // Closed by the service's stop: no worker starts from then on.
    private bool _closed;

    /// <summary>A protocol line from the worker, on its reader's thread.</summary>
    public event Action<WorkerEvent>? Event;

    /// <summary>The worker ended: its exit code, and whether the engine stopped or killed it.</summary>
    public event Action<int, bool>? Exited;

    /// <summary>A killed worker had not gone <see cref="OptimiseOptions.KillWait"/> later: its pid.</summary>
    public event Action<int>? Stuck;

    /// <summary>A worker replaced on its way out has gone: the files it held are free.</summary>
    public event Action? LetGo;

    public int? Pid
    {
        get
        {
            lock (_gate)
                return _process is { } p && !p.HasExited ? p.Id : null;
        }
    }

    public bool Running => Pid is not null;

    /// <summary>Starts the worker unless one runs that was not asked to end and whose end was not reported. A worker on its way out
    /// is killed and a fresh one started at once, so a job is never handed to a worker on its way out and never waits for one that
    /// will not go; the old worker is left to its reader. False once <see cref="Close"/>d: nothing is started or killed.</summary>
    public bool EnsureStarted()
    {
        lock (_gate)
        {
            if (_closed)
                return false;
            if (_process is { } old)
            {
                var live = !old.HasExited;
                if (live && !_expected && !_reported)
                    return true;
                if (live)
                    KillTree(old);
                if (_reported)
                    old.Dispose();
                // The old worker is not the current one any more: a start that fails leaves none, never an object let go.
                _process = null;
            }
            var info = new ProcessStartInfo(options.Worker.FileName)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in options.Worker.Prefix)
                info.ArgumentList.Add(a);
            info.ArgumentList.Add(OptimiseWorker.Word);
            info.ArgumentList.Add(OptimiseWorker.PriorityWord);
            info.ArgumentList.Add(options.WorkerPriority.ToString());
            var p = new Process { StartInfo = info, EnableRaisingEvents = true };
            try
            {
                if (!p.Start())
                    throw new InvalidOperationException("the optimise worker did not start");
            }
            catch
            {
                p.Dispose();
                throw;
            }
            _process = p;
            _expected = false;
            _reported = false;
            Listen(p);
            return true;
        }
    }

    /// <summary>Reads the worker's output and drains its errors on workers. Its own method: the closures (they capture the process)
    /// are made only when a worker starts. Written in <see cref="EnsureStarted"/>, an optimised build would allocate them at the
    /// method's entry, on every call that finds the worker running.</summary>
    private void Listen(Process p)
    {
        _ = Task.Run(() => ReadAsync(p), CancellationToken.None);
        _ = Task.Run(() => DrainAsync(p), CancellationToken.None);
    }

    /// <summary>No worker starts from now (the service's stop has begun): <see cref="EnsureStarted"/> answers false. The worker there
    /// is stays, for the stop to end.</summary>
    public void Close()
    {
        lock (_gate)
            _closed = true;
    }

    /// <summary>Writes a line to the worker; false when there is no worker to take it (gone, or on its way out).</summary>
    public bool Send(string line)
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false } p || _expected || _reported)
                return false;
            try
            {
                p.StandardInput.WriteLine(line);
                p.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                log.LogDebug(ex, "The optimise worker's input is closed.");
                return false;
            }
        }
    }

    /// <summary>Kills the worker (a running job's cancel); its exit is reported as expected.</summary>
    public void Kill()
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false } p)
                return;
            _expected = true;
            KillTree(p);
        }
    }

    /// <summary>Asks the worker to end (its input closes), waits for it <see cref="OptimiseOptions.StopGrace"/>, then kills it. The
    /// stop is over when the worker has gone, or when a killed worker has not gone <see cref="OptimiseOptions.KillWait"/> later (it is
    /// said, and the stop is over without it). One stop per worker: asked again, it asks nothing more and is over when the first is.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_process is not { } p)
                return Task.CompletedTask;
            if (!ReferenceEquals(p, _stopping))
            {
                var running = !p.HasExited;
                if (running)
                    _expected = true;
                var pid = p.Id;
                _stopping = p;
                _stop = StartStop(p, pid, running);
            }
            return _stop;
        }
    }

    /// <summary>The stop of one worker, on a worker. Its own method: the closure (it captures the worker) is made once per worker.
    /// Written in <see cref="StopAsync()"/>, an optimised build would allocate it at the method's entry, on every service step that
    /// asks a lingering worker to stop again.</summary>
    private Task StartStop(Process p, int pid, bool running) => Task.Run(() => StopAsync(p, pid, running), CancellationToken.None);

    private async Task StopAsync(Process p, int pid, bool running)
    {
        if (running)
        {
            try
            {
                await p.StandardInput.WriteLineAsync("{\"t\":\"stop\"}");
                p.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                log.LogDebug(ex, "The optimise worker's input was closed already.");
            }
            using var wait = new CancellationTokenSource(options.StopGrace);
            try
            {
                await p.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                // The worker this stop asked to end: one started since (a job came) is not this stop's.
                KillTree(p);
            }
            catch (InvalidOperationException ex)
            {
                log.LogDebug(ex, "The optimise worker was let go by its reader: it has gone.");
            }
        }
        // A worker killed a moment ago (a cancel) reads as exited while it still holds its files: the stop is over when it has gone.
        if (await GoneAsync(p))
            return;
        log.LogWarning("The optimise worker (pid {Pid}) has not ended {Wait} after it was killed; the node goes on without it.", pid, options.KillWait);
        Stuck?.Invoke(pid);
    }

    /// <summary>Kills a worker's process tree. A worker that has ended, or whose object was let go, is past killing; a kill the
    /// system refuses is logged, and the bounded wait after it decides.</summary>
    private void KillTree(Process p)
    {
        try
        {
            options.Kill(p);
        }
        catch (InvalidOperationException ex)
        {
            log.LogDebug(ex, "The optimise worker had ended already.");
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            log.LogWarning(ex, "The optimise worker could not be killed.");
        }
    }

    /// <summary>Whether the process has gone, its handles closed, within <see cref="OptimiseOptions.KillWait"/>. On Windows HasExited
    /// and WaitForExitAsync answer as soon as the exit code is set, while a killed worker still holds its source and its partial
    /// file; WaitForExit waits on the process itself.</summary>
    private Task<bool> GoneAsync(Process p) => Task.Run(() =>
    {
        try
        {
            return p.WaitForExit(options.KillWait);
        }
        catch (InvalidOperationException)
        {
            // Never started, or let go by whoever was done with it after it had gone: nothing it held waits on this object.
            return true;
        }
    });

    private async Task ReadAsync(Process p)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (WorkerProtocol.Parse(line) is { } e)
                    Event?.Invoke(e);
                else
                    log.LogDebug("The optimise worker said: {Line}", line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log.LogDebug(ex, "The optimise worker's output closed.");
        }
        // Its end is reported once it has gone: the job's partial file is deleted next, and on Windows a file still open is not.
        if (!await GoneAsync(p))
            log.LogWarning("The optimise worker's output closed and it has not ended {Wait} later.", options.KillWait);
        bool current, expected;
        lock (_gate)
        {
            current = ReferenceEquals(p, _process);
            expected = _expected;
            if (current)
                _reported = true;
        }
        if (current)
        {
            Exited?.Invoke(SafeExitCode(p), expected);
            return;
        }
        // Replaced on its way out: nothing else waits on its object, and what it held is free now it has gone.
        p.Dispose();
        LetGo?.Invoke();
    }

    private static int SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private async Task DrainAsync(Process p)
    {
        try
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
                log.LogWarning("optimise worker: {Line}", line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log.LogDebug(ex, "The optimise worker's error output closed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
        }
    }
}
