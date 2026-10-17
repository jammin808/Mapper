using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PlayMap.Net;

namespace PlayMap.Engine.Host;

/// <summary>"PlayMap engine [headless] …": the node process. The exe's Main hands it the command line when the first
/// word is "engine"; it runs the node until it is asked to stop (Ctrl+C, SIGTERM, the service manager) and answers the
/// process's exit code. The node outlives its desk (docs/ARCHITECTURE.md §1: a desk that closes never blanks a
/// projector) — a desk that leaves is noted in the journal and the show plays on. The engine has no console: what it
/// says goes to playmap.engine.log in the node's folder (bad arguments included), and the show's record to its
/// journal.</summary>
public static partial class EngineEntry
{
    /// <summary>Asked to stop, and stopped cleanly.</summary>
    public const int Stopped = 0;

    /// <summary>Something no part of the node could handle; the log says what.</summary>
    public const int Crashed = 1;

    /// <summary>The command line is not one the engine reads; the log says why and what it reads.</summary>
    public const int BadArguments = 2;

    /// <summary>Neither the line wire nor the web port could listen (a port in use, a bind address that is not this
    /// machine's), so nothing could reach the node; the log names both reasons.</summary>
    public const int BindRefused = 3;

    public const string Usage =
        "PlayMap engine [headless] [--home <folder>] [--project <file>] [--name <node name>] [--http <port>] [--wire <port>] [--no-recover] [--desk-pid <pid>]";

    /// <summary>Runs the node with the exe's standard parts (<see cref="EngineParts.Standard"/>) until it is asked to
    /// stop, and answers the exit code. Blocks the calling thread for the node's life.</summary>
    public static int Run(string[] args) => Run(args, EngineParts.Standard());

    /// <summary>The process's entry points beside the desk: "engine [headless] …" runs the node, and each package's own word (an
    /// optimise worker) runs its process. True with the exit code when the first word is one of them; false leaves the command
    /// line to the caller (the desk, or the node host's usage). Every exe that can be a node calls it first.</summary>
    public static bool TryDispatch(string[] args, out int exit)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length > 0 && string.Equals(args[0], "engine", StringComparison.OrdinalIgnoreCase))
        {
            exit = Run(args);
            return true;
        }
        int? dispatched = null;
        DispatchOptimise(args, ref dispatched);
        exit = dispatched ?? BadArguments;
        return dispatched is not null;
    }

    /// <summary>wp2-optimise's worker process (Host/EngineEntry.Optimise.cs).</summary>
    static partial void DispatchOptimise(string[] args, ref int? exit);

    /// <summary>Runs the node with these parts.</summary>
    public static int Run(string[] args, EngineParts parts)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(parts);
        using var signals = new StopSignals();
        using var finished = new ManualResetEventSlim(false);
        var code = Crashed;
        _ = Task.Run(async () =>
        {
            try
            {
                code = await RunAsync(args, parts, signals.Token);
            }
            finally
            {
                finished.Set();
            }
        }, CancellationToken.None);
        // The process's first thread holds here for the node's life; the node runs on the pool and its own threads.
        finished.Wait();
        return code;
    }

    /// <summary>The node's life: parse, pick the home, log, start, run until <paramref name="stop"/>, stop.</summary>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, EngineParts parts, CancellationToken stop, ILoggerProvider? extraLog = null)
    {
        var options = EngineOptions.Parse(args, out var error);
        if (error is null && args.Contains("--home") && options.Home is null)
            error = "--home takes a folder";
        if (error is null && args.Contains("--project") && options.Project is null)
            error = "--project takes a file";
        if (error is null && args.Contains("--name") && string.IsNullOrWhiteSpace(options.Name))
            error = "--name takes a name";
        NodeHome home;
        try
        {
            home = options.Home is { Length: > 0 } folder ? new NodeHome(folder) : NodeHome.Default();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // No folder to log into: the default home hears why, if it can be made.
            home = NodeHome.Default();
            error ??= $"the node's folder {options.Home} cannot be used: {ex.Message}";
        }

        using var file = NodeLogProvider.For(home);
        using var logs = LoggerFactory.Create(b =>
        {
            b.AddProvider(file);
            if (extraLog is not null)
                b.AddProvider(extraLog);
            b.SetMinimumLevel(LogLevel.Information);
        });
        var log = logs.CreateLogger("PlayMap.Engine");
        // The build rides on the start line and on every crash line, as on the first line of every file the log starts: a crash's
        // lines may be read from a file that holds no start line, or be all that is left of one.
        var build = Core.BuildInfo.Current.Display;
        if (error is not null)
        {
            log.LogError("{Build}: the engine did not start: {Error}. It reads: {Usage}", build, error, Usage);
            return BadArguments;
        }

        void OnUnhandled(object? sender, UnhandledExceptionEventArgs e) =>
            log.LogCritical(e.ExceptionObject as Exception, "The engine process is ending on a fault nothing caught ({Build}).", build);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        try
        {
            log.LogInformation("{Build}: the engine starts{Headless} in {Home} (process {Pid}).", build, options.Headless ? " headless" : "", home.Folder,
                Environment.ProcessId);
            await using var host = new EngineHost(options, home, new EngineHostOptions { Parts = parts, Logs = logs });
            try
            {
                await host.StartAsync(stop);
            }
            catch (BindRefusedException ex)
            {
                log.LogError(ex, "The engine did not start: {Why}", ex.Message);
                return BindRefused;
            }
            catch (OperationCanceledException ex) when (stop.IsCancellationRequested)
            {
                log.LogInformation(ex, "The engine was asked to stop while it started.");
                return Stopped;
            }
            var desk = options.DeskPid is { } pid ? WatchDeskAsync(pid, host.Engine, log, stop) : Task.CompletedTask;
            await Waits.DelayAsync(Timeout.InfiniteTimeSpan, stop);
            log.LogInformation("The engine was asked to stop.");
            await desk;
            return Stopped;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "The engine stopped on a fault nothing else caught ({Build}).", build);
            return Crashed;
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        }
    }

    /// <summary>Notes when the desk that started this engine goes away. The engine keeps playing: a desk that closes or
    /// faults never blanks a projector, and a desk that comes back attaches again.</summary>
    private static async Task WatchDeskAsync(int pid, EngineCore engine, ILogger log, CancellationToken stop)
    {
        Process desk;
        try
        {
            desk = Process.GetProcessById(pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            log.LogInformation(ex, "The desk (process {Pid}) had gone before the engine started; the engine runs on its own.", pid);
            return;
        }
        using (desk)
        {
            try
            {
                await desk.WaitForExitAsync(stop);
            }
            catch (OperationCanceledException)
            {
                return;   // the engine is stopping first
            }
        }
        engine.Journal.Add("node", "the desk closed; the engine keeps playing and the desk attaches again when it is back");
        log.LogInformation("The desk (process {Pid}) closed; the engine keeps playing.", pid);
    }

    /// <summary>Ctrl+C, SIGTERM (a service manager's stop) and, on Unix, SIGQUIT ask the node to stop; the default
    /// handling — ending the process at once — is cancelled so the node can close in order.</summary>
    private sealed class StopSignals : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly List<PosixSignalRegistration> _registrations = [];

        public StopSignals()
        {
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
            {
                try
                {
                    _registrations.Add(PosixSignalRegistration.Create(signal, Asked));
                }
                catch (PlatformNotSupportedException)
                {
                    // A platform without that signal stops by the others.
                }
            }
        }

        public CancellationToken Token => _stop.Token;

        private void Asked(PosixSignalContext context)
        {
            context.Cancel = true;
            _stop.Cancel();
        }

        public void Dispose()
        {
            foreach (var r in _registrations)
                r.Dispose();
            _stop.Dispose();
        }
    }
}
