using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using PlayMap.Net;

namespace PlayMap.Engine.Host;

/// <summary>"PlayMap engine [headless] …": the node process. The exe's Main hands it the command line when the first
/// word is "engine"; it runs the node until it is asked to stop (Ctrl+C, SIGTERM, the service manager, and for an engine
/// the supervisor started, the supervisor's stop event) and answers the process's exit code. The node outlives its desk
/// (docs/ARCHITECTURE.md §1: a desk that closes never blanks a projector) — a desk that leaves is noted in the journal and
/// the show plays on. The engine has no console: what it says goes to playmap.engine.log in the node's folder (bad
/// arguments included), and the show's record to its journal.</summary>
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
        "PlayMap engine [headless] [--home <folder>] [--project <file>] [--name <node name>] [--http <port>] [--wire <port>] [--no-recover] [--desk-pid <pid>] [--supervised]";

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
        DispatchObserver(args, ref dispatched);
        DispatchShaderWorker(args, ref dispatched);
        exit = dispatched ?? BadArguments;
        return dispatched is not null;
    }

    /// <summary>wp2-optimise's worker process (Host/EngineEntry.Optimise.cs).</summary>
    static partial void DispatchOptimise(string[] args, ref int? exit);

    /// <summary>wp3-stability's air observer process (Host/EngineEntry.Observer.cs).</summary>
    static partial void DispatchObserver(string[] args, ref int? exit);

    /// <summary>wp3-shaders' shader worker process (Host/EngineEntry.ShaderWorker.cs).</summary>
    static partial void DispatchShaderWorker(string[] args, ref int? exit);

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

    /// <summary>Opens the event a supervisor sets to stop the engine it started: the handle to wait on, or null with why there is
    /// none.</summary>
    internal delegate WaitHandle? StopEventOpener(out string why);

    /// <summary>The node's life: parse, pick the home, log, start, run until <paramref name="stop"/> or, for an engine the supervisor
    /// started (<c>--supervised</c>), until its stop event is set — opened by <paramref name="supervisorStop"/>, this machine's
    /// (<see cref="OpenThisMachinesStopEvent"/>) when none is given — then stop: either way the same stop, the doors closed in order,
    /// the recovery record written and the journal flushed.</summary>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, EngineParts parts, CancellationToken stop, ILoggerProvider? extraLog = null,
        StopEventOpener? supervisorStop = null)
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
            using var supervisor = options.Supervised ? StopEvent.Open(supervisorStop ?? OpenThisMachinesStopEvent, log) : null;
            using var asked = supervisor is null ? null : CancellationTokenSource.CreateLinkedTokenSource(stop, supervisor.Token);
            var running = asked?.Token ?? stop;
            await using var host = new EngineHost(options, home, new EngineHostOptions
            {
                Parts = parts,
                Logs = logs,
                SupervisorFolder = SupervisorFolderFor(options),
            });
            try
            {
                await host.StartAsync(running);
            }
            catch (BindRefusedException ex)
            {
                log.LogError(ex, "The engine did not start: {Why}", ex.Message);
                return BindRefused;
            }
            catch (OperationCanceledException ex) when (running.IsCancellationRequested)
            {
                log.LogInformation(ex, "The engine was asked to stop while it started{By}.", supervisor is { WasSet: true } ? " (by the supervisor)" : "");
                return Stopped;
            }
            var desk = options.DeskPid is { } pid ? WatchDeskAsync(pid, host.Engine, log, running) : Task.CompletedTask;
            await Waits.DelayAsync(Timeout.InfiniteTimeSpan, running);
            if (supervisor is { WasSet: true })
            {
                host.Engine.Journal.Add("node", "the supervisor asked the engine to stop");
                log.LogInformation("The engine was asked to stop by the supervisor.");
            }
            else
            {
                log.LogInformation("The engine was asked to stop.");
            }
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

    /// <summary>The supervisor's folder a run writes its pid record into: the machine's for an engine the supervisor started, none for
    /// any other run, so a second engine on a supervised machine never takes the supervised one's record.</summary>
    internal static string? SupervisorFolderFor(EngineOptions options) =>
        options.Supervised ? Core.Machine.EnginePidRecord.MachineFolder() : null;

    /// <summary>This machine's stop event for the engine its supervisor started (<see cref="Core.Machine.SupervisorNames.StopEvent"/>);
    /// none off Windows, which has no supervisor.</summary>
    internal static WaitHandle? OpenThisMachinesStopEvent(out string why)
    {
        if (OperatingSystem.IsWindows())
            return OpenStopEvent(Core.Machine.SupervisorNames.StopEvent, out why);
        why = "only Windows has a supervisor's stop event";
        return null;
    }

    /// <summary>The named event <paramref name="name"/>, opened with Synchronize alone — the one right the supervisor's ACL gives the
    /// show user, enough to wait on it and never to set or clear it — or null with why it could not be.</summary>
    [SupportedOSPlatform("windows")]
    internal static WaitHandle? OpenStopEvent(string name, out string why)
    {
        try
        {
            if (EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Synchronize, out var opened))
            {
                why = "";
                return opened;
            }
            why = $"{name} does not exist";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            why = $"{name} could not be opened: {ex.Message}";
        }
        return null;
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

    /// <summary>The supervisor's stop event, waited on for the engine's life: its token is cancelled once the event is set, and the wait
    /// on it ends when it is disposed.</summary>
    private sealed class StopEvent : IDisposable
    {
        private readonly WaitHandle _event;
        private readonly CancellationTokenSource _set = new();
        private readonly RegisteredWaitHandle _wait;

        private StopEvent(WaitHandle stopEvent)
        {
            _event = stopEvent;
            _wait = ThreadPool.RegisterWaitForSingleObject(stopEvent, Set, null, Timeout.Infinite, executeOnlyOnce: true);
        }

        /// <summary>Cancelled once the supervisor has set the event.</summary>
        public CancellationToken Token => _set.Token;

        /// <summary>Whether the supervisor has set the event.</summary>
        public bool WasSet => _set.IsCancellationRequested;

        /// <summary>The event <paramref name="open"/> gives, waited on; null, said in the log, when it gives none: the engine then stops
        /// by its signals alone, and the supervisor's stop ends in termination after its grace.</summary>
        public static StopEvent? Open(StopEventOpener open, ILogger log)
        {
            if (open(out var why) is not { } opened)
            {
                log.LogWarning("The supervisor's stop event is not waited on ({Why}): its stop ends this engine only by termination.", why);
                return null;
            }
            log.LogInformation("The engine waits on the supervisor's stop event.");
            return new StopEvent(opened);
        }

        private void Set(object? state, bool timedOut)
        {
            try
            {
                _set.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The engine had stopped already: there is nothing left to stop.
            }
        }

        public void Dispose()
        {
            _wait.Unregister(null);
            _set.Dispose();
            _event.Dispose();
        }
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
