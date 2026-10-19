using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Effects;
using PlayMap.Engine.Optimise;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>How the engine runs its shader worker: the process (this exe in its "shader-worker" role) and the words after the word
/// (a test's fake compiler), the clock its deadlines and backoff run on, the deadline of one compile, the bound of one FX CHECK (the
/// whole check: waiting for the worker, its start, the compile and the end of a worker that did not answer — inside the 10 s the
/// engine gives a stage check, so the check reads the host's own words), the backoff between restarts (doubling from the first to
/// the most), how many stops fail a source, and how long a start and a kill are waited for.</summary>
public sealed record ShaderWorkerOptions
{
    public WorkerLaunch Worker { get; init; } = WorkerLaunch.ThisProcess();

    public ImmutableArray<string> Arguments { get; init; } = [];

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CheckBound { get; init; } = TimeSpan.FromSeconds(8);

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

/// <summary>The engine's side of the shader worker: started on the first source, one source at a time. A source that is not answered
/// — past the compile's deadline, past an FX CHECK's bound, or abandoned by its caller — ends the worker that holds it before the next
/// source is sent, so no source ever waits behind another's compile; running long is not stopping the worker, and counts nothing. A
/// worker that stops while it compiles is started again after a backoff (1 s doubling to 30 s) and handed the source again; a source
/// that stops it <see cref="ShaderWorkerOptions.StopsToFail"/> times fails with words and is answered at once from then on, never sent
/// again until its source changes (a changed source is another hash). That is the host's own memory: it writes no quarantine, so an
/// FX CHECK (a query) changes nothing on the air, and the worker serves Direct3D 12 only. A kind the node's quarantine holds is
/// answered at once too. It is FX CHECK's stage check: the road's stages beside the GL dialects'. The engine never loads a compiler:
/// every native is the worker's.</summary>
public sealed class ShaderWorkerHost : IFxStageCheck, IAsyncDisposable
{
    /// <summary>The stage a reply the engine makes itself names.</summary>
    public const string WorkerStage = "worker";

    /// <summary>The stage a quarantined kind's reply names.</summary>
    public const string QuarantineStage = "quarantine";

    private readonly ShaderWorkerOptions _options;
    private readonly ShaderQuarantine _quarantine;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _stops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failedSources = new(StringComparer.Ordinal);
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

    public ShaderWorkerHost(ShaderWorkerOptions options, ShaderQuarantine quarantine, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(quarantine);
        ArgumentNullException.ThrowIfNull(log);
        _options = options;
        _quarantine = quarantine;
        _log = log;
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

    /// <summary>The node has begun to stop: whatever ends now is the stop's, never a source's.</summary>
    private bool NodeStopping
    {
        get
        {
            lock (_gate)
                return _closed;
        }
    }

    /// <summary>The stops counted against a source (its kind's hash) since it was last answered.</summary>
    internal int StopsOf(string kindHash)
    {
        lock (_gate)
            return _stops.GetValueOrDefault(kindHash);
    }

    /// <summary>FX CHECK's stages, answered within <see cref="ShaderWorkerOptions.CheckBound"/> of the call.</summary>
    public async Task<ImmutableArray<FxStage>> CheckAsync(string name, string fragment, string? vertex, CancellationToken ct) =>
        (await RunAsync(name, fragment, vertex, _options.CheckBound, ct).ConfigureAwait(false)).Stages;

    /// <summary>A source through the worker's road: its reply, or one the engine made when the worker could not answer.</summary>
    public Task<ShaderReply> CompileAsync(string name, string fragment, string? vertex, CancellationToken ct) =>
        RunAsync(name, fragment, vertex, null, ct);

    private async Task<ShaderReply> RunAsync(string name, string fragment, string? vertex, TimeSpan? bound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fragment);
        var hash = FxCatalog.ContentHash(fragment, vertex);
        if (_quarantine.IsQuarantined(hash, out var held))
            return ShaderReply.Refused(0, QuarantineStage, $"{held.Kind} is quarantined on this node: {held.Words} (FX RELEASE lifts it)");
        lock (_gate)
        {
            if (_failedSources.TryGetValue(hash, out var failed))
                return ShaderReply.Refused(0, WorkerStage, failed);
            if (_closed)
                return Stopping(name);
        }
        // The check's bound runs from the call, on the host's clock; the node's stop and the caller end every wait too.
        using var whole = new CancellationTokenSource(bound ?? Timeout.InfiniteTimeSpan, _options.Time);
        CancellationTokenSource life;
        try
        {
            life = CancellationTokenSource.CreateLinkedTokenSource(whole.Token, _stopping.Token, ct);
        }
        catch (ObjectDisposedException)
        {
            return Stopping(name);
        }
        using (life)
            return await HeldAsync(name, fragment, vertex, hash, bound, whole, life, ct).ConfigureAwait(false);
    }

    /// <summary>The source through the one lane, once the bound and the tokens are set.</summary>
    private async Task<ShaderReply> HeldAsync(string name, string fragment, string? vertex, string hash, TimeSpan? bound,
        CancellationTokenSource whole, CancellationTokenSource life, CancellationToken ct)
    {
        try
        {
            await _one.WaitAsync(life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return NodeStopping ? Stopping(name)
                : Fail(ShaderReply.Refused(0, WorkerStage, $"the check ran past {Seconds(bound)} s waiting for the shader worker, busy with another source"));
        }
        catch (ObjectDisposedException)
        {
            return Stopping(name);
        }
        try
        {
            while (true)
            {
                Worker? worker;
                string why;
                try
                {
                    (worker, why) = await StartedAsync(life.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return NodeStopping ? Stopping(name)
                        : Fail(ShaderReply.Refused(0, WorkerStage, $"the check ran past {Seconds(bound)} s and was ended while the shader worker started"));
                }
                if (worker is null)
                    return NodeStopping ? Stopping(name) : Fail(ShaderReply.Refused(0, WorkerStage, why));
                var id = Interlocked.Increment(ref _nextId);
                var reply = worker.Expect(id);
                var line = ShaderWorkerProtocol.Compile(new ShaderRequest(id, hash, name, fragment, vertex));
                using var deadline = new CancellationTokenSource(_options.Deadline, _options.Time);
                using var both = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, life.Token);
                ShaderReply? answered;
                try
                {
                    answered = await SendThenWaitAsync(worker, line, reply, both.Token).WaitAsync(both.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Whatever ended the wait, the worker that holds the source is ended before the lane is let go: the next source
                    // meets a worker with nothing in hand. Running long, or being abandoned, is not a stop: nothing is counted.
                    await EndAsync(worker).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        throw;
                    if (NodeStopping)
                        return Stopping(name);
                    return Fail(ShaderReply.Refused(0, WorkerStage, whole.IsCancellationRequested
                        ? $"the check ran past {Seconds(bound)} s and was ended"
                        : $"the compile ran past {Seconds(_options.Deadline)} s and was ended"));
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
                // The worker went while it held the source (or took no line, and is ended): unless the node is stopping, that is
                // counted against this source, and the worker is started again after the backoff and handed the source again.
                if (!worker.Gone)
                    worker.Kill(_options.KillWait, _log);
                var code = await worker.ExitCodeAsync(_options.KillWait).ConfigureAwait(false);
                if (NodeStopping)
                    return Stopping(name);
                if (Stopped(hash, $"the shader worker stopped while it compiled {name} (exit {code})") is { } failedNow)
                    return failedNow;
                BackingOff = true;
                try
                {
                    await Task.Delay(_backoff, _options.Time, life.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return NodeStopping ? Stopping(name)
                        : Fail(ShaderReply.Refused(0, WorkerStage, $"the check ran past {Seconds(bound)} s while the shader worker was started again"));
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

    /// <summary>Sends the source, then waits for its reply; null when the worker took no line or went without answering.</summary>
    private static async Task<ShaderReply?> SendThenWaitAsync(Worker worker, string line, Task<ShaderReply?> reply, CancellationToken ct)
    {
        if (!await worker.SendAsync(line, ct).ConfigureAwait(false))
            return null;
        return await reply.ConfigureAwait(false);
    }

    /// <summary>Kills a worker and waits for it to go, so what STATE reads after the answer is a node with no worker running.</summary>
    private async Task EndAsync(Worker worker)
    {
        worker.Kill(_options.KillWait, _log);
        await worker.ExitCodeAsync(_options.KillWait).ConfigureAwait(false);
    }

    private ShaderReply Stopping(string name)
    {
        var reply = ShaderReply.Refused(0, WorkerStage, $"the node is stopping: {name} was not compiled");
        lock (_gate)
            _words = reply.Words;
        Changed?.Invoke();
        return reply;
    }

    private static string Seconds(TimeSpan? span) =>
        (span ?? TimeSpan.Zero).TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>Counts a stop of the worker on a source: null below <see cref="ShaderWorkerOptions.StopsToFail"/> (the source is sent
    /// again); at it, the source fails with words and is remembered, so it is answered at once and never sent again.</summary>
    private ShaderReply? Stopped(string hash, string words)
    {
        int stops;
        lock (_gate)
        {
            stops = _stops[hash] = _stops.GetValueOrDefault(hash) + 1;
            _words = words;
        }
        _log.LogWarning("Shader worker: {Words} ({Stops} of {Of}).", words, stops, _options.StopsToFail);
        Changed?.Invoke();
        if (stops < _options.StopsToFail)
            return null;
        var said = $"the source stopped the shader worker {stops} times ({words}): it fails, and is not sent again until it changes";
        lock (_gate)
        {
            _stops.Remove(hash);
            _failedSources[hash] = said;
        }
        return Fail(ShaderReply.Refused(0, WorkerStage, said));
    }

    /// <summary>The worker, started and greeted; null with words when it cannot be. A start the caller abandons ends the worker it
    /// started.</summary>
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
        ShaderHello? hello;
        try
        {
            hello = await worker.HelloAsync(_options.StartWait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await EndAsync(worker).ConfigureAwait(false);
            throw;
        }
        if (hello is null)
        {
            await EndAsync(worker).ConfigureAwait(false);
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

    /// <summary>Ends any compile in hand first (it kills its worker and answers that the node is stopping), then, once no compile
    /// holds the lane, stops an idle worker (the stop line, its input closed, killed if it has not gone in time) and lets the lane go.
    /// The worker's input is written by one compile at a time, never by the stop while a compile writes it.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        var held = await _one.WaitAsync(_options.KillWait * 3, CancellationToken.None).ConfigureAwait(false);
        Worker? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
        }
        if (!held)
        {
            // A compile still holds the lane: its worker is ended here, and the compile lets the lane go itself.
            _log.LogWarning("A shader compile still held the worker {Wait} after the node began to stop.", _options.KillWait * 3);
            worker?.Kill(_options.KillWait, _log);
            return;
        }
        if (worker is not null)
        {
            await worker.StopAsync(_options.KillWait, _log).ConfigureAwait(false);
            worker.Dispose();
        }
        _one.Dispose();
        _stopping.Dispose();
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

        /// <summary>Starts the two readers. Each awaits the worker's output at once, so neither is CPU work handed to the pool: they
        /// run on I/O completions.</summary>
        private void Listen(ILogger log)
        {
            _ = ReadAsync(log);
            _ = DrainAsync(log);
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

        /// <summary>Writes a line to the worker; false when it takes none (it has gone, or was ended mid-write). The caller's token
        /// ends a write the worker does not read.</summary>
        public async Task<bool> SendAsync(string line, CancellationToken ct)
        {
            try
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Writes the stop line if the worker takes it at once; false when it does not.</summary>
        private bool TrySendStop()
        {
            try
            {
                _process.StandardInput.WriteLine(ShaderWorkerProtocol.Stop);
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
            TrySendStop();
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
