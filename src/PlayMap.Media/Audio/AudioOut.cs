using PlayMap.Core.Machine;
using PlayMap.Core.Playback;
using PlayMap.Core.Threading;
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

/// <summary>Audio slaved to a timeline. Each service step reads the output's clock once — a device's position (the media time of
/// the sample at its output) read together with it, before anything else the step does — and compares the audio clock with the
/// timeline at that one instant: within the hard-resync window the playback rate is nudged by at most ±0.5 % (the source's pitch,
/// a resample) so the clock converges and stays within ±10 ms; beyond it (a seek, a jump, a resume elsewhere) the output resyncs
/// hard: queue dropped, decoder seeked to the timeline, refilled. Sound plays rates from <see cref="MinRate"/> to
/// <see cref="MaxRate"/>, with the correction's room beyond either end; at any other rate, as while the timeline is paused, the
/// output holds silent. Every mode runs this one step: a device's thread (OpenAL Soft over WASAPI, PulseAudio, ALSA or
/// CoreAudio), the loopback's pull, the null output's thread. A thread steps every 5 ms, waiting on a deadline timer it makes as it
/// starts for instants on the machine's monotonic clock, so no wait reads the output's clock. The null output's playhead is a
/// function of its clock — the media time at the instant its rate was last set, plus the time since at that rate, never run back
/// by a clock read before its last step — so a step consumes the whole frames up to its instant and carries the part of a frame
/// after them: no step loses or invents a frame, wherever the clock steps. While the timeline is paused the playhead holds at the
/// paused media time; a step whose frames span more than the hard-resync window passes them by a seek, so no step decodes more than
/// the window. The decoder is the caller's; this class only reads it, on the thread that services the output.</summary>
public sealed unsafe class AudioOut : IDisposable
{
    public const int BlockFrames = 480;
    public const int QueueBlocks = 4;
    public const double MaxCorrection = 0.005;

    /// <summary>The slowest rate sound plays (its pitch may go <see cref="MaxCorrection"/> below it to steer).</summary>
    public const double MinRate = 0.5;

    /// <summary>The fastest rate sound plays (its pitch may go <see cref="MaxCorrection"/> above it to steer).</summary>
    public const double MaxRate = 2.0;

    private const int AlFormatStereoFloat32 = 0x10011;
    private const int AlcFormatChannelsSoft = 0x1990;
    private const int AlcFormatTypeSoft = 0x1991;
    private const int AlcStereoSoft = 0x1501;
    private const int AlcFloatSoft = 0x1406;
    private const int AlcFrequency = 0x1007;
    private const int AlSampleOffsetLatencySoft = 0x1200;

    /// <summary>How long the feeder waits after one service step before the next (ns, on its cadence clock).</summary>
    private const long FeedPeriodNs = 5_000_000;

    private static readonly Lock GlobalContextGate = LockWaits.Prepared();

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
    private readonly IMonoClock _cadence;
    private readonly Func<IDeadlineTimer> _timers;

    private FfmpegAudioDecoder? _decoder;
    private IAudioTimeline? _timeline;
    private bool _started;
    private bool _paused;
    private long _headSample;     // output sample index of the first queued buffer's first frame
    private long _nullPosition;   // null mode: the frames consumed (the sample at the output)
    private long _nullBaseNs;     // null mode: the instant the playhead's rate was last set
    private double _nullBaseMediaNs; // null mode: the playhead's media time at that instant
    private double _nullHeadNs;   // null mode: the playhead's media time at _nullAtNs
    private long _nullAtNs;       // null mode: the latest instant a step put the playhead at
    private double _pitch = 1;
    private float _volume = 1;
    private bool _volumeDirty;
    private long _resyncs;
    private double _lastErrorMs;
    private long _framesFed;
    private bool _disposed;

    /// <param name="cadence">The clock the feeder's waits are on (the machine's monotonic clock by default), never the output's.</param>
    /// <param name="timers">Makes the feeder's deadline timer, on its thread as it starts (the thread disposes it); null:
    /// <see cref="ThreadClaims.CreateTimer"/> on <paramref name="cadence"/>.</param>
    private AudioOut(AudioOutMode mode, string status, Device* device, Context* context, IMonoClock clock, bool feeder = true,
        IMonoClock? cadence = null, Func<IDeadlineTimer>? timers = null)
    {
        Mode = mode;
        Status = status;
        _device = device;
        _context = context;
        _clock = clock;
        var paced = cadence ?? MonoClock.Instance;
        _cadence = paced;
        _timers = timers ?? (() => ThreadClaims.CreateTimer(paced));
        // The feeder steps under the gate on its deadlines while readers take it too: a first wait allocates nothing.
        LockWaits.Prepare(_gate);
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

    /// <summary>True when sound plays <paramref name="rate"/> (<see cref="MinRate"/> to <see cref="MaxRate"/>).</summary>
    public static bool Plays(double rate) => rate is >= MinRate and <= MaxRate;

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

    /// <summary>A null output whose feeder waits on the timer <paramref name="timers"/> makes on its thread, for instants on
    /// <paramref name="cadence"/> (tests: every wait of the feeder seen, and its cadence moved by the test's timer).</summary>
    internal static AudioOut OpenNullPaced(string why, IMonoClock clock, IMonoClock cadence, Func<IDeadlineTimer> timers) =>
        new(AudioOutMode.Null, why, null, null, clock, feeder: true, cadence, timers);

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

    /// <summary>The media time of the sample at the output now (or −1 when nothing plays): a device's read with its context current
    /// on the calling thread.</summary>
    public long AudioClockNs
    {
        get
        {
            lock (_gate)
            {
                if (_decoder is null || !_started)
                    return -1;
                if (Mode == AudioOutMode.Null)
                    return ClockLocked();
                using (Use())
                    return ClockLocked();
            }
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

    /// <summary>Loopback only: the device plays <c>output.Length / 2</c> stereo frames of the mix with no service step (tests: a device
    /// that plays on while a step runs).</summary>
    internal void Pull(Span<float> output)
    {
        if (Mode != AudioOutMode.Loopback)
            throw new InvalidOperationException("Pull mixes a loopback output only");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fixed (float* p = output)
                _renderSamples(_device, p, output.Length / FfmpegAudioDecoder.Channels);
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
        // The one instant of this step, read right after a device's position and before anything else: the timeline is asked about
        // it, the device's position is compared there, and the null output's playhead is there.
        var played = Mode == AudioOutMode.Null ? 0 : DevicePositionLocked();
        var at = _clock.NowNs;
        var now = timeline.At(at);
        if (!now.Running || !Plays(now.Rate))
        {
            HoldLocked(decoder, now, at);
            return;
        }
        if (!_started)
        {
            HardSyncLocked(decoder, now.MediaNs, now.Rate, at, counted: false);
            return;
        }
        if (_paused)
        {
            // The playhead held while the output held: it runs on from here.
            _paused = false;
            _nullAtNs = Math.Max(at, _nullAtNs);
            _nullBaseNs = _nullAtNs;
            _nullBaseMediaNs = _nullHeadNs;
            if (Mode != AudioOutMode.Null)
                OpenAlLibrary.Al.SourcePlay(_source);
        }
        if (Mode == AudioOutMode.Null)
            played = AdvanceNullLocked(decoder, at);
        var error = played - now.MediaNs;
        _lastErrorMs = error / 1e6;
        if (Math.Abs(error) > HardResyncNs)
        {
            HardSyncLocked(decoder, now.MediaNs, now.Rate, at, counted: true);
            return;
        }
        // The rate is in the range sound plays, so the pitch stays within its correction of that range.
        var correction = Math.Clamp(error / 1e9 * Gain, -MaxCorrection, MaxCorrection);
        SetPitchLocked(now.Rate * (1 - correction));
        if (Mode != AudioOutMode.Null)
            RefillLocked(decoder);
    }

    // The timeline does not run, or runs at a rate sound does not play: the output holds silent. The null output's playhead holds
    // at a paused timeline's media time: a step plays it on to there (consuming the whole frames before it) when that is ahead of
    // it within the hard-resync window, so the step that resumes finds the audio clock on the timeline; it never runs back.
    private void HoldLocked(FfmpegAudioDecoder decoder, TimelinePosition now, long at)
    {
        if (!_started)
            return;
        if (!_paused)
        {
            _paused = true;
            if (Mode != AudioOutMode.Null)
                OpenAlLibrary.Al.SourcePause(_source);
        }
        if (Mode != AudioOutMode.Null || now.Running)
            return;
        var ahead = now.MediaNs - _nullHeadNs;
        if (ahead <= 0 || ahead > HardResyncNs)
            return;
        _nullHeadNs = now.MediaNs;
        _nullAtNs = Math.Max(at, _nullAtNs);
        ConsumeNullLocked(decoder);
    }

    private void HardSyncLocked(FfmpegAudioDecoder decoder, long mediaNs, double rate, long at, bool counted)
    {
        if (counted)
            Interlocked.Increment(ref _resyncs);
        decoder.Seek(mediaNs);
        _headSample = decoder.PositionSamples;
        _nullPosition = _headSample;
        _nullHeadNs = _headSample * 1e9 / FfmpegAudioDecoder.SampleRate;
        _nullAtNs = at;
        _nullBaseNs = at;
        _nullBaseMediaNs = _nullHeadNs;
        _started = true;
        _paused = false;
        _lastErrorMs = 0;
        SetPitchLocked(rate);
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

    // A new rate from the null output's playhead on: it runs on from where it is at its instant (its step has consumed up to it),
    // so the part of a frame it holds carries over.
    private void SetPitchLocked(double pitch)
    {
        if (Math.Abs(pitch - _pitch) < 1e-7)
            return;
        _pitch = pitch;
        if (Mode != AudioOutMode.Null)
        {
            OpenAlLibrary.Al.SetSourceProperty(_source, SourceFloat.Pitch, (float)pitch);
            return;
        }
        _nullBaseNs = _nullAtNs;
        _nullBaseMediaNs = _nullHeadNs;
    }

    // A device's position now: its played buffers unqueued, then the media time of the sample at its output.
    private long DevicePositionLocked()
    {
        ReclaimLocked();
        return ClockLocked();
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

    // The null output's playhead at instant at: the media time at its base plus the time since at its rate. A clock read at or
    // before the playhead's instant leaves it where it is. Answers the audio clock.
    private long AdvanceNullLocked(FfmpegAudioDecoder decoder, long at)
    {
        if (at > _nullAtNs)
        {
            _nullAtNs = at;
            _nullHeadNs = _nullBaseMediaNs + (at - _nullBaseNs) * _pitch;
            ConsumeNullLocked(decoder);
        }
        return (long)Math.Round(_nullHeadNs);
    }

    // The whole frames before the null output's playhead are consumed; what is less than a frame stays in the playhead for the
    // next step. Frames spanning more than the hard-resync window (a thread that did not step for that long) are passed by a seek,
    // never decoded inside one step.
    private void ConsumeNullLocked(FfmpegAudioDecoder decoder)
    {
        var whole = (long)Math.Floor(_nullHeadNs * FfmpegAudioDecoder.SampleRate / 1e9);
        var gap = whole - _nullPosition;
        if (gap <= 0)
            return;
        long done;
        if (gap > FfmpegAudioDecoder.NsToSample(HardResyncNs))
        {
            decoder.Seek(FfmpegAudioDecoder.SampleToNs(whole));
            done = decoder.DurationNs > 0 ? Math.Clamp(FfmpegAudioDecoder.NsToSample(decoder.DurationNs) - _nullPosition, 0, gap) : gap;
        }
        else
        {
            done = decoder.Skip(gap);
        }
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
        using var claim = Core.Machine.ThreadClaims.Claim(Core.Machine.ThreadRole.Audio);
        // The feeder's waits are on its cadence clock, never the output's, which only a service step reads.
        using var timer = _timers();
        var stop = _stopFeeder.WaitHandle;
        if (Mode != AudioOutMode.Null && _setThreadContext is not null)
            _setThreadContext(_context);
        while (timer.WaitUntil(_cadence.NowNs + FeedPeriodNs, stop))
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
