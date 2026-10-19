using System.Diagnostics;

namespace PlayMap.App.Link;

/// <summary>How long to wait before restarting an engine that stopped: 0.5 s, then doubling to a 10 s
/// ceiling; an engine that ran for a healthy while (30 s) starts the ladder again, so one crash an hour
/// restarts in half a second and a crash loop does not spin the machine.</summary>
public sealed class RestartBackoff(TimeSpan? first = null, TimeSpan? ceiling = null, TimeSpan? healthy = null)
{
    public TimeSpan First { get; } = first ?? TimeSpan.FromMilliseconds(500);

    public TimeSpan Ceiling { get; } = ceiling ?? TimeSpan.FromSeconds(10);

    public TimeSpan Healthy { get; } = healthy ?? TimeSpan.FromSeconds(30);

    private int _failures;

    /// <summary>The delay before the next start, given how long the last run lasted.</summary>
    public TimeSpan Next(TimeSpan lastUptime)
    {
        if (lastUptime >= Healthy)
            _failures = 0;
        var ms = First.TotalMilliseconds * Math.Pow(2, Math.Min(_failures, 16));
        _failures++;
        return TimeSpan.FromMilliseconds(Math.Min(ms, Ceiling.TotalMilliseconds));
    }

    public int Failures => _failures;

    public void Reset() => _failures = 0;
}

/// <summary>A started engine process as the supervisor sees it (the seam the tests fake).</summary>
public interface IEngineProcess : IDisposable
{
    int Id { get; }

    Task<int> WaitForExitAsync(CancellationToken ct);
}

/// <summary>Starts this machine's engine ("PlayMap engine", the same exe in its own process) and restarts it
/// when it stops, with <see cref="RestartBackoff"/>. Closing the desk stops the watching, never the engine:
/// a desk that closes or faults never blanks a projector, and the next desk re-attaches on loopback. An
/// engine that exits with 0 was asked to stop and is not restarted.</summary>
public sealed class EngineSupervisor : IAsyncDisposable
{
    private readonly Func<IEngineProcess?> _start;
    private readonly TimeProvider _time;
    private readonly RestartBackoff _backoff;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<bool> _supervised;
    private Task _loop = Task.CompletedTask;

    public EngineSupervisor(Func<IEngineProcess?>? start = null, TimeProvider? time = null, RestartBackoff? backoff = null,
        Func<bool>? supervisedMachine = null)
    {
        _start = start ?? StartOwnEngine;
        _supervised = supervisedMachine ?? IsSupervisedMachine;
        _time = time ?? TimeProvider.System;
        _backoff = backoff ?? new RestartBackoff();
    }

    /// <summary>The watch loop (it ends when the engine stops on request or the desk stops watching).</summary>
    public Task Watching => _loop;

    /// <summary>What the supervisor did, in words for the status line ("engine restarted after 1 s").</summary>
    public event Action<string>? Note;

    /// <summary>Raised after every (re)start with the process id.</summary>
    public event Action<int>? Started;

    public int Starts { get; private set; }

    public bool IsWatching => !_loop.IsCompleted;

    public void Start()
    {
        if (IsWatching)
            return;
        // An installed, supervised machine's engine is the supervisor's: the desk attaches to it on loopback and never starts its own.
        if (_supervised())
        {
            Note?.Invoke("This machine's engine is kept by its supervisor — the desk attaches to it.");
            return;
        }
        _loop = WatchAsync(_stop.Token);
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var process = _start();
            if (process is null)
            {
                Note?.Invoke("The engine could not be started — the desk keeps trying.");
                await Task.Delay(_backoff.Next(TimeSpan.Zero), _time, ct);
                continue;
            }
            Starts++;
            Started?.Invoke(process.Id);
            var began = _time.GetTimestamp();
            int code;
            try
            {
                code = await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // The desk stopped watching; the engine keeps running on its own.
                return;
            }
            if (code == 0)
            {
                Note?.Invoke("The engine stopped (asked to) — start it again from Connect.");
                return;
            }
            var delay = _backoff.Next(_time.GetElapsedTime(began));
            Note?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The engine stopped (code {code}) — restarting in {delay.TotalSeconds:0.#} s."));
            await Task.Delay(delay, _time, ct);
        }
    }

    /// <summary>"PlayMap engine --desk-pid &lt;this desk&gt;" from the running exe's own full path (never resolved through
    /// PATH). The engine notes in its journal when this desk goes, and plays on.</summary>
    private static IEngineProcess? StartOwnEngine()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return null;
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        info.ArgumentList.Add("engine");
        info.ArgumentList.Add("--desk-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var p = Process.Start(info);
            return p is null ? null : new OsEngineProcess(p);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Trace.WriteLine("PlayMap: the engine did not start: " + ex.Message);
            return null;
        }
    }

    /// <summary>Whether the installer made this a supervised machine (HKLM\SOFTWARE\PlayMap, Supervised = 1).</summary>
    public static bool IsSupervisedMachine()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PlayMap");
        return key?.GetValue("Supervised") is int supervised && supervised != 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Stopping the watch ends the loop by cancellation.
        }
        _stop.Dispose();
    }

    private sealed class OsEngineProcess(Process process) : IEngineProcess
    {
        public int Id => process.Id;

        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }

        public void Dispose() => process.Dispose();
    }
}
