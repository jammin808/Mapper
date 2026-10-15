using PlayMap.Core.Audio;
using PlayMap.Core.Bus;
using PlayMap.Core.Modulation;
using PlayMap.Core.Time;

namespace PlayMap.Engine.Audio;

/// <summary>What the show asks to hear: an input by the show's name for it ("" off, "default", "system", "test:click", a device's
/// name or key, "file:/path.wav"), the channels it takes, the gain, auto-gain, the gate and the input's latency.</summary>
public sealed record ListenSpec(string Input, string Channels, double GainDb, bool AutoGain, double GateDb, double InputLatencyMs);

/// <summary>What the node hears, as observed: the show's name for the input, the key it resolved to, the device's name, the channels
/// taken, the rate it delivers at, the state (off, opening, listening, retrying) with words, the backend, and since when.</summary>
public sealed record ListenState(string Input, string Key, string Name, string Channels, int RateHz, string State, string Words, string Backend,
    long SinceMonoNs)
{
    public static readonly ListenState Off = new("", "", "", "", 0, "off", "", "", 0);
}

/// <summary>The levels and the input as STATE shows them: refreshed by the lane's tick (a few times a second while listening), so a
/// STATE written on any thread reads one consistent set.</summary>
public sealed record AudioFacts(float Level, float Bass, float Mid, float High, float PeakDb, bool Silent, double SilentForS, long FrameLeaderNs)
{
    public static readonly AudioFacts Quiet = new(0, 0, 0, 0, -120, true, 0, long.MinValue);
}

/// <summary>The node's sound and beat: the input hub and its backends, the listening input with its analyser and frame feed, the beat
/// clock (a timeline the lane replaces whole and anyone reads), and two modulation buses — the render thread's own, and one for the
/// lane and STATE behind a lock — reading the same frames and the same clock. Opening an input waits on a device, so it runs off
/// the lane, one open at a time; a lost or missing input is tried again every 5 s.</summary>
public sealed class AudioRuntime : IDisposable
{
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    private readonly IMonoClock _mono;
    private readonly NetworkClock _clock;
    private readonly SemaphoreSlim _listenGate = new(1, 1);
    private readonly Lock _readerGate = new();
    private readonly ModulationBus _reader;
    private readonly List<IAudioInputs> _backends = [];
    private volatile TempoTimeline _tempo;
    private volatile ListenState _state = ListenState.Off;
    private volatile AudioFacts _facts = AudioFacts.Quiet;
    private volatile AudioNodeSettings _node;
    private ListenSpec? _spec;
    private IAudioCapture? _capture;
    private AudioAnalyser? _analyser;
    private long _retryAtMonoNs = long.MaxValue;
    private long _silentSinceMonoNs = long.MinValue;
    private int _disposed;

    /// <param name="paced">True: test inputs deliver on their own timer (a running node). False: they deliver when
    /// <see cref="SyntheticAudioInputs.Pump"/> is called (the lane's tick, or a test on a hand-moved clock).</param>
    public AudioRuntime(IMonoClock mono, NetworkClock clock, bool paced, AudioNodeSettings? node = null)
    {
        _mono = mono ?? throw new ArgumentNullException(nameof(mono));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _node = node ?? AudioNodeSettings.Default;
        _tempo = TempoTimeline.Start(120);
        Paced = paced;
        Synthetic = new SyntheticAudioInputs(mono, () => _tempo.Bpm, paced);
        Hub = new AudioInputHub();
        AddBackend(SyntheticAudioInputs.Prefix, Synthetic);
        RenderBus = new ModulationBus(Feed, () => _tempo);
        _reader = new ModulationBus(Feed, () => _tempo);
    }

    public AudioInputHub Hub { get; }

    public SyntheticAudioInputs Synthetic { get; }

    public AudioFeed Feed { get; } = new();

    public bool Paced { get; }

    /// <summary>The render thread's bus (only the render thread reads it).</summary>
    public ModulationBus RenderBus { get; }

    /// <summary>The beat clock now (the lane replaces it whole; anyone reads it).</summary>
    public TempoTimeline Tempo
    {
        get => _tempo;
        set => _tempo = value ?? throw new ArgumentNullException(nameof(value));
    }

    public ListenState State => _state;

    public AudioFacts Facts => _facts;

    /// <summary>This machine's own choices (AUDIO LISTEN … HERE), kept beside the node's settings.</summary>
    public AudioNodeSettings Node
    {
        get => _node;
        set => _node = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Saves the machine's own choices to the node's settings file (the host's; none in a bare engine).</summary>
    public Action<AudioNodeSettings>? SaveNode { get; init; }

    /// <summary>The effects' parameter ranges, for binding a slot's own numbers (null: a slot binds its mix).</summary>
    public IFxParamRanges? FxRanges { get; set; }

    /// <summary>The analyser of the open input (null while none is open).</summary>
    public AudioAnalyser? Analyser => _analyser;

    /// <summary>Adds a backend for the keys starting with <paramref name="prefix"/>.</summary>
    public AudioRuntime AddBackend(string prefix, IAudioInputs backend)
    {
        Hub.Add(prefix, backend);
        if (!_backends.Contains(backend))
            _backends.Add(backend);
        return this;
    }

    /// <summary>Each backend's name and its words about what it lists.</summary>
    public IReadOnlyList<(string Backend, string Words)> BackendWords() =>
        [.. _backends.OfType<IAudioInputsWords>().Select(b => (b.Backend, b.Words))];

    /// <summary>Reads the lane's bus (any thread but the render thread; one reader at a time).</summary>
    public T Read<T>(Func<ModulationBus, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_readerGate)
            return read(_reader);
    }

    /// <summary>True when what the show asks differs from what is open, or a retry is due.</summary>
    public bool Due(ListenSpec spec, long monoNowNs) =>
        Volatile.Read(ref _disposed) == 0
        && (_spec != spec || (_state.State == "retrying" && monoNowNs >= Interlocked.Read(ref _retryAtMonoNs)));

    /// <summary>Makes the node hear what the show asks: the same input keeps its open device (only the gain, the gate and the latency
    /// move); another input closes the old one and opens the new one. A failure is the state "retrying" with its words.</summary>
    public async Task<ListenState> ListenAsync(ListenSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _listenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var current = _spec;
            if (current is not null && _analyser is { } analyser && _state.State == "listening"
                && current.Input == spec.Input && current.Channels == spec.Channels)
            {
                analyser.GainDb = spec.GainDb;
                analyser.GateDb = spec.GateDb;
                analyser.AutoGain = spec.AutoGain;
                if (Feed.Listener is { } l)
                    l.InputLatencyNs = LatencyNs(spec);
                _spec = spec;
                return _state;
            }
            Close();
            _spec = spec;
            if (spec.Input.Length == 0)
                return _state = ListenState.Off;
            if (!ChannelPick.TryParse(spec.Channels, out var pick, out var pickWhy))
                return Retry(spec, "", pickWhy);
            var inputs = Hub.Inputs();
            var info = Hub.Resolve(spec.Input, inputs)
                ?? (spec.Input.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    ? new AudioInputInfo(spec.Input, Path.GetFileName(spec.Input[5..]), 2, [], "file") { Kind = AudioInputKind.File }
                    : null);
            if (info is null)
                return Retry(spec, "", $"no input '{spec.Input}' on this node — AUDIO INPUTS lists them{BackendNote()}");
            _state = new ListenState(spec.Input, info.Key, info.Name, pick.ToString(), 0, "opening", "", info.Backend, _mono.NowNs);
            var next = new AudioAnalyser { GainDb = spec.GainDb, GateDb = spec.GateDb, AutoGain = spec.AutoGain };
            var listener = new AudioListener(next, Feed.Ring, _clock.ToLeader) { InputLatencyNs = LatencyNs(spec) };
            Feed.Ring.Clear();
            IAudioCapture capture;
            try
            {
                var key = pick.Kind == ChannelPickKind.All ? info.Key : $"{info.Key}@{pick}";
                capture = await Task.Run(() => Hub.Open(key, AudioAnalyser.SampleRate, listener), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Retry(spec, info.Key, e.Message);
            }
            _capture = capture;
            _analyser = next;
            Feed.Listener = listener;
            _silentSinceMonoNs = long.MinValue;
            return _state = new ListenState(spec.Input, info.Key, info.Name, pick.ToString(), capture.SampleRate, "listening", "", info.Backend,
                _mono.NowNs);
        }
        finally
        {
            _listenGate.Release();
        }
    }

    /// <summary>The lane's look at the input (each tick): a listener whose device stopped is closed and tried again in 5 s; the
    /// facts STATE shows are refreshed. True when STATE should be written again.</summary>
    /// <param name="force">Take the newest frame's numbers whatever moved (a test's fixed instant).</param>
    public bool Observe(long monoNowNs, long leaderNowNs, AnalysisFrame scratch, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(scratch);
        var changed = false;
        if (Volatile.Read(ref _disposed) == 0 && _state.State == "listening" && Feed.Listener is { FaultWords.Length: > 0 } faulted
            && _listenGate.Wait(0))
        {
            try
            {
                var words = faulted.FaultWords;
                Close();
                Retry(_spec!, _state.Key, words);
                changed = true;
            }
            finally
            {
                _listenGate.Release();
            }
        }
        var before = _facts;
        AudioFacts now;
        if (!Feed.Listening)
        {
            _silentSinceMonoNs = long.MinValue;
            now = AudioFacts.Quiet;
        }
        else
        {
            // An input that delivers nothing (a device that stalled without a fault) is silent like one that delivers silence, and
            // its silence is counted the same way.
            var fresh = Feed.Ring.TryReadLatest(scratch) && leaderNowNs - scratch.LeaderNs <= ModulationBus.StaleNs;
            var silent = !fresh || scratch.Silent;
            if (!silent)
                _silentSinceMonoNs = long.MinValue;
            else if (_silentSinceMonoNs == long.MinValue)
                _silentSinceMonoNs = monoNowNs;
            var silentFor = _silentSinceMonoNs == long.MinValue ? 0 : (monoNowNs - _silentSinceMonoNs) / 1e9;
            now = fresh
                ? new AudioFacts(scratch.Level, scratch.Bass, scratch.Mid, scratch.High, scratch.PeakDb, scratch.Silent, silentFor, scratch.LeaderNs)
                : AudioFacts.Quiet with { SilentForS = silentFor };
        }
        // Levels move every hop; STATE takes them when they moved visibly, at most four times a second.
        var moved = now.Silent != before.Silent || Math.Abs(now.SilentForS - before.SilentForS) >= 1
            || MathF.Abs(now.Level - before.Level) > 0.05f || MathF.Abs(now.Bass - before.Bass) > 0.05f
            || MathF.Abs(now.Mid - before.Mid) > 0.05f || MathF.Abs(now.High - before.High) > 0.05f;
        var due = before.FrameLeaderNs == long.MinValue || now.FrameLeaderNs == long.MinValue
            || now.FrameLeaderNs - before.FrameLeaderNs >= 250_000_000;
        if (force ? now != before : moved && due)
        {
            _facts = now;
            changed = true;
        }
        return changed;
    }

    /// <summary>Closes the input and every backend; an open under way finishes first.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _listenGate.Wait();
        try
        {
            Close();
            Hub.Dispose();
            Synthetic.Dispose();
        }
        finally
        {
            _listenGate.Release();
        }
        _listenGate.Dispose();
    }

    private ListenState Retry(ListenSpec spec, string key, string words)
    {
        Interlocked.Exchange(ref _retryAtMonoNs, _mono.NowNs + (long)RetryEvery.TotalMilliseconds * 1_000_000);
        return _state = new ListenState(spec.Input, key, "", spec.Channels, 0, "retrying", words, "", _mono.NowNs);
    }

    private string BackendNote()
    {
        var notes = BackendWords().Where(b => b.Words.Length > 0).Select(b => $"{b.Backend}: {b.Words}").ToList();
        return notes.Count == 0 ? "" : " (" + string.Join("; ", notes) + ")";
    }

    private long LatencyNs(ListenSpec spec) => spec.InputLatencyMs > 0 ? (long)(spec.InputLatencyMs * 1e6) : 0;

    private void Close()
    {
        Feed.Listener = null;
        _capture?.Dispose();
        _capture = null;
        _analyser = null;
        _facts = AudioFacts.Quiet;
    }
}
