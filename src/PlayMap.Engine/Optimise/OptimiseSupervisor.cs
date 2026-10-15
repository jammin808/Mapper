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
/// the engine asked for it). A kill is how a running job is cancelled; closing its input is how it is asked to end.</summary>
internal sealed class OptimiseSupervisor(WorkerLaunch launch, ILogger log) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Process? _process;
    private bool _expected;

    /// <summary>A protocol line from the worker, on its reader's thread.</summary>
    public event Action<WorkerEvent>? Event;

    /// <summary>The worker ended: its exit code, and whether the engine stopped or killed it.</summary>
    public event Action<int, bool>? Exited;

    public int? Pid
    {
        get
        {
            lock (_gate)
                return _process is { } p && !p.HasExited ? p.Id : null;
        }
    }

    public bool Running => Pid is not null;

    /// <summary>Starts the worker unless one runs that was not asked to end; a worker being stopped or killed is waited for (a
    /// moment, then killed) and a fresh one started, so a job is never handed to a worker on its way out.</summary>
    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false } live)
            {
                if (!_expected)
                    return;
                try
                {
                    live.Kill(entireProcessTree: true);
                    live.WaitForExit(5000);
                }
                catch (InvalidOperationException ex)
                {
                    log.LogDebug(ex, "The optimise worker had ended already.");
                }
            }
            _process?.Dispose();
            var info = new ProcessStartInfo(launch.FileName)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in launch.Prefix)
                info.ArgumentList.Add(a);
            info.ArgumentList.Add(OptimiseWorker.Word);
            var p = new Process { StartInfo = info, EnableRaisingEvents = true };
            if (!p.Start())
            {
                p.Dispose();
                throw new InvalidOperationException("the optimise worker did not start");
            }
            _process = p;
            _expected = false;
            _ = Task.Run(() => ReadAsync(p), CancellationToken.None);
            _ = Task.Run(() => DrainAsync(p), CancellationToken.None);
        }
    }

    /// <summary>Writes a line to the worker; false when there is no worker to take it (gone, or on its way out).</summary>
    public bool Send(string line)
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false } p || _expected)
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
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException ex)
            {
                log.LogDebug(ex, "The optimise worker had ended already.");
            }
        }
    }

    /// <summary>Asks the worker to end (its input closes) and waits for it a moment, then kills it.</summary>
    public async Task StopAsync()
    {
        Process? p;
        lock (_gate)
        {
            p = _process;
            if (p is null || p.HasExited)
                return;
            _expected = true;
        }
        try
        {
            await p.StandardInput.WriteLineAsync("{\"t\":\"stop\"}");
            p.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log.LogDebug(ex, "The optimise worker's input was closed already.");
        }
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await p.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            Kill();
        }
    }

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
        try
        {
            await p.WaitForExitAsync();
        }
        catch (InvalidOperationException ex)
        {
            log.LogDebug(ex, "The optimise worker was not started.");
        }
        bool expected;
        lock (_gate)
        {
            if (!ReferenceEquals(p, _process))
                return;
            expected = _expected;
        }
        Exited?.Invoke(SafeExitCode(p), expected);
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
