using PlayMap.Core.Time;

namespace PlayMap.Core.Audio;

/// <summary>The test inputs every node has, so a show rehearses in a silent room: "test:click" (a click on every beat of the show's
/// tempo), "test:sweep", "test:noise" and "test:drums". A capture makes stereo at 48 kHz from the moment it opened, as far as the
/// monotonic clock has come, in 10 ms blocks: paced, a timer delivers every 10 ms; unpaced (tests), nothing moves until
/// <see cref="Pump"/>, so a test on a hand-moved clock hears exactly the samples up to its clock.</summary>
public sealed class SyntheticAudioInputs : IAudioInputs, IDisposable
{
    public const string Prefix = "test:";
    private const int BlockFrames = 480;

    private readonly IMonoClock _mono;
    private readonly Func<double> _bpm;
    private readonly bool _paced;
    private readonly Lock _gate = new();
    private readonly List<Capture> _open = [];

    /// <param name="bpm">The show's tempo now, read when a click or drum input opens.</param>
    public SyntheticAudioInputs(IMonoClock mono, Func<double> bpm, bool paced)
    {
        _mono = mono ?? throw new ArgumentNullException(nameof(mono));
        _bpm = bpm ?? throw new ArgumentNullException(nameof(bpm));
        _paced = paced;
    }

    public IReadOnlyList<AudioInputInfo> Inputs() =>
        [.. TestSignal.Kinds.Select(k => new AudioInputInfo(Prefix + k, "Test " + k, 2, [TestSignal.Rate], "test") { Kind = AudioInputKind.Synthetic })];

    public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(sink);
        var kind = key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? key[Prefix.Length..] : "";
        var signal = TestSignal.Of(kind, Math.Clamp(_bpm(), 20, 999))
            ?? throw new AudioUnavailableException($"'{key}' is not a test input — test:click, test:sweep, test:noise or test:drums");
        var capture = new Capture(this, signal, sink, _mono.NowNs);
        lock (_gate)
            _open.Add(capture);
        if (_paced)
            capture.StartTimer();
        return capture;
    }

    /// <summary>Delivers every open capture's samples up to the clock's now (unpaced inputs; a paced one does this itself).</summary>
    public void Pump()
    {
        Capture[] open;
        lock (_gate)
            open = [.. _open];
        foreach (var c in open)
            c.Deliver();
    }

    public void Dispose()
    {
        Capture[] open;
        lock (_gate)
            open = [.. _open];
        foreach (var c in open)
            c.Dispose();
    }

    private void Closed(Capture c)
    {
        lock (_gate)
            _open.Remove(c);
    }

    private sealed class Capture(SyntheticAudioInputs owner, TestSignal signal, IAudioCaptureSink sink, long startNs) : IAudioCapture
    {
        private readonly Lock _gate = new();
        private readonly float[] _mono = new float[BlockFrames];
        private readonly float[] _stereo = new float[2 * BlockFrames];
        private Timer? _timer;
        private long _made;
        private bool _closed;

        public int SampleRate => TestSignal.Rate;

        public int Channels => 2;

        public void StartTimer() => _timer = new Timer(_ => Deliver(), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));

        public void Deliver()
        {
            lock (_gate)
            {
                if (_closed)
                    return;
                var now = owner._mono.NowNs;
                var due = (long)Math.Floor((now - startNs) * (double)TestSignal.Rate / 1e9);
                // A clock that jumped far (a test, a sleeping machine) is not caught up sample by sample: at most 2 s are made.
                if (due - _made > 2 * TestSignal.Rate)
                    _made = due - 2 * TestSignal.Rate;
                while (_made + BlockFrames <= due)
                {
                    signal.Fill(_mono, _made);
                    for (var i = 0; i < BlockFrames; i++)
                        _stereo[2 * i] = _stereo[2 * i + 1] = _mono[i];
                    _made += BlockFrames;
                    // The block's last sample is due at its own instant: that is when it is "read".
                    var readNs = startNs + (long)Math.Round(_made * 1e9 / TestSignal.Rate);
                    sink.Samples(_stereo, 2, TestSignal.Rate, _made - BlockFrames, readNs);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
                _closed = true;
            _timer?.Dispose();
            owner.Closed(this);
        }
    }
}
