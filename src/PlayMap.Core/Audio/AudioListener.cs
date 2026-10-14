namespace PlayMap.Core.Audio;

/// <summary>One listening input from capture to frames: its samples (any rate, any channel count; a pair or more are averaged to
/// mono) are brought to 48 kHz, analysed, stamped in leader time and kept in the frame ring; the last 2 s of mono samples are kept
/// for the waveform. The stamp of a sample is fitted from the capture's own count: the offset between "sample i's time at the
/// device rate" and "the monotonic instant its block was read" is taken at its lower envelope over the last 2 s (a block read late
/// says nothing about when its sound arrived; the earliest reads do), minus the input's latency, mapped to leader time. Called on
/// one capture thread; nothing is allocated per block once warm.</summary>
public sealed class AudioListener : IAudioCaptureSink
{
    private const int WaveSeconds = 2;
    private const int FitBlocks = 256;

    private readonly AudioAnalyser _analyser;
    private readonly AnalysisRing _ring;
    private readonly Func<long, long> _toLeader;
    private readonly Stamper _stamper;
    private readonly float[] _wave = new float[AudioAnalyser.SampleRate * WaveSeconds];
    private readonly long[] _fitOffsets = new long[FitBlocks];
    private readonly long[] _fitAt = new long[FitBlocks];
    private float[] _mono = [];
    private int _fitCount;
    private int _fitNext;
    private long _offsetNs = long.MinValue;
    private long _waveEnd;
    private long _inputSamples;
    private int _rate;
    private double _resamplePos;
    private float _resampleLast;
    private long _lastReadMonoNs = long.MinValue;
    private string _faultWords = "";

    /// <param name="toLeader">Monotonic ns → leader ns (the node's clock).</param>
    public AudioListener(AudioAnalyser analyser, AnalysisRing ring, Func<long, long> toLeader)
    {
        _analyser = analyser ?? throw new ArgumentNullException(nameof(analyser));
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _toLeader = toLeader ?? throw new ArgumentNullException(nameof(toLeader));
        _stamper = new Stamper(this);
    }

    /// <summary>The input's latency taken off every stamp (the device's buffer before the read), in ns.</summary>
    public long InputLatencyNs { get; set; }

    /// <summary>The rate the input delivers at (0 before the first block).</summary>
    public int InputRate => Volatile.Read(ref _rate);

    /// <summary>The monotonic instant of the last block (long.MinValue before the first).</summary>
    public long LastReadMonoNs => Volatile.Read(ref _lastReadMonoNs);

    /// <summary>Why the input stopped, or "" while it delivers.</summary>
    public string FaultWords => Volatile.Read(ref _faultWords);

    public void Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs)
    {
        if (channels <= 0 || sampleRate <= 0)
            return;
        var frames = interleaved.Length / channels;
        if (frames == 0)
            return;
        if (_mono.Length < frames)
            _mono = new float[Math.Max(frames, 4096)];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += interleaved[f * channels + c];
            _mono[f] = sum / channels;
        }
        if (sampleRate != _rate)
        {
            Volatile.Write(ref _rate, sampleRate);
            _resamplePos = 0;
        }
        _inputSamples = firstSampleIndex + frames;
        Fit(readMonoNs, sampleRate);
        Volatile.Write(ref _lastReadMonoNs, readMonoNs);
        Volatile.Write(ref _faultWords, "");
        if (sampleRate == AudioAnalyser.SampleRate)
            Take(_mono.AsSpan(0, frames));
        else
            Resample(_mono.AsSpan(0, frames), sampleRate);
    }

    public void Faulted(string words) => Volatile.Write(ref _faultWords, string.IsNullOrWhiteSpace(words) ? "the input stopped" : words);

    /// <summary>The leader instant of an output sample (48 kHz count), on the current fit.</summary>
    public long LeaderAtSample(long sample48k)
    {
        if (_offsetNs == long.MinValue || _rate == 0)
            return long.MinValue;
        // Output sample s stands at input sample s·rate/48000; its monotonic instant is that sample's time plus the offset.
        var inputTimeNs = (long)Math.Round(sample48k * 1e9 / AudioAnalyser.SampleRate);
        return _toLeader(inputTimeNs + _offsetNs) - InputLatencyNs;
    }

    /// <summary>Copies the <paramref name="samples"/> mono samples that end at <paramref name="leaderNs"/>; false when they are not
    /// held (not yet heard, or older than 2 s).</summary>
    public bool TryWave(long leaderNs, Span<float> into)
    {
        var end = Volatile.Read(ref _waveEnd);
        if (end == 0 || into.Length > _wave.Length / 2)
            return false;
        var endLeader = LeaderAtSample(end);
        if (endLeader == long.MinValue)
            return false;
        var last = end - (long)Math.Round((endLeader - leaderNs) * AudioAnalyser.SampleRate / 1e9);
        if (last > end || last - into.Length < end - _wave.Length / 2)
            return false;
        for (var i = 0; i < into.Length; i++)
            into[i] = _wave[(int)((last - into.Length + i) % _wave.Length)];
        return Volatile.Read(ref _waveEnd) - (last - into.Length) < _wave.Length;
    }

    private void Fit(long readMonoNs, int rate)
    {
        var endTimeNs = (long)Math.Round(_inputSamples * 1e9 / rate);
        var offset = readMonoNs - endTimeNs;
        _fitOffsets[_fitNext] = offset;
        _fitAt[_fitNext] = readMonoNs;
        _fitNext = (_fitNext + 1) % FitBlocks;
        _fitCount = Math.Min(_fitCount + 1, FitBlocks);
        var min = long.MaxValue;
        for (var i = 0; i < _fitCount; i++)
            if (readMonoNs - _fitAt[i] <= WaveSeconds * 1_000_000_000L && _fitOffsets[i] < min)
                min = _fitOffsets[i];
        _offsetNs = min;
    }

    private void Take(ReadOnlySpan<float> mono48k)
    {
        for (var i = 0; i < mono48k.Length; i++)
            _wave[(int)((_waveEnd + i) % _wave.Length)] = mono48k[i];
        Volatile.Write(ref _waveEnd, _waveEnd + mono48k.Length);
        _analyser.Push(mono48k, _stamper);
    }

    private void Resample(ReadOnlySpan<float> mono, int rate)
    {
        // Linear interpolation to 48 kHz: enough for levels, bands and onsets (none of them reads above 16 kHz).
        var step = rate / (double)AudioAnalyser.SampleRate;
        Span<float> chunk = stackalloc float[256];
        var n = 0;
        while (_resamplePos < mono.Length)
        {
            var i = (int)Math.Floor(_resamplePos);
            var t = (float)(_resamplePos - i);
            var a = i == 0 ? _resampleLast : mono[i - 1];
            var b = mono[i];
            chunk[n++] = a + (b - a) * t;
            _resamplePos += step;
            if (n == chunk.Length)
            {
                Take(chunk);
                n = 0;
            }
        }
        if (n > 0)
            Take(chunk[..n]);
        _resamplePos -= mono.Length;
        _resampleLast = mono[^1];
    }

    /// <summary>Stamps each frame the analyser makes and keeps it in the ring.</summary>
    private sealed class Stamper(AudioListener owner) : IAnalysisSink
    {
        public void Frame(AnalysisFrame frame)
        {
            frame.LeaderNs = owner.LeaderAtSample(frame.CentreSample);
            if (frame.Onset)
                frame.LastOnsetLeaderNs = owner.LeaderAtSample(frame.LastOnsetSample);
            else if (frame.LastOnsetSample < 0)
                frame.LastOnsetLeaderNs = long.MinValue;
            owner._ring.Frame(frame);
        }
    }
}
