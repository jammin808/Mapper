using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using Silk.NET.OpenAL;

namespace PlayMap.Media.Audio;

/// <summary>Where a timeline is now: its media time, whether it runs, and its rate.</summary>
public readonly record struct TimelinePosition(long MediaNs, bool Running, double Rate);

/// <summary>The timeline an audio output follows (the engine's lane: a transport over the leader's clock). It reads no clock of its
/// own: the output's service step reads its clock once and asks the timeline where it is at that instant.</summary>
public interface IAudioTimeline
{
    /// <summary>The position at <paramref name="localNs"/>, an instant of the output's clock.</summary>
    TimelinePosition At(long localNs);
}

/// <summary>A timeline from an anchor read fresh each step, at the leader instant of the output's instant.</summary>
public sealed class AnchorTimeline(Func<TimelineAnchor> anchor, Func<long, long> leaderAt) : IAudioTimeline
{
    /// <summary>The anchor on the output's own clock (a node that leads its own time line).</summary>
    public AnchorTimeline(Func<TimelineAnchor> anchor) : this(anchor, static localNs => localNs)
    {
    }

    /// <summary>The anchor on the leader's time line as <paramref name="clock"/> maps this machine's instants to it (the output
    /// runs on <see cref="NetworkClock.Local"/>).</summary>
    public AnchorTimeline(Func<TimelineAnchor> anchor, NetworkClock clock) : this(anchor, clock.ToLeader)
    {
    }

    public TimelinePosition At(long localNs) => Position(anchor(), leaderAt(localNs));

    /// <summary>An anchor's position at a leader instant.</summary>
    public static TimelinePosition Position(TimelineAnchor anchor, long leaderNs) =>
        new(anchor.MediaAt(leaderNs), anchor.Running && leaderNs >= anchor.LeaderNs, anchor.Rate);
}

/// <summary>How an audio output plays.</summary>
public enum AudioOutMode
{
    /// <summary>An OpenAL Soft device: a feeder thread keeps its queue topped up.</summary>
    Device,

    /// <summary>OpenAL Soft's loopback device: nothing plays until <see cref="AudioOut.Render"/> pulls the mix
    /// (tests; routing the mix elsewhere).</summary>
    Loopback,

    /// <summary>No sound at all: the samples are consumed on the output's clock at the slaved rate (a machine
    /// with no device; CI).</summary>
    Null,
}

/// <summary>Audio slaved to a timeline. Each service step reads the output's clock once and compares, at that one instant, the
/// audio clock (the media time of the sample at the output) with the timeline: within the hard-resync window the playback rate is
/// nudged by at most ±0.5 % (the source's pitch, a resample) so the clock converges and stays within ±10 ms; beyond it (a seek, a
/// jump, a resume elsewhere) the output resyncs hard: queue dropped, decoder seeked to the timeline, refilled. Every mode runs
/// this one step: a device's thread (OpenAL Soft over WASAPI, PulseAudio, ALSA or CoreAudio), the loopback's pull, the null
/// output's thread. The null output's playhead is a function of its clock — the media time at the step that last set its rate,
/// plus the time since at that rate — so a step consumes the whole frames up to its instant and carries the part of a frame
/// after them: no step loses or invents a frame, wherever the clock steps. The decoder is the caller's; this class only reads it,
/// on the thread that services the output.</summary>
public sealed unsafe class AudioOut : IDisposable
{
    public const int BlockFrames = 480;
    public const int QueueBlocks = 4;
    public const double MaxCorrection = 0.005;

    private const int AlFormatStereoFloat32 = 0x10011;
    private const int AlcFormatChannelsSoft = 0x1990;
    private const int AlcFormatTypeSoft = 0x1991;
    private const int AlcStereoSoft = 0x1501;
    private const int AlcFloatSoft = 0x1406;
    private const int AlcFrequency = 0x1007;
    private const int AlSampleOffsetLatencySoft = 0x1200;

    private static readonly Lock GlobalContextGate = new();

    private readonly Lock _gate = new();
    private readonly float[] _block = new float[BlockFrames * FfmpegAudioDecoder.Channels];
    private readonly uint[] _buffers = new uint[QueueBlocks];
    private readonly Queue<uint> _free = new(QueueBlocks);
    private readonly Queue<(uint Buffer, long Frames)> _queued = new(QueueBlocks);
    private readonly ManualResetEventSlim _stopFeeder = new(false);
    private readonly Thread? _feeder;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly delegate* unmanaged[Cdecl]<Context*, byte> _setThreadContext;
    private readonly delegate* unmanaged[Cdecl]<Device*, void*, int, void> _renderSamples;
    private readonly delegate* unmanaged[Cdecl]<uint, int, long*, void> _getSourceI64;
    private readonly uint _source;
    private readonly IMonoClock _clock;

    private FfmpegAudioDecoder? _decoder;
    private IAudioTimeline? _timeline;
    private bool _started;
    private bool _paused;
    private long _headSample;     // output sample index of the first queued buffer's first frame
    private long _nullPosition;   // null mode: the frames consumed (the sample at the output)
    private long _nullBaseNs;     // null mode: the instant the playhead's rate was last set
    private double _nullBaseMediaNs; // null mode: the playhead's media time at that instant
    private double _nullHeadNs;   // null mode: the playhead's media time at the last step
    private double _pitch = 1;
    private float _volume = 1;
    private bool _volumeDirty;
    private long _resyncs;
    private double _lastErrorMs;
    private long _framesFed;
    private bool _disposed;

    private AudioOut(AudioOutMode mode, string status, Device* device, Context* context, IMonoClock clock, bool feeder = true)
    {
        Mode = mode;
        Status = status;
        _device = device;
        _context = context;
        _clock = clock;
        if (mode != AudioOutMode.Null)
        {
            var alc = OpenAlLibrary.Alc;
            _setThreadContext = (delegate* unmanaged[Cdecl]<Context*, byte>)alc.GetProcAddress(null, "alcSetThreadContext");
            _renderSamples = (delegate* unmanaged[Cdecl]<Device*, void*, int, void>)alc.GetProcAddress(null, "alcRenderSamplesSOFT");
            using (Use())
            {
                var al = OpenAlLibrary.Al;
                _getSourceI64 = (delegate* unmanaged[Cdecl]<uint, int, long*, void>)al.GetProcAddress("alGetSourcei64vSOFT");
                _source = al.GenSource();
                fixed (uint* b = _buffers)
                    al.GenBuffers(QueueBlocks, b);
                foreach (var b in _buffers)
                    _free.Enqueue(b);
            }
        }
        if (mode != AudioOutMode.Loopback && feeder)
        {
            _feeder = new Thread(Feed) { IsBackground = true, Name = "audio out", Priority = ThreadPriority.AboveNormal };
            _feeder.Start();
        }
    }

    public AudioOutMode Mode { get; }

    /// <summary>The device's name, or why the output is the null one.</summary>
    public string Status { get; }

    /// <summary>The clock each service step reads once: the instant the timeline is asked about (and, for the null output, the
    /// instant its playhead is at).</summary>
    public IMonoClock Clock => _clock;

    /// <summary>The playback rate applied now (the timeline's rate × the correction).</summary>
    public double Pitch
    {
        get
        {
            lock (_gate)
                return _pitch;
        }
    }

    /// <summary>Hard resyncs so far (jumps, seeks, resumes elsewhere).</summary>
    public long Resyncs => Interlocked.Read(ref _resyncs);

    /// <summary>Audio clock minus timeline at the last service step, milliseconds.</summary>
    public double LastErrorMs
    {
        get
        {
            lock (_gate)
                return _lastErrorMs;
        }
    }

    /// <summary>Frames handed to the output (queued, or consumed by the null output).</summary>
    public long FramesFed => Interlocked.Read(ref _framesFed);

    /// <summary>The window within which the clock is steered by rate; beyond it the output resyncs hard.</summary>
    public long HardResyncNs { get; init; } = 50_000_000;

    /// <summary>The correction per second of error (1.0: a 5 ms error asks for the full 0.5 %).</summary>
    public double Gain { get; init; } = 1.0;

    public float Volume
    {
        get
        {
            lock (_gate)
                return _volume;
        }
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0f, 4f);
                _volumeDirty = true;
            }
        }
    }

    /// <summary>Opens the default (or a named) OpenAL Soft device, serviced on <paramref name="clock"/> (the process's monotonic
    /// clock by default); when there is none the output is the null one on that clock and <see cref="Status"/> says why.</summary>
    public static AudioOut OpenDevice(string? deviceName = null, IMonoClock? clock = null) => OpenDevice(deviceName, OpenAlLibrary.Current, clock);

    /// <summary><see cref="OpenDevice(string?, IMonoClock?)"/> given the process's OpenAL load (a failed load is the null output
    /// with its words). Every output speaks through the process's one load, so a loaded state must be that one.</summary>
    internal static AudioOut OpenDevice(string? deviceName, OpenAlLibrary.State library, IMonoClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        var on = clock ?? MonoClock.Instance;
        if (library.Handle == 0)
            return OpenNull(library.Words, on);
        if (!ReferenceEquals(library, OpenAlLibrary.Current))
            throw new ArgumentException("an output opens only through the process's OpenAL load", nameof(library));
        var device = OpenAlLibrary.Alc.OpenDevice(deviceName!);
        if (device is null)
            return OpenNull("no audio device" + (deviceName is null ? "" : " named " + deviceName), on);
        var context = OpenAlLibrary.Alc.CreateContext(device, null);
        if (context is null)
        {
            OpenAlLibrary.Alc.CloseDevice(device);
            return OpenNull("the audio device refused a context", on);
        }
        return new AudioOut(AudioOutMode.Device, deviceName ?? "default device", device, context, on);
    }

    /// <summary>Opens OpenAL Soft's loopback device (float stereo 48 kHz), serviced on <paramref name="clock"/> (the process's
    /// monotonic clock by default): the mix is pulled by <see cref="Render"/>. Throws <see cref="InvalidOperationException"/>
    /// with the reason when no OpenAL loads or the loopback device cannot open.</summary>
    public static AudioOut OpenLoopback(IMonoClock? clock = null)
    {
        if (!OpenAlLibrary.Loaded)
            throw new InvalidOperationException(OpenAlLibrary.Words);
        var alc = OpenAlLibrary.Alc;
        var open = (delegate* unmanaged[Cdecl]<byte*, Device*>)alc.GetProcAddress(null, "alcLoopbackOpenDeviceSOFT");
        if (open is null)
            throw new InvalidOperationException("this OpenAL has no loopback device (ALC_SOFT_loopback)");
        var device = open(null);
        if (device is null)
            throw new InvalidOperationException("the loopback device did not open");
        var attributes = stackalloc int[] { AlcFormatChannelsSoft, AlcStereoSoft, AlcFormatTypeSoft, AlcFloatSoft, AlcFrequency, FfmpegAudioDecoder.SampleRate, 0 };
        var context = alc.CreateContext(device, attributes);
        if (context is null)
        {
            alc.CloseDevice(device);
            throw new InvalidOperationException("the loopback device refused float stereo at 48 kHz");
        }
        return new AudioOut(AudioOutMode.Loopback, "loopback", device, context, clock ?? MonoClock.Instance);
    }

    /// <summary>An output with no sound: samples are consumed on the clock at the slaved rate.</summary>
    public static AudioOut OpenNull(string why = "no sound requested", IMonoClock? clock = null) =>
        new(AudioOutMode.Null, why, null, null, clock ?? MonoClock.Instance);

    /// <summary>A null output with no thread of its own: each service step is a call of <see cref="Service"/> (tests: every step at
    /// an instant the test sets).</summary>
    internal static AudioOut OpenNullPulled(string why, IMonoClock clock) => new(AudioOutMode.Null, why, null, null, clock, feeder: false);

    /// <summary>One service step of a pulled null output.</summary>
    internal void Service()
    {
        if (Mode != AudioOutMode.Null || _feeder is not null)
            throw new InvalidOperationException("Service steps a pulled null output only");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ServiceLocked();
        }
    }

    /// <summary>Plays a decoder slaved to a timeline (replacing what played). The first service step syncs
    /// hard to the timeline.</summary>
    public void Play(FfmpegAudioDecoder decoder, IAudioTimeline timeline)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopLocked();
            _decoder = decoder;
            _timeline = timeline;
        }
    }

    /// <summary>Stops and lets go of the decoder.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_disposed)
                StopLocked();
        }
    }

    /// <summary>The media time of the sample at the output now (or −1 when nothing plays).</summary>
    public long AudioClockNs
    {
        get
        {
            lock (_gate)
                return _decoder is null || !_started ? -1 : ClockLocked();
        }
    }

    /// <summary>Loopback only: one service step, then the mix of <c>output.Length / 2</c> stereo frames.</summary>
    public void Render(Span<float> output)
    {
        if (Mode != AudioOutMode.Loopback)
            throw new InvalidOperationException("Render pulls a loopback output only");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using (Use())
            {
                ServiceLocked();
                fixed (float* p = output)
                    _renderSamples(_device, p, output.Length / FfmpegAudioDecoder.Channels);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _stopFeeder.Set();
        _feeder?.Join();
        lock (_gate)
        {
            if (Mode != AudioOutMode.Null)
            {
                using (Use())
                {
                    var al = OpenAlLibrary.Al;
                    al.SourceStop(_source);
                    al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
                    al.DeleteSource(_source);
                    fixed (uint* b = _buffers)
                        al.DeleteBuffers(QueueBlocks, b);
                }
                var alc = OpenAlLibrary.Alc;
                if (_setThreadContext is null)
                {
                    lock (GlobalContextGate)
                        alc.MakeContextCurrent(null);
                }
                alc.DestroyContext(_context);
                alc.CloseDevice(_device);
            }
            _decoder = null;
            _timeline = null;
        }
        _stopFeeder.Dispose();
    }

    // ---- the service step (under _gate, with the context current) ------------------------------------

    private void ServiceLocked()
    {
        var decoder = _decoder;
        var timeline = _timeline;
        if (decoder is null || timeline is null)
            return;
        if (_volumeDirty && Mode != AudioOutMode.Null)
        {
            OpenAlLibrary.Al.SetSourceProperty(_source, SourceFloat.Gain, _volume);
            _volumeDirty = false;
        }
        // The one instant of this step: the timeline is read at it, and the null output's playhead is there.
        var at = _clock.NowNs;
        var now = timeline.At(at);
        if (!now.Running)
        {
            if (_started && !_paused)
            {
                _paused = true;
                if (Mode != AudioOutMode.Null)
                    OpenAlLibrary.Al.SourcePause(_source);
            }
            return;
        }
        var rate = now.Rate > 0 && double.IsFinite(now.Rate) ? now.Rate : 1;
        if (!_started)
        {
            HardSyncLocked(decoder, now.MediaNs, rate, at, counted: false);
            return;
        }
        if (_paused)
        {
            // The playhead held while paused: it runs on from here.
            _paused = false;
            _nullBaseNs = at;
            _nullBaseMediaNs = _nullHeadNs;
            if (Mode != AudioOutMode.Null)
                OpenAlLibrary.Al.SourcePlay(_source);
        }
        if (Mode == AudioOutMode.Null)
            AdvanceNullLocked(decoder, at);
        else
            ReclaimLocked();
        var error = ClockLocked() - now.MediaNs;
        _lastErrorMs = error / 1e6;
        if (Math.Abs(error) > HardResyncNs)
        {
            HardSyncLocked(decoder, now.MediaNs, rate, at, counted: true);
            return;
        }
        var correction = Math.Clamp(error / 1e9 * Gain, -MaxCorrection, MaxCorrection);
        SetPitchLocked(rate * (1 - correction), at);
        if (Mode != AudioOutMode.Null)
            RefillLocked(decoder);
    }

    private void HardSyncLocked(FfmpegAudioDecoder decoder, long mediaNs, double rate, long at, bool counted)
    {
        if (counted)
            Interlocked.Increment(ref _resyncs);
        decoder.Seek(mediaNs);
        _headSample = decoder.PositionSamples;
        _nullPosition = _headSample;
        _nullHeadNs = _headSample * 1e9 / FfmpegAudioDecoder.SampleRate;
        _nullBaseNs = at;
        _nullBaseMediaNs = _nullHeadNs;
        _started = true;
        _paused = false;
        _lastErrorMs = 0;
        SetPitchLocked(rate, at);
        if (Mode == AudioOutMode.Null)
            return;
        var al = OpenAlLibrary.Al;
        al.SourceStop(_source);
        // Detaching the buffer list unqueues everything, played or not.
        al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
        while (_queued.Count > 0)
            _free.Enqueue(_queued.Dequeue().Buffer);
        RefillLocked(decoder);
        al.SourcePlay(_source);
    }

    // A new rate from instant at on: the null output's playhead runs on from where it is at that instant (its step has consumed up
    // to it), so the part of a frame it holds carries over.
    private void SetPitchLocked(double pitch, long at)
    {
        var p = Math.Clamp(pitch, 0.5, 2.0);
        if (Math.Abs(p - _pitch) < 1e-7)
            return;
        _pitch = p;
        if (Mode != AudioOutMode.Null)
        {
            OpenAlLibrary.Al.SetSourceProperty(_source, SourceFloat.Pitch, (float)p);
            return;
        }
        _nullBaseNs = at;
        _nullBaseMediaNs = _nullHeadNs;
    }

    // Unqueues played buffers; the queue's head moves on by their frames.
    private void ReclaimLocked()
    {
        var al = OpenAlLibrary.Al;
        al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        while (processed-- > 0 && _queued.Count > 0)
        {
            var (buffer, frames) = _queued.Dequeue();
            var b = buffer;
            al.SourceUnqueueBuffers(_source, 1, &b);
            _headSample += frames;
            _free.Enqueue(buffer);
        }
    }

    private void RefillLocked(FfmpegAudioDecoder decoder)
    {
        var al = OpenAlLibrary.Al;
        while (_free.Count > 0)
        {
            var n = decoder.Read(_block);
            if (n == 0)
                break;
            var buffer = _free.Dequeue();
            fixed (float* p = _block)
                al.BufferData(buffer, (BufferFormat)AlFormatStereoFloat32, p, n * FfmpegAudioDecoder.Channels * sizeof(float), FfmpegAudioDecoder.SampleRate);
            al.SourceQueueBuffers(_source, 1, &buffer);
            _queued.Enqueue((buffer, n));
            Interlocked.Add(ref _framesFed, n);
        }
        al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if (state != (int)SourceState.Playing && !_paused && _queued.Count > 0)
            al.SourcePlay(_source);
    }

    // The null output's consumption: its playhead at instant at (the media time at its base plus the time since at its rate), and
    // the whole frames before it; what is less than a frame stays in the playhead for the next step.
    private void AdvanceNullLocked(FfmpegAudioDecoder decoder, long at)
    {
        if (at <= _nullBaseNs)
            return;
        _nullHeadNs = _nullBaseMediaNs + (at - _nullBaseNs) * _pitch;
        var whole = (long)Math.Floor(_nullHeadNs * FfmpegAudioDecoder.SampleRate / 1e9);
        if (whole <= _nullPosition)
            return;
        var done = decoder.Skip(whole - _nullPosition);
        _nullPosition = whole;
        Interlocked.Add(ref _framesFed, done);
    }

    // The media time of the sample at the output: the queue head plus the source's offset into the queue, less
    // what the device still has to play when it can say (AL_SOFT_source_latency).
    private long ClockLocked()
    {
        if (Mode == AudioOutMode.Null)
            return (long)Math.Round(_nullHeadNs);
        if (_getSourceI64 is not null)
        {
            var values = stackalloc long[2];
            _getSourceI64(_source, AlSampleOffsetLatencySoft, values);
            var offsetFrames = values[0] / (double)(1L << 32);
            var latencyNs = Mode == AudioOutMode.Device ? values[1] : 0;
            return FfmpegAudioDecoder.SampleToNs(_headSample) + (long)Math.Round(offsetFrames * 1e9 / FfmpegAudioDecoder.SampleRate) - (long)(latencyNs * _pitch);
        }
        OpenAlLibrary.Al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int offset);
        return FfmpegAudioDecoder.SampleToNs(_headSample + offset);
    }

    private void StopLocked()
    {
        if (Mode != AudioOutMode.Null && _started)
        {
            using (Use())
            {
                var al = OpenAlLibrary.Al;
                al.SourceStop(_source);
                al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
                while (_queued.Count > 0)
                    _free.Enqueue(_queued.Dequeue().Buffer);
            }
        }
        _decoder = null;
        _timeline = null;
        _started = false;
        _paused = false;
    }

    // ---- threads and contexts ---------------------------------------------------------------------------

    private void Feed()
    {
        if (Mode != AudioOutMode.Null && _setThreadContext is not null)
            _setThreadContext(_context);
        while (!_stopFeeder.Wait(5))
        {
            lock (_gate)
            {
                if (_disposed)
                    break;
                if (Mode == AudioOutMode.Null || _setThreadContext is not null)
                {
                    ServiceLocked();
                }
                else
                {
                    using (Use())
                        ServiceLocked();
                }
            }
        }
        if (Mode != AudioOutMode.Null && _setThreadContext is not null)
            _setThreadContext(null);
    }

    // Makes this output's context current for the calling thread (thread-local where OpenAL Soft offers it,
    // otherwise process-wide under a global lock) until the scope ends.
    private ContextScope Use()
    {
        if (_setThreadContext is not null)
        {
            _setThreadContext(_context);
            return new ContextScope(this, global: false);
        }
        GlobalContextGate.Enter();
        OpenAlLibrary.Alc.MakeContextCurrent(_context);
        return new ContextScope(this, global: true);
    }

    private readonly struct ContextScope(AudioOut owner, bool global) : IDisposable
    {
        public void Dispose()
        {
            if (global)
                GlobalContextGate.Exit();
            else if (owner._feeder is null || Thread.CurrentThread != owner._feeder)
                owner._setThreadContext(null);
        }
    }
}
