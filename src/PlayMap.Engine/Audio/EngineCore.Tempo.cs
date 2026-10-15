using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Audio;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Core.Time;

namespace PlayMap.Engine;

/// <summary>The TEMPO verbs. Every change is an anchor one lead (40 ms) ahead of the line's arrival — or at the instant AT names —
/// so the beat on every node that holds the anchor moves at the same leader instant; the show's tempo and meter are project
/// settings (undoable), and the clock follows them. A change that would change nothing is refused with the reason.</summary>
public sealed partial class EngineCore
{
    private const double MinBpm = 20;
    private const double MaxBpm = 999;
    private const double NudgeShare = 0.04;
    private static readonly long NudgeHoldNs = 2_000_000_000;

    private readonly TapTempo _tap = new();

    private void RouteTempo(VerbRoutes routes)
    {
        routes.Lane("TEMPO STATUS", _ => CommandReply.Success(TempoJson()));
        routes.Lane("TEMPO SET", c => TempoSet(c.Args));
        routes.Lane("TEMPO TAP", c => TempoTap(c.Args));
        routes.Lane("TEMPO RESYNC", c => TempoResync(c.Args));
        routes.Lane("TEMPO NUDGE", c => TempoNudge(c.Args));
        routes.Lane("TEMPO HALF", _ => TempoScale(0.5, "half"));
        routes.Lane("TEMPO DOUBLE", _ => TempoScale(2, "double"));
        routes.Lane("TEMPO SOURCE", c => TempoSource(c.Args));
        routes.Lane("TEMPO METER", c => TempoMeter(c.Args));
    }

    /// <summary>The beat clock now: tempo, counts and phases at the leader's now, the meter, the source, and the anchors in force
    /// and pending.</summary>
    internal string TempoJson()
    {
        var now = LeaderNowNs;
        var clock = AudioRt.Tempo;
        var counts = clock.CountsAt(now);
        var phases = clock.PhasesAt(now);
        var t = Store.Project.Tempo;
        return JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("bpm", Math.Round(clock.BpmAt(now), 6));
            w.WriteNumber("settlesAt", Math.Round(clock.Bpm, 6));
            w.WriteNumber("beat", counts.Beat);
            w.WriteNumber("beatInBar", counts.BeatInBar);
            w.WriteNumber("bar", counts.Bar);
            w.WriteNumber("barInPhrase", counts.BarInPhrase);
            w.WriteNumber("phrase", counts.Phrase);
            w.WriteNumber("phase", Math.Round(phases.Beat, 6));
            w.WriteNumber("barPhase", Math.Round(phases.Bar, 6));
            w.WriteNumber("phrasePhase", Math.Round(phases.Phrase, 6));
            w.WriteNumber("beatsPerBar", clock.BeatsPerBar);
            w.WriteNumber("barsPerPhrase", clock.BarsPerPhrase);
            w.WriteString("source", t.Source);
            w.WriteString("lock", TempoLockNow);
            w.WriteNumber("confidence", t.Source == "manual" ? 1 : TempoConfidenceNow);
            w.WriteNumber("leaderNs", now);
            w.WriteStartArray("anchors");
            foreach (var a in clock.Pruned(now).Anchors)
            {
                w.WriteStartObject();
                w.WriteNumber("leaderNs", a.LeaderNs);
                w.WriteNumber("beat", Math.Round(a.Beat, 9));
                w.WriteNumber("bpm", Math.Round(a.Bpm, 6));
                w.WriteNumber("seq", a.Seq);
                w.WriteString("source", a.Source);
                w.WriteBoolean("pending", a.LeaderNs > now);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private CommandReply TempoSet(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("TEMPO SET <20-999>|+<n>|-<n> [AT +<ms>|<unix ms>|BEAT|BAR|PHRASE]");
        var clock = AudioRt.Tempo;
        var text = args[0];
        var relative = text.StartsWith('+') || text.StartsWith('-');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            return CommandReply.Fail($"'{text}' is not a tempo: 20 to 999 BPM, or +n / -n to move it");
        var bpm = Math.Round(relative ? clock.Bpm + value : value, 3);
        if (bpm is < MinBpm or > MaxBpm)
            return CommandReply.Fail(Inv($"the tempo is 20 to 999 BPM (that would be {bpm:0.###})"));
        if (Math.Abs(bpm - clock.Bpm) < 1e-9)
            return CommandReply.Fail(Inv($"the tempo is already {bpm:0.###} BPM"));
        if (TempoAt(args.Skip(1).ToList(), out var why) is not { } at)
            return CommandReply.Fail(why);
        return ChangeTempo(bpm, at, "set");
    }

    private CommandReply TempoScale(double factor, string word)
    {
        var bpm = Math.Round(AudioRt.Tempo.Bpm * factor, 3);
        if (bpm is < MinBpm or > MaxBpm)
            return CommandReply.Fail(Inv($"{word} of {AudioRt.Tempo.Bpm:0.###} BPM is {bpm:0.###}, outside 20 to 999"));
        return ChangeTempo(bpm, LeaderNowNs + TempoTimeline.LeadNs, word);
    }

    /// <summary>Writes the show's tempo and schedules it on the clock at <paramref name="at"/> (continuous beat).</summary>
    private CommandReply ChangeTempo(double bpm, long at, string source)
    {
        var r = Edits.Apply(Inv($"tempo {bpm:0.###} BPM"), p => p with { Tempo = p.Tempo with { Bpm = bpm } }, coalesceKey: "tempo bpm");
        if (!r.Changed)
            return FromEdit(r);
        if (ReferenceEquals(Edits, Store))
            AudioRt.Tempo = AudioRt.Tempo.Kept(LeaderNowNs).WithTempo(at, bpm, source);
        TempoInUse();
        return CommandReply.Success(AnchorJson(at), Inv($"tempo {bpm:0.###} BPM from {WhenWords(at)}"));
    }

    private CommandReply TempoTap(List<string> args)
    {
        var at = LeaderNowNs;
        if (args.Count > 0)
        {
            if (args.Count != 2 || !args[0].Equals("AT", StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var unix) || LeaderAtUnix(unix) is not { } tapAt)
                return CommandReply.Fail("TEMPO TAP [AT <unix ms>] — the press time on this node's show clock, within a day");
            at = Math.Min(tapAt, LeaderNowNs);
        }
        var bpm = _tap.Tap(at);
        TempoInUse();
        if (bpm is not { } tapped)
            return CommandReply.Success(JsonOut(w => { w.WriteStartObject(); w.WriteNumber("taps", _tap.Taps); w.WriteEndObject(); }),
                "1 tap — tap again on the beat");
        var rounded = Math.Round(Math.Clamp(tapped, MinBpm, MaxBpm), 2);
        // The taps fall on beats: the last one is a whole beat, and the tempo they give runs from one lead after the line.
        var clock = AudioRt.Tempo.Kept(LeaderNowNs);
        var effective = LeaderNowNs + TempoTimeline.LeadNs;
        var beatAtTap = Math.Round(clock.BeatAt(at));
        var beatAtEffective = beatAtTap + (effective - at) * rounded / 60e9;
        if (Math.Abs(rounded - Store.Project.Tempo.Bpm) > 1e-9)
        {
            var r = Edits.Apply(Inv($"tempo {rounded:0.##} BPM (tapped)"), p => p with { Tempo = p.Tempo with { Bpm = rounded } }, coalesceKey: "tempo bpm");
            if (!r.Changed)
                return FromEdit(r);
        }
        if (ReferenceEquals(Edits, Store))
            AudioRt.Tempo = clock.WithBeat(effective, beatAtEffective, "tap").WithTempo(effective, rounded, "tap");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("taps", _tap.Taps);
            w.WriteNumber("bpm", rounded);
            w.WriteNumber("atLeaderNs", effective);
            w.WriteEndObject();
        }), Inv($"{_tap.Taps} taps: {rounded:0.##} BPM, the last tap on the beat"));
    }

    private CommandReply TempoResync(List<string> args)
    {
        var rest = new List<string>(args);
        var press = LeaderNowNs;
        var atIndex = rest.FindIndex(a => a.Equals("AT", StringComparison.OrdinalIgnoreCase));
        if (atIndex >= 0)
        {
            if (atIndex + 1 >= rest.Count || !long.TryParse(rest[atIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
                || LeaderAtUnix(unix) is not { } pressAt)
                return CommandReply.Fail("TEMPO RESYNC [BEAT|BAR|PHRASE] [AT <unix ms>] — the press time on this node's show clock");
            press = Math.Min(pressAt, LeaderNowNs);
            rest.RemoveRange(atIndex, 2);
        }
        var clock = AudioRt.Tempo.Kept(LeaderNowNs);
        var unit = (rest.FirstOrDefault() ?? "BAR").ToUpperInvariant() switch
        {
            "BEAT" => 1,
            "BAR" => clock.BeatsPerBar,
            "PHRASE" => clock.BeatsPerBar * clock.BarsPerPhrase,
            _ => 0,
        };
        if (unit == 0 || rest.Count > 1)
            return CommandReply.Fail("TEMPO RESYNC [BEAT|BAR|PHRASE] [AT <unix ms>]");
        var word = unit == 1 ? "beat" : unit == clock.BeatsPerBar ? "bar" : "phrase";
        var b = clock.BeatAt(press);
        // The press is the next line of the unit (never a step back in the count); one that falls on it already changes nothing.
        var target = Math.Ceiling(b / unit - 1e-9) * unit;
        if (Math.Abs(target - b) * 60e9 / clock.BpmAt(press) < 1_000_000)
            return CommandReply.Fail($"the {word} already starts there");
        var effective = LeaderNowNs + TempoTimeline.LeadNs;
        var bpm = clock.BpmAt(effective);
        AudioRt.Tempo = clock.WithBeat(effective, target + (effective - press) * bpm / 60e9, "resync");
        TempoInUse();
        return CommandReply.Success(AnchorJson(effective), $"the {word} starts at the press, seen from {WhenWords(effective)}");
    }

    private CommandReply TempoNudge(List<string> args)
    {
        if (args.Count != 1)
            return CommandReply.Fail("TEMPO NUDGE <±1-200 ms>|FASTER|SLOWER|STOP");
        var clock = AudioRt.Tempo.Kept(LeaderNowNs);
        var at = LeaderNowNs + TempoTimeline.LeadNs;
        var bpm = Store.Project.Tempo.Bpm;
        var word = args[0].ToUpperInvariant();
        var pending = clock.Anchors.Any(a => a.LeaderNs > LeaderNowNs && a.Source == "nudge")
            || clock.At(LeaderNowNs).Source == "nudge" && Math.Abs(clock.BpmAt(LeaderNowNs) - bpm) > 1e-9;
        switch (word)
        {
            case "FASTER":
            case "SLOWER":
                var factor = word == "FASTER" ? 1 + NudgeShare : 1 - NudgeShare;
                AudioRt.Tempo = clock.WithTempo(at, bpm * factor, "nudge").WithTempo(at + NudgeHoldNs, bpm, "nudge");
                TempoInUse();
                return CommandReply.Success(AnchorJson(at), $"{word.ToLowerInvariant()} by 4 % for 2 s (TEMPO NUDGE STOP ends it)");
            case "STOP":
                if (!pending)
                    return CommandReply.Fail("no nudge is running");
                AudioRt.Tempo = clock.WithTempo(at, bpm, "nudge");
                return CommandReply.Success(AnchorJson(at), Inv($"the nudge ends: {bpm:0.###} BPM from {WhenWords(at)}"));
        }
        if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || !double.IsFinite(ms)
            || Math.Abs(ms) is < 1 or > 200)
            return CommandReply.Fail("TEMPO NUDGE takes ±1 to 200 ms (positive: the beat lands earlier), FASTER, SLOWER or STOP");
        AudioRt.Tempo = clock.WithNudge(at, (long)Math.Round(ms * 1e6), "nudge");
        TempoInUse();
        var end = AudioRt.Tempo.Anchors[^1].LeaderNs;
        return CommandReply.Success(AnchorJson(at), Inv($"the beat moves {ms:+0.#;-0.#} ms, slewed over {(end - at) / 1e9:0.##} s"));
    }

    private CommandReply TempoSource(List<string> args)
    {
        if (args.Count != 1 || args[0].ToLowerInvariant() is not ("manual" or "detect"))
            return CommandReply.Fail("TEMPO SOURCE MANUAL|DETECT");
        var source = args[0].ToLowerInvariant();
        if (Store.Project.Tempo.Source == source)
            return CommandReply.Fail($"the tempo source is already {source}");
        var r = Edits.Apply($"tempo source {source}", p => p with { Tempo = p.Tempo with { Source = source } });
        if (!r.Changed)
            return FromEdit(r);
        if (ReferenceEquals(Edits, Store))
            DetectionSource(source);
        TempoInUse();
        return CommandReply.Success(effect: source == "manual" ? "the beat comes from taps and TEMPO SET"
            : "the beat is detected from the sound: searching until it locks");
    }

    private CommandReply TempoMeter(List<string> args)
    {
        if (args.Count is < 1 or > 2
            || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var beats) || beats is < 1 or > 16)
            return CommandReply.Fail("TEMPO METER <beats per bar 1-16> [<bars per phrase 1-64>]");
        var t = Store.Project.Tempo;
        var bars = t.BarsPerPhrase;
        if (args.Count == 2 && (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out bars) || bars is < 1 or > 64))
            return CommandReply.Fail("bars per phrase is 1 to 64");
        if (beats == t.BeatsPerBar && bars == t.BarsPerPhrase)
            return CommandReply.Fail($"the meter is already {beats} beats a bar, {bars} bars a phrase");
        var r = Edits.Apply($"meter {beats}/{bars}", p => p with { Tempo = p.Tempo with { BeatsPerBar = beats, BarsPerPhrase = bars } });
        if (!r.Changed)
            return FromEdit(r);
        if (ReferenceEquals(Edits, Store))
            AudioRt.Tempo = AudioRt.Tempo.WithMeter(beats, bars);
        TempoInUse();
        return CommandReply.Success(effect: $"{beats} beats a bar, {bars} bars a phrase");
    }

    /// <summary>"AT +ms" (at least one lead ahead), "AT &lt;unix ms&gt;" (a future instant), "AT BEAT|BAR|PHRASE" (the next line
    /// of the unit one lead from now); absent: one lead from now.</summary>
    private long? TempoAt(List<string> args, out string why)
    {
        why = "";
        var now = LeaderNowNs;
        var earliest = now + TempoTimeline.LeadNs;
        if (args.Count == 0)
            return earliest;
        if (args.Count != 2 || !args[0].Equals("AT", StringComparison.OrdinalIgnoreCase))
        {
            why = "AT +<ms>, AT <unix ms>, or AT BEAT|BAR|PHRASE";
            return null;
        }
        var v = args[1];
        var clock = AudioRt.Tempo;
        switch (v.ToUpperInvariant())
        {
            case "BEAT": return clock.NextBoundary(earliest, 1);
            case "BAR": return clock.NextBoundary(earliest, clock.BeatsPerBar);
            case "PHRASE": return clock.NextBoundary(earliest, clock.BeatsPerBar * clock.BarsPerPhrase);
        }
        if (v.StartsWith('+') && double.TryParse(v.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms is >= 0 and <= 86_400_000)
            return Math.Max(earliest, now + (long)(ms * 1e6));
        if (long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) && LeaderAtUnix(unix) is { } at)
        {
            if (at < earliest)
            {
                why = "AT is at least 40 ms ahead, so every node holds the change before it applies";
                return null;
            }
            return at;
        }
        why = "AT +<ms>, AT <unix ms>, or AT BEAT|BAR|PHRASE";
        return null;
    }

    private string AnchorJson(long at) => JsonOut(w =>
    {
        var a = AudioRt.Tempo.At(at);
        w.WriteStartObject();
        w.WriteNumber("leaderNs", a.LeaderNs);
        w.WriteNumber("beat", Math.Round(a.Beat, 9));
        w.WriteNumber("bpm", Math.Round(a.Bpm, 6));
        w.WriteNumber("seq", a.Seq);
        w.WriteString("source", a.Source);
        w.WriteEndObject();
    });

    private string WhenWords(long leaderNs)
    {
        var ms = (leaderNs - LeaderNowNs) / (double)Durations.NsPerMs;
        return ms <= TempoTimeline.LeadNs / (double)Durations.NsPerMs + 0.5 ? "one lead ahead (40 ms)" : Inv($"{ms:0} ms from now");
    }
}
