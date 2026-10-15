using System.Collections.Immutable;

namespace PlayMap.Core.Audio;

/// <summary>Where the beat stands from a leader instant on: beat(t) = Beat + (t − LeaderNs)·Bpm/60e9. Seq orders the anchors a
/// node has made; Source says what made it ("start", "set", "tap", "resync", "nudge", "meter").</summary>
public readonly record struct TempoAnchor(long LeaderNs, double Beat, double Bpm, long Seq, string Source)
{
    public double BeatAt(long leaderNs) => Beat + (leaderNs - LeaderNs) * Bpm / 60e9;

    /// <summary>The leader instant a beat count is reached (on this anchor's line).</summary>
    public long InstantOf(double beat) => LeaderNs + (long)Math.Round((beat - Beat) * 60e9 / Bpm);
}

/// <summary>The beat clock as anchors: a pure function of leader time, so every node holding the same anchors reads the same beat,
/// bar and phrase at the same instant, whatever its frame rate. Every change is a new anchor at an instant at least
/// <see cref="LeadNs"/> ahead of the moment it is made, so a peer holds it before it applies; nothing is integrated frame by frame.
/// Immutable: the lane makes a new timeline per change and the render thread reads whichever one it holds.</summary>
public sealed record TempoTimeline(ImmutableArray<TempoAnchor> Anchors, int BeatsPerBar, int BarsPerPhrase)
{
    /// <summary>How far ahead a change is scheduled: 40 ms.</summary>
    public const long LeadNs = 40_000_000;

    /// <summary>How far back the clock keeps its anchors: a smoothed beat source replays up to a minute, and every node must read the
    /// same beat at any instant it replays.</summary>
    public const long HistoryNs = 60_000_000_000;

    /// <summary>The largest share a phase correction changes the tempo by while it slews.</summary>
    public const double MaxSlew = 0.02;

    /// <summary>A clock at <paramref name="bpm"/> with beat 0 at leader 0: the same on every node from the start.</summary>
    public static TempoTimeline Start(double bpm, int beatsPerBar = 4, int barsPerPhrase = 8) =>
        new([new TempoAnchor(0, 0, bpm, 0, "start")], beatsPerBar, barsPerPhrase);

    /// <summary>The anchor in force at <paramref name="leaderNs"/>: the last one at or before it (the first before any).</summary>
    public TempoAnchor At(long leaderNs)
    {
        var a = Anchors[0];
        for (var i = 1; i < Anchors.Length; i++)
        {
            if (Anchors[i].LeaderNs > leaderNs)
                break;
            a = Anchors[i];
        }
        return a;
    }

    public double BeatAt(long leaderNs) => At(leaderNs).BeatAt(leaderNs);

    public double BpmAt(long leaderNs) => At(leaderNs).Bpm;

    /// <summary>The newest anchor's seq.</summary>
    public long Seq => Anchors[^1].Seq;

    /// <summary>The tempo the clock settles on: the last anchor's (a slew's end, a pending change).</summary>
    public double Bpm => Anchors[^1].Bpm;

    /// <summary>Phase 0–1 within the beat, the bar and the phrase.</summary>
    public (double Beat, double Bar, double Phrase) PhasesAt(long leaderNs)
    {
        var b = BeatAt(leaderNs);
        var bar = b / BeatsPerBar;
        var phrase = bar / BarsPerPhrase;
        return (Frac(b), Frac(bar), Frac(phrase));
    }

    /// <summary>The beat count, the beat in its bar (1-based), the bar count and the bar in its phrase (1-based) at an instant.</summary>
    public (long Beat, int BeatInBar, long Bar, int BarInPhrase, long Phrase) CountsAt(long leaderNs)
    {
        var b = (long)Math.Floor(BeatAt(leaderNs));
        var bar = FloorDiv(b, BeatsPerBar);
        var phrase = FloorDiv(bar, BarsPerPhrase);
        return (b, (int)(b - bar * BeatsPerBar) + 1, bar, (int)(bar - phrase * BarsPerPhrase) + 1, phrase);
    }

    /// <summary>A tempo change at <paramref name="atNs"/>: the beat carries on continuously from where the clock stands then. Anchors
    /// after the instant are dropped (a later change replaces a pending one).</summary>
    public TempoTimeline WithTempo(long atNs, double bpm, string source) =>
        Append(new TempoAnchor(atNs, BeatAt(atNs), bpm, Seq + 1, source));

    /// <summary>The beat count jumps at <paramref name="atNs"/> to <paramref name="beat"/> (a resync), at the clock's tempo.</summary>
    public TempoTimeline WithBeat(long atNs, double beat, string source) =>
        Append(new TempoAnchor(atNs, beat, At(atNs).Bpm, Seq + 1, source));

    /// <summary>Moves the beat's phase by <paramref name="shiftNs"/> (positive: beats land earlier) from <paramref name="atNs"/> on,
    /// slewed: the tempo changes by at most <see cref="MaxSlew"/> for as many whole bars as that takes, then returns, so beat time
    /// never runs backwards and never jumps.</summary>
    public TempoTimeline WithNudge(long atNs, long shiftNs, string source)
    {
        var bpm = At(atNs).Bpm;
        var barNs = BeatsPerBar * 60e9 / bpm;
        var bars = Math.Max(1, (int)Math.Ceiling(Math.Abs(shiftNs) / (MaxSlew * barNs)));
        var lengthNs = (long)Math.Round(bars * barNs);
        var factor = 1 + shiftNs / (double)lengthNs;
        var slewing = WithTempo(atNs, bpm * factor, source);
        return slewing.WithTempo(atNs + lengthNs, bpm, source);
    }

    /// <summary>A new meter (beats per bar, bars per phrase); the beat count is unchanged.</summary>
    public TempoTimeline WithMeter(int beatsPerBar, int barsPerPhrase) => this with { BeatsPerBar = beatsPerBar, BarsPerPhrase = barsPerPhrase };

    /// <summary>The next instant at or after <paramref name="fromNs"/> where the beat count is a whole multiple of
    /// <paramref name="beats"/> (1 = a beat, a bar's beats = a bar line …), on the line of whichever anchor is in force then (a
    /// change scheduled before the line moves it).</summary>
    public long NextBoundary(long fromNs, int beats)
    {
        var t = fromNs;
        for (var i = 0; i <= Anchors.Length; i++)
        {
            var a = At(t);
            var next = Math.Ceiling(a.BeatAt(t) / beats - 1e-9) * beats;
            var at = Math.Max(t, a.InstantOf(next));
            var later = Anchors.Where(x => x.LeaderNs > t && x.LeaderNs <= at).Select(x => x.LeaderNs).DefaultIfEmpty(long.MinValue).Min();
            if (later == long.MinValue)
                return at;
            t = later;
        }
        return t;
    }

    /// <summary>The clock with the anchors of more than <see cref="HistoryNs"/> ago dropped (what a node keeps).</summary>
    public TempoTimeline Kept(long nowNs) => Pruned(nowNs - HistoryNs);

    /// <summary>Drops anchors no instant at or after <paramref name="nowNs"/> reads (keeps the one in force and every later one).</summary>
    public TempoTimeline Pruned(long nowNs)
    {
        var keepFrom = 0;
        for (var i = 1; i < Anchors.Length; i++)
            if (Anchors[i].LeaderNs <= nowNs)
                keepFrom = i;
        return keepFrom == 0 ? this : this with { Anchors = Anchors[keepFrom..] };
    }

    private TempoTimeline Append(TempoAnchor anchor)
    {
        var kept = Anchors.Where(a => a.LeaderNs < anchor.LeaderNs).ToImmutableArray();
        if (kept.IsEmpty)
            kept = [Anchors[0]];
        return this with { Anchors = kept.Add(anchor) };
    }

    private static double Frac(double v) => v - Math.Floor(v);

    private static long FloorDiv(long a, long b) => (long)Math.Floor(a / (double)b);

    public bool Equals(TempoTimeline? other) =>
        other is not null && BeatsPerBar == other.BeatsPerBar && BarsPerPhrase == other.BarsPerPhrase && Anchors.SequenceEqual(other.Anchors);

    public override int GetHashCode() => HashCode.Combine(BeatsPerBar, BarsPerPhrase, Anchors.Length, Seq);
}

/// <summary>Tap tempo: the last eight intervals of a series, those more than 30 % off their median left out (a late or a doubled
/// tap), and the tempo from the mean of the rest — the median says what belongs, the mean of what belongs averages the hand's
/// jitter. A gap of more than 2 s starts a new series.</summary>
public sealed class TapTempo
{
    public const long ResetNs = 2_000_000_000;
    private const int Keep = 8;
    private const double Outlier = 0.30;

    private readonly List<long> _intervals = [];
    private long _last = long.MinValue;

    /// <summary>Taps in the current series.</summary>
    public int Taps { get; private set; }

    /// <summary>The instant of the series' first tap.</summary>
    public long FirstTapNs { get; private set; }

    /// <summary>Takes a tap; the tempo once two or more taps are in the series, else null.</summary>
    public double? Tap(long atNs)
    {
        if (_last == long.MinValue || atNs - _last > ResetNs || atNs <= _last)
        {
            _intervals.Clear();
            _last = atNs;
            Taps = 1;
            FirstTapNs = atNs;
            return null;
        }
        _intervals.Add(atNs - _last);
        if (_intervals.Count > Keep)
            _intervals.RemoveAt(0);
        _last = atNs;
        Taps++;
        var median = Median(_intervals);
        var kept = _intervals.Where(i => Math.Abs(i - median) <= Outlier * median).ToList();
        return 60e9 / (kept.Count == 0 ? median : kept.Average());
    }

    public void Reset()
    {
        _intervals.Clear();
        _last = long.MinValue;
        Taps = 0;
    }

    private static double Median(List<long> values)
    {
        var sorted = values.Order().ToArray();
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
