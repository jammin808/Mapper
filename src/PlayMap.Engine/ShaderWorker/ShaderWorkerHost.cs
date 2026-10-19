using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Effects;
using PlayMap.Engine.Optimise;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>How the engine runs its shader worker: the process (this exe in its "shader-worker" role) and the words after the word
/// (a test's fake compiler), the clock its deadline and backoff run on, the deadline of one compile, the backoff between restarts
/// (doubling from the first to the most), how many stops fail a source, and how long a start and a kill are waited for.</summary>
public sealed record ShaderWorkerOptions
{
    public WorkerLaunch Worker { get; init; } = WorkerLaunch.ThisProcess();

    public ImmutableArray<string> Arguments { get; init; } = [];

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    public int StopsToFail { get; init; } = 3;

    public TimeSpan StartWait { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>The worker as STATE reads it: whether one runs and its pid, how often it was started again, the last compile's time,
/// how many sources compiled and failed, each native's state from its hello, and the words of its last trouble.</summary>
public sealed record ShaderWorkerFacts(bool Running, int? Pid, int Restarts, long LastMs, int Compiled, int Failed,
    ImmutableArray<NativeState> Natives, string Words);

/// <summary>The engine's side of the shader worker: started on the first source, one source at a time, each answered within the
/// deadline or ended with words (the worker is killed and the next source starts another). A worker that stops while it compiles
/// is started again after a backoff (1 s doubling to 30 s) and handed the source again; a source that stops it
/// <see cref="ShaderWorkerOptions.StopsToFail"/> times fails with words and its kind is quarantined
/// (<see cref="QuarantineReason.CompilerStopped"/>), so it is never sent again until a person releases it or its source changes
/// (a changed source is another kind). A quarantined kind is answered at once, never sent. It is FX CHECK's stage check: the road's
/// stages beside the GL dialects'. The engine never loads a compiler: every native is the worker's.</summary>
public sealed class ShaderWorkerHost : IFxStageCheck, IAsyncDisposable
{
    /// <summary>The stage a reply the engine makes itself names.</summary>
    public const string WorkerStage = "worker";

    /// <summary>The stage a quarantined kind's reply names.</summary>
    public const string QuarantineStage = "quarantine";

    private readonly ShaderWorkerOptions _options;
    private readonly ShaderQuarantine _quarantine;
    private readonly ILogger _log;
    private readonly Func<long> _unixMs;
    private readonly string _build;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _stops = new(StringComparer.Ordinal);
    private Worker? _worker;
    private TimeSpan _backoff;
    private long _nextId;
    private int _started;
    private int _compiled;
    private int _failed;
    private long _lastMs;
    private ImmutableArray<NativeState> _natives = [];
    private string _words = "the shader worker starts with the first effect to compile";
    private bool _closed;

    public ShaderWorkerHost(ShaderWorkerOptions options, ShaderQuarantine quarantine, ILogger log, Func<long> unixMs, string build)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(quarantine);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(unixMs);
        _options = options;
        _quarantine = quarantine;
        _log = log;
        _unixMs = unixMs;
        _build = build;
        _backoff = options.FirstBackoff;
    }

    /// <summary>Raised when a fact STATE reads changes (a start, a stop, a reply).</summary>
    public event Action? Changed;

    /// <summary>A backoff before a restart is running: the worker stopped and is started again when it ends.</summary>
    public bool BackingOff { get; private set; }

    public ShaderWorkerFacts Facts
    {
        get
        {
            lock (_gate)
            {
                var running = _worker is { Gone: false };
                return new ShaderWorkerFacts(running, running ? _worker!.Pid : null, Math.Max(0, _started - 1), _lastMs, _compiled, _failed,
                    _natives, _words);
            }
        }
    }

    public async Task<ImmutableArray<FxStage>> CheckAsync(string name, string fragment, string? vertex, CancellationToken ct) =>
        (await CompileAsync(name, fragment, vertex, ct).ConfigureAwait(false)).Stages;

    /// <summary>A source through the worker's road: its reply, or one the engine made when the worker could not answer.</summary>
    public async Task<ShaderReply> CompileAsync(string name, string fragment, string? vertex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fragment);
        var hash = FxCatalog.ContentHash(fragment, vertex);
        if (_quarantine.IsQuarantined(hash, out var held))
            return ShaderReply.Refused(0, QuarantineStage, $"{held.Kind} is quarantined on this node: {held.Words} (FX RELEASE lifts it)");
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var (worker, why) = await StartedAsync(ct).ConfigureAwait(false);
                if (worker is null)
                    return Fail(ShaderReply.Refused(0, WorkerStage, why));
                var id = Interlocked.Increment(ref _nextId);
                var reply = worker.Expect(id);
                if (!worker.Send(ShaderWorkerProtocol.Compile(new ShaderRequest(id, hash, name, fragment, vertex))))
                {
                    // A worker that takes no line is past use: it is ended, and counted as a stop.
                    worker.Kill(_options.KillWait, _log);
                    reply = Task.FromResult<ShaderReply?>(null);
                }
                using var deadline = new CancellationTokenSource(_options.Deadline, _options.Time);
                using var both = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ct);
                ShaderReply? answered;
                try
                {
                    answered = await reply.WaitAsync(both.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    worker.Kill(_options.KillWait, _log);
                    var seconds = _options.Deadline.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                    return Stopped(hash, name, $"the compile ran past {seconds} s and was ended", retry: false)!;
                }
                if (answered is not null)
                {
                    lock (_gate)
                    {
                        _stops.Remove(hash);
                        _backoff = _options.FirstBackoff;
                        _lastMs = answered.Ms;
                        if (answered.Ok)
                            _compiled++;
                        else
                            _failed++;
                    }
                    Changed?.Invoke();
                    return answered;
                }
                // The worker went while it held the source: started again after the backoff, and the source handed to it again.
                var code = await worker.ExitCodeAsync(_options.KillWait).ConfigureAwait(false);
                if (Stopped(hash, name, $"the shader worker stopped while it compiled {name} (exit {code})", retry: true) is { } failed)
                    return failed;
                BackingOff = true;
                try
                {
                    await Task.Delay(_backoff, _options.Time, ct).ConfigureAwait(false);
                }
                finally
                {
                    BackingOff = false;
                }
                lock (_gate)
                    _backoff = _backoff * 2 > _options.MaxBackoff ? _options.MaxBackoff : _backoff * 2;
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private ShaderReply Fail(ShaderReply reply)
    {
        lock (_gate)
        {
            _failed++;
            _words = reply.Words;
        }
        Changed?.Invoke();
        return reply;
    }

    /// <summary>Counts a stop of the worker on a source. At <see cref="ShaderWorkerOptions.StopsToFail"/> the kind is quarantined and
    /// its reply answered; below it, null when the source is to be sent again (a stop), else its reply (a deadline: the caller
    /// decides whether to try again).</summary>
    private ShaderReply? Stopped(string hash, string name, string words, bool retry)
    {
        int stops;
        lock (_gate)
        {
            stops = _stops[hash] = _stops.GetValueOrDefault(hash) + 1;
            _words = words;
        }
        _log.LogWarning("Shader worker: {Words} ({Stops} of {Of}).", words, stops, _options.StopsToFail);
        Changed?.Invoke();
        if (stops >= _options.StopsToFail)
        {
            lock (_gate)
                _stops.Remove(hash);
            var said = $"the source stopped the shader worker {stops} times ({words}): it is quarantined and never sent again until it is released or changed";
            _quarantine.Add(new QuarantineEntry(hash, name, QuarantineReason.CompilerStopped, said, _unixMs(), _build));
            return Fail(ShaderReply.Refused(0, WorkerStage, said));
        }
        return retry ? null : Fail(ShaderReply.Refused(0, WorkerStage, $"{words} ({stops} of {_options.StopsToFail} before the kind is quarantined)"));
    }

    /// <summary>The worker, started and greeted; null with words when it cannot be.</summary>
    private async Task<(Worker? Worker, string Words)> StartedAsync(CancellationToken ct)
    {
        Worker? worker;
        lock (_gate)
        {
            if (_closed)
                return (null, "the node is stopping: no shader worker starts");
            if (_worker is { Gone: false } live)
                return (live, "");
            _worker?.Dispose();
            _worker = null;
            try
            {
                worker = Worker.Start(_options, _log);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                _words = "the shader worker did not start: " + ex.Message;
                return (null, _words);
            }
            _worker = worker;
            _started++;
        }
        Changed?.Invoke();
        var hello = await worker.HelloAsync(_options.StartWait, ct).ConfigureAwait(false);
        if (hello is null)
        {
            worker.Kill(_options.KillWait, _log);
            lock (_gate)
                _words = "the shader worker did not say hello";
            return (null, "the shader worker did not say hello");
        }
        lock (_gate)
        {
            _natives = hello.Natives;
            var missing = hello.Natives.Where(n => !n.Ok).ToList();
            _words = missing.Count == 0 ? "" : string.Join("; ", missing.Select(n => $"{n.Name}: {n.Words}"));
        }
        Changed?.Invoke();
        return (worker, "");
    }

    public async ValueTask DisposeAsync()
    {
        Worker? worker;
        lock (_gate)
        {
            _closed = true;
            worker = _worker;
            _worker = null;
        }
        if (worker is not null)
        {
            await worker.StopAsync(_options.KillWait, _log).ConfigureAwait(false);
            worker.Dispose();
        }
        _one.Dispose();
    }

    /// <summary>One worker process: its replies by id, its hello and its end, read from its output on a worker thread.</summary>
    private sealed class Worker : IDisposable
    {
        private readonly Process _process;
        private readonly Lock _gate = new();
        private readonly Dictionary<long, TaskCompletionSource<ShaderReply?>> _pending = [];
        private readonly TaskCompletionSource<ShaderHello?> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Worker(Process process)
        {
            _process = process;
            Pid = process.Id;
        }

        public int Pid { get; }

        public bool Gone => _gone.Task.IsCompleted;

        public static Worker Start(ShaderWorkerOptions options, ILogger log)
        {
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
            info.ArgumentList.Add(ShaderWorkerMain.Word);
            foreach (var a in options.Arguments)
                info.ArgumentList.Add(a);
            var p = new Process { StartInfo = info };
            try
            {
                if (!p.Start())
                    throw new InvalidOperationException("the process did not start");
            }
            catch
            {
                p.Dispose();
                throw;
            }
            var worker = new Worker(p);
            worker.Listen(log);
            return worker;
        }

        private void Listen(ILogger log)
        {
            _ = Task.Run(() => ReadAsync(log), CancellationToken.None);
            _ = Task.Run(() => DrainAsync(log), CancellationToken.None);
        }

        public Task<ShaderReply?> Expect(long id)
        {
            var tcs = new TaskCompletionSource<ShaderReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (Gone)
                    return Task.FromResult<ShaderReply?>(null);
                _pending[id] = tcs;
            }
            return tcs.Task;
        }

        public bool Send(string line)
        {
            try
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }

        public async Task<ShaderHello?> HelloAsync(TimeSpan wait, CancellationToken ct)
        {
            try
            {
                return await _hello.Task.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public async Task<int> ExitCodeAsync(TimeSpan wait)
        {
            try
            {
                await _gone.Task.WaitAsync(wait).ConfigureAwait(false);
                using var exited = new CancellationTokenSource(wait);
                await _process.WaitForExitAsync(exited.Token).ConfigureAwait(false);
                return _process.ExitCode;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                return -1;
            }
        }

        public void Kill(TimeSpan wait, ILogger log)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                if (!_process.WaitForExit(wait))
                    log.LogWarning("The shader worker (pid {Pid}) has not ended {Wait} after it was killed.", Pid, wait);
            }
            catch (InvalidOperationException ex)
            {
                log.LogDebug(ex, "The shader worker had ended already.");
            }
            catch (Exception ex) when (ex is Win32Exception or AggregateException)
            {
                log.LogWarning(ex, "The shader worker could not be killed.");
            }
        }

        public async Task StopAsync(TimeSpan wait, ILogger log)
        {
            if (Gone)
                return;
            Send(ShaderWorkerProtocol.Stop);
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                log.LogDebug(ex, "The shader worker's input was closed already.");
            }
            try
            {
                await _gone.Task.WaitAsync(wait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Kill(wait, log);
            }
        }

        private async Task ReadAsync(ILogger log)
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    switch (ShaderWorkerProtocol.Parse(line))
                    {
                        case ShaderHello hello:
                            _hello.TrySetResult(hello);
                            break;
                        case ShaderReply reply:
                            TaskCompletionSource<ShaderReply?>? waiting;
                            lock (_gate)
                            {
                                if (_pending.Remove(reply.Id, out waiting))
                                    waiting.TrySetResult(reply);
                            }
                            break;
                        default:
                            log.LogDebug("The shader worker said: {Line}", line);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                log.LogDebug(ex, "The shader worker's output closed.");
            }
            // Its output closed: it has gone, and nothing it held is answered.
            lock (_gate)
            {
                _gone.TrySetResult();
                foreach (var waiting in _pending.Values)
                    waiting.TrySetResult(null);
                _pending.Clear();
            }
            _hello.TrySetResult(null);
        }

        private async Task DrainAsync(ILogger log)
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    log.LogWarning("shader worker: {Line}", line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                log.LogDebug(ex, "The shader worker's error output closed.");
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
