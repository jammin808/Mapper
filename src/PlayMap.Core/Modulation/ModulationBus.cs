using PlayMap.Core.Audio;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Modulation;

/// <summary>The node's modulation sources (<see cref="ModSource"/> names them): the sound's analysis (live: this node's input), the
/// beat clock and LFOs (pure functions of leader time, identical on every node at the same instant). A smoothed source
/// ("…~attack/release", or a binding's SMOOTH) runs a one-pole filter on a fixed 5 ms grid of leader time — never per render frame,
/// so 50, 60 or 144 frames a second read the same value at the same instant — as two filters restarted in turn at absolute
/// multiples of a span H (≥ 2 s and ≥ 16 time constants), the output taken from the one that has run longer: its value depends
/// only on the source since its restart, so an instance that starts late (a second reader, a node joining) replays that span and
/// agrees. Audio sources are missing (TryRead false, 0.5) while the node listens to nothing, and read silence when the newest frame
/// is more than 1 s old. One instance per reading thread: nothing here is locked, and nothing is allocated once a source is
/// resolved.</summary>
public sealed class ModulationBus : IModulationBus, IAudioFrames
{
    public const long TickNs = 5_000_000;
    public const long StaleNs = 1_000_000_000;
    private const long MinSpanTicks = 400;
    private const long MaxPureSpanTicks = 12_000;
    private const long MaxAudioSpanTicks = 1_200;

    private readonly AudioFeed? _feed;
    private readonly Func<TempoTimeline> _tempo;
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private readonly List<Channel> _channels = [];
    private readonly AnalysisFrame _frame = new();
    private long _frameIndex = -1;
    private long _frameRingCount = -1;

    /// <param name="feed">The node's sound (null: a node without audio).</param>
    /// <param name="tempo">The beat clock now (read per call; the lane swaps it whole).</param>
    public ModulationBus(AudioFeed? feed, Func<TempoTimeline> tempo)
    {
        _feed = feed;
        _tempo = tempo ?? throw new ArgumentNullException(nameof(tempo));
    }

    /// <summary>The leader instant of the frame being drawn (set by <see cref="BeginFrame"/>).</summary>
    public long FrameNs { get; private set; }

    /// <summary>True while the node listens to an input.</summary>
    public bool AudioPresent => _feed is { Listening: true };

    public void BeginFrame(long leaderNs) => FrameNs = leaderNs;

    public int Resolve(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_slots.TryGetValue(source, out var slot))
            return slot;
        if (!ModSource.TryParse(source, out var parsed, out _))
            return -1;
        slot = _channels.Count;
        _channels.Add(new Channel(parsed));
        _slots[source] = slot;
        return slot;
    }

    public bool TryRead(string source, long leaderNs, out double value01)
    {
        var slot = Resolve(source);
        if (slot < 0)
        {
            value01 = 0.5;
            return false;
        }
        return TryRead(slot, leaderNs, out value01);
    }

    public bool TryRead(int slot, long leaderNs, out double value01)
    {
        if (slot < 0 || slot >= _channels.Count)
        {
            value01 = 0.5;
            return false;
        }
        var c = _channels[slot];
        if (c.Source.IsAudio && !AudioPresent)
        {
            value01 = 0.5;
            return false;
        }
        value01 = c.Source.IsSmoothed ? Smoothed(c, leaderNs) : Raw(c, leaderNs);
        return true;
    }

    public bool TryReadRaw(string source, long leaderNs, out double value)
    {
        var slot = Resolve(source);
        if (slot < 0 || !TryRead(slot, leaderNs, out value))
        {
            value = 0;
            return false;
        }
        var s = _channels[slot].Source;
        if (s.Kind == ModSourceKind.Bpm)
            value = _tempo().BpmAt(leaderNs);
        else if (s.Kind == ModSourceKind.Peak)
            value = ReadFrame(leaderNs) ? _frame.PeakDb : -120;
        return true;
    }

    public bool IsLive(string source) => ModSource.TryParse(source, out var s, out _) && s.IsAudio;

    public SourceDeterminism Determinism(string source) =>
        ModSource.TryParse(source, out var s, out _) ? s.Determinism : SourceDeterminism.Live;

    // ---- IAudioFrames ----

    public int Channels => 1;

    public bool TryWave(long leaderNs, int samples, Span<float> interleaved)
    {
        if (_feed?.Listener is not { } listener || samples <= 0 || interleaved.Length < samples)
            return false;
        return listener.TryWave(leaderNs, interleaved[..samples], StaleNs);
    }

    public bool TrySpectrum(long leaderNs, int bins, Span<float> magnitudes01)
    {
        if (!AudioPresent || bins <= 0 || magnitudes01.Length < bins || !ReadFrame(leaderNs))
            return false;
        var spectrum = _frame.Spectrum;
        for (var i = 0; i < bins; i++)
        {
            var pos = bins == 1 ? 0 : i * (spectrum.Length - 1) / (double)(bins - 1);
            var k = (int)Math.Floor(pos);
            var t = (float)(pos - k);
            magnitudes01[i] = k + 1 < spectrum.Length ? spectrum[k] * (1 - t) + spectrum[k + 1] * t : spectrum[k];
        }
        return true;
    }

    // ---- values ----

    /// <summary>A source's unsmoothed value at an instant.</summary>
    private double Raw(Channel c, long t)
    {
        var s = c.Source;
        if (s.IsAudio)
            return ReadFrame(t) ? AudioValue(s, t) : 0;
        var tempo = _tempo();
        return s.Kind switch
        {
            ModSourceKind.Beat => tempo.PhasesAt(t).Beat,
            ModSourceKind.Bar => tempo.PhasesAt(t).Bar,
            ModSourceKind.Phrase => tempo.PhasesAt(t).Phrase,
            ModSourceKind.BeatPulse => Pulse(tempo, t, 1, s.PulseMs),
            ModSourceKind.BarPulse => Pulse(tempo, t, tempo.BeatsPerBar, s.PulseMs),
            ModSourceKind.Bpm => Math.Clamp(tempo.BpmAt(t) / 240, 0, 1),
            _ => Lfo(s, c.Seed, tempo, t),
        };
    }

    private double AudioValue(ModSource s, long t)
    {
        var f = _frame;
        return s.Kind switch
        {
            ModSourceKind.Level => s.Normalised ? f.LevelN : f.Level,
            ModSourceKind.Bass => s.Normalised ? f.BassN : f.Bass,
            ModSourceKind.Mid => s.Normalised ? f.MidN : f.Mid,
            ModSourceKind.High => s.Normalised ? f.HighN : f.High,
            ModSourceKind.Peak => Math.Clamp((f.PeakDb + 60) / 60.0, 0, 1),
            ModSourceKind.Flux => f.Flux,
            ModSourceKind.Centroid => f.Centroid,
            ModSourceKind.Onset => f.LastOnsetLeaderNs == long.MinValue || t < f.LastOnsetLeaderNs ? 0
                : Math.Exp(-(t - f.LastOnsetLeaderNs) / (ModSource.OnsetDecayMs * 1e6)),
            ModSourceKind.Band => s.Normalised ? f.BandsN[s.Index] : f.Bands[s.Index],
            _ => f.Spectrum[s.Index],
        };
    }

    /// <summary>Puts the frame at <paramref name="t"/> in <see cref="_frame"/>; false when there is none or it is stale.</summary>
    private bool ReadFrame(long t)
    {
        if (_feed is null)
            return false;
        var ring = _feed.Ring;
        var count = ring.Count;
        if (count < _frameRingCount)
            _frameIndex = -1; // the ring was cleared: the kept copy is of another input
        _frameRingCount = count;
        var index = ring.IndexAt(t);
        if (index < 0)
            return false;
        if (index != _frameIndex)
        {
            if (!ring.TryRead(index, _frame))
            {
                _frameIndex = -1;
                return false;
            }
            _frameIndex = index;
        }
        return t - _frame.LeaderNs <= StaleNs;
    }

    private static double Pulse(TempoTimeline tempo, long t, int beats, double decayMs)
    {
        var a = tempo.At(t);
        var b = a.BeatAt(t);
        var last = Math.Floor(b / beats + 1e-9) * beats;
        var dt = t - a.InstantOf(last);
        return dt < 0 ? 1 : Math.Exp(-dt / (decayMs * 1e6));
    }

    private static double Lfo(ModSource s, ulong seed, TempoTimeline tempo, long t)
    {
        var time = s.PeriodInBeats ? tempo.BeatAt(t) : t / 1e9;
        var cycles = time / s.Period + s.Phase;
        var x = cycles - Math.Floor(cycles);
        return s.Shape switch
        {
            LfoShape.Triangle => x < 0.5 ? 2 * x : 2 - 2 * x,
            LfoShape.Saw => x,
            LfoShape.Ramp => 1 - x,
            LfoShape.Square => x < 0.5 ? 1 : 0,
            LfoShape.Pulse => x < 0.1 ? 1 : 0,
            LfoShape.Random => new Pcg32(seed ^ (ulong)(long)Math.Floor(cycles)).NextDouble(),
            _ => 0.5 - 0.5 * Math.Cos(2 * Math.PI * x),
        };
    }

    // ---- smoothing on the tick grid ----

    private double Smoothed(Channel c, long t)
    {
        var k = FloorDiv(t, TickNs);
        if (c.LastTick == long.MinValue || k < c.LastTick || k - c.LastTick > c.Span)
            WarmStart(c, k);
        else
            while (c.LastTick < k)
                Step(c, c.LastTick + 1);
        return Age(k, 0, c.Span) >= Age(k, c.Span / 2, c.Span) ? c.A : c.B;
    }

    /// <summary>Replays from the older of the two filters' last restarts to tick <paramref name="k"/>.</summary>
    private void WarmStart(Channel c, long k)
    {
        var start = k - Math.Max(Age(k, 0, c.Span), Age(k, c.Span / 2, c.Span));
        var x = Raw(c, start * TickNs);
        c.A = x;
        c.B = x;
        c.LastTick = start - 1;
        while (c.LastTick < k)
            Step(c, c.LastTick + 1);
    }

    private void Step(Channel c, long k)
    {
        var x = Raw(c, k * TickNs);
        c.A = Age(k, 0, c.Span) == 0 ? x : Follow(c, c.A, x);
        c.B = Age(k, c.Span / 2, c.Span) == 0 ? x : Follow(c, c.B, x);
        c.LastTick = k;
    }

    private static double Follow(Channel c, double y, double x) => y + (x - y) * (x > y ? c.AttackCoef : c.ReleaseCoef);

    /// <summary>Ticks since the last restart of a filter that restarts at offset + n·span.</summary>
    private static long Age(long k, long offset, long span)
    {
        var a = (k - offset) % span;
        return a < 0 ? a + span : a;
    }

    private static long FloorDiv(long a, long b)
    {
        var q = a / b;
        return a % b != 0 && (a < 0) != (b < 0) ? q - 1 : q;
    }

    /// <summary>One resolved source and, when it is smoothed, its two filters.</summary>
    private sealed class Channel
    {
        public Channel(ModSource source)
        {
            Source = source;
            var tau = Math.Max(source.AttackMs, source.ReleaseMs) * 1e6 / TickNs;
            var cap = source.IsAudio ? MaxAudioSpanTicks : MaxPureSpanTicks;
            Span = Math.Clamp((long)Math.Ceiling(16 * tau), MinSpanTicks, cap) & ~1L;
            AttackCoef = source.AttackMs <= 0 ? 1 : 1 - Math.Exp(-TickNs / (source.AttackMs * 1e6));
            ReleaseCoef = source.ReleaseMs <= 0 ? 1 : 1 - Math.Exp(-TickNs / (source.ReleaseMs * 1e6));
            Seed = source.Kind == ModSourceKind.Lfo ? ModSource.StableHash(source.BaseName) : 0;
        }

        public ModSource Source { get; }

        /// <summary>A random LFO's seed: the hash of its name without smoothing, so every node draws the same values (taken once,
        /// so a read allocates nothing).</summary>
        public ulong Seed { get; }

        public long Span { get; }

        public double AttackCoef { get; }

        public double ReleaseCoef { get; }

        public long LastTick { get; set; } = long.MinValue;

        public double A { get; set; }

        public double B { get; set; }
    }
}
