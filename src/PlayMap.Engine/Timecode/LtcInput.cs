using System.Globalization;
using PlayMap.Core.Audio;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Tc;

namespace PlayMap.Engine.Tc;

/// <summary>An LTC source on an audio input: the capture's blocks through the decoder (on the capture thread), each frame's end
/// mapped from its sample time to the monotonic clock and handed to the board as an observation. The sample clock is mapped by
/// the lower envelope of (read instant − block end ÷ rate) over the last 10 s: scheduling only ever delays a read, so the earliest
/// is the truest. The input is opened as "&lt;device&gt;@&lt;channel&gt;" (the audio hub resolves the show's name for the device and
/// hands this reader its one channel, sharing the device with every other listener); <see cref="Open"/> waits on the device, so
/// its owner calls it, and <see cref="Dispose"/>, off its own loop. <see cref="Close"/> stops it hearing at once, on that loop: a
/// closed reader hands the board nothing more, whenever its device is given back; nor does a reader whose source the board now has
/// on another device or channel. An input that cannot open, or a node without audio inputs, says so as the source's state.</summary>
public sealed class LtcInput : IAudioCaptureSink, IDisposable
{
    private const long WindowNs = 10_000_000_000;
    private const long SilentAfterNs = 500_000_000;

    private readonly TimecodeBoard _board;
    private readonly IMonoClock _mono;
    private readonly Lock _gate = new();
    private readonly Queue<(long At, double Offset)> _offsets = new();
    private readonly List<LtcFrameEvent> _frames = [];
    private readonly IAudioInputs? _audio;
    private IAudioCapture? _capture;
    private bool _closed;
    private LtcDecoder? _decoder;
    private LtcCounts _counted;
    private long _signalAt;
    private bool _silent;

    public LtcInput(TimecodeSource source, IAudioInputs? audio, TimecodeBoard board, IMonoClock mono)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(mono);
        Source = source;
        _audio = audio;
        _board = board;
        _mono = mono;
        _signalAt = mono.NowNs;
    }

    /// <summary>The key this reader opens: the show's name for the device ("" = the default input) and its channel.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Source.Device}@{Source.Channel}");

    /// <summary>Opens the input: waits while the device opens (the hub holds its lock meanwhile), so never on a loop that must keep
    /// answering. An input closed before it opened opens nothing.</summary>
    public void Open()
    {
        if (_audio is null)
        {
            if (Heard)
                _board.Fault(Source.Id, "no audio inputs on this node (the audio hub is not running) — LTC cannot arrive here", _mono.NowNs);
            return;
        }
        lock (_gate)
        {
            if (_closed || _capture is not null)
                return;
        }
        IAudioCapture capture;
        try
        {
            capture = _audio.Open(Key, 48000, this);
        }
        catch (AudioUnavailableException ex)
        {
            if (Heard)
                _board.Fault(Source.Id, ex.Message, _mono.NowNs);
            return;
        }
        var device = Source.Device.Length == 0 ? "the default input" : Source.Device;
        lock (_gate)
        {
            if (!_closed)
            {
                _capture = capture;
                if (_board.Holds(Source))
                    _board.Input(Source.Id, true, -120, string.Create(CultureInfo.InvariantCulture, $"listening on {device} channel {Source.Channel}"));
                return;
            }
        }
        // Closed while the device opened: the open is given back at once.
        capture.Dispose();
    }

    public TimecodeSource Source { get; }

    public void Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs)
    {
        if (channels <= 0 || sampleRate <= 0)
            return;
        lock (_gate)
        {
            if (_closed || !_board.Holds(Source))
                return;
            if (_decoder is null || _decoder.SampleRate != sampleRate)
                _decoder = new LtcDecoder(sampleRate, Source.FloorDbfs);
            var count = interleaved.Length / channels;
            var perSample = 1e9 / sampleRate;
            // The read instant belongs to the block's end; keep the earliest mapping of the last 10 s.
            _offsets.Enqueue((readMonoNs, readMonoNs - (firstSampleIndex + count) * perSample));
            while (_offsets.Count > 1 && readMonoNs - _offsets.Peek().At > WindowNs)
                _offsets.Dequeue();
            var offset = _offsets.Min(o => o.Offset);
            // The hub hands this reader its one channel; an input that hands every channel is read at the source's own.
            var channel = channels == 1 ? 0 : Math.Clamp(Source.Channel - 1, 0, channels - 1);
            _frames.Clear();
            _decoder.Process(interleaved, channels, channel, firstSampleIndex, _frames);
            if (_decoder.HasSignal)
                _signalAt = readMonoNs;
            var observations = new List<TcObservation>(_frames.Count);
            foreach (var f in _frames)
            {
                var format = TcFormat.OfLabel(LabelRate(f), f.Fields.DropFrame);
                // Above 30 fps the frames field counts frame pairs.
                var labelFrames = format.Pairs ? f.Fields.Frames * 2 : f.Fields.Frames;
                if (!format.TryFrame(f.Fields.Hours, f.Fields.Minutes, f.Fields.Seconds, labelFrames, out var frame))
                    continue;
                var mono = (long)Math.Round(offset + f.EndSample * perSample) - Source.LatencyNs;
                // Forward, a frame's end is the next frame's start; reversed, it is its own start.
                var at = f.Direction > 0 ? frame + (format.Pairs ? 2 : 1) : frame;
                observations.Add(new TcObservation(mono, at, 0, format, f.Direction, TcObsKind.FrameEnd, f.Fields.UserBits, f.Fields.Bgf1));
            }
            var counts = _decoder.Counts;
            var delta = new TcCounters(counts.Biphase - _counted.Biphase, 0, 0, 0, counts.Invalid - _counted.Invalid, counts.BadBcd - _counted.BadBcd,
                counts.Parity - _counted.Parity);
            _counted = counts;
            if (delta != TcCounters.Zero)
                _board.Count(Source.Id, delta);
            if (observations.Count > 0)
            {
                _silent = false;
                _board.Observe(Source.Id, observations);
            }
            _board.Input(Source.Id, true, _decoder.LevelDbfs, null);
        }
    }

    /// <summary>The label rate of a frame: measured from the bit period (80 bits a frame), snapped to 24, 25 or 30 (frame pairs
    /// above).</summary>
    private int LabelRate(LtcFrameEvent f)
    {
        var fps = _decoder!.SampleRate / (f.SamplesPerBit * 80);
        var label = Math.Abs(fps - 24) < Math.Abs(fps - 25) ? 24 : Math.Abs(fps - 25) <= Math.Abs(fps - 29.97) ? 25 : 30;
        if (TcFormat.TryParse(Source.Format, out var configured))
            return configured.LabelFps;
        return label;
    }

    public void Faulted(string words)
    {
        if (Heard)
            _board.Fault(Source.Id, words, _mono.NowNs);
    }

    /// <summary>The control loop's tick: an open input without signal for half a second is silent.</summary>
    public void Tick(long nowNs)
    {
        lock (_gate)
        {
            if (_closed || _capture is null || _silent || nowNs - _signalAt < SilentAfterNs || !_board.Holds(Source))
                return;
            _silent = true;
        }
        _board.Silent(Source.Id, nowNs);
    }

    /// <summary>Stops hearing at once (cheap: its owner's loop calls it when the source goes or moves, before the close that waits
    /// off it): later blocks, a device fault and a late open's words never reach the board.</summary>
    public void Close()
    {
        lock (_gate)
            _closed = true;
    }

    /// <summary>Whether what this reader hears is still its source's: not closed, and the board's source still on this reader's
    /// device and channel (a source moved elsewhere starts afresh on the board at once, before its owner retires this reader).</summary>
    private bool Heard
    {
        get
        {
            lock (_gate)
                return !_closed && _board.Holds(Source);
        }
    }

    /// <summary>Closes the input (waits while the hub lets the device go when this was its last listener).</summary>
    public void Dispose()
    {
        IAudioCapture? capture;
        lock (_gate)
        {
            _closed = true;
            capture = _capture;
            _capture = null;
        }
        capture?.Dispose();
    }
}
