using PlayMap.Core.Model;
using PlayMap.Core.Tc;

namespace PlayMap.Engine.Tc;

/// <summary>The show's timecode sources at run time: one tracker per source, fed from the inputs' own threads (each source under its
/// own lock) and read by the chase on the lane. Internal sources are exact lines run by TIMECODE INTERNAL. An input's level, its
/// open state and its words ride beside the reading.</summary>
public sealed class TimecodeBoard
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Keeps one entry per source of the show (a source whose kind, format or window changed starts afresh).</summary>
    public void Reconcile(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_gate)
        {
            foreach (var id in _entries.Keys.Where(k => project.FindTimecodeSource(k) is null).ToList())
                _entries.Remove(id);
            foreach (var s in project.Timecode.Sources)
            {
                if (_entries.TryGetValue(s.Id, out var e) && e.Source.Kind == s.Kind && e.Source.Format == s.Format && e.Source.FitWindowMs == s.FitWindowMs
                    && e.Source.Channel == s.Channel && e.Source.Device == s.Device)
                {
                    e.Source = s;
                    continue;
                }
                _entries[s.Id] = new Entry(s);
            }
        }
    }

    /// <summary>True while the board has this source as given (its kind, device and channel): an input made for a source that has
    /// since gone or moved to another device or channel feeds it nothing.</summary>
    public bool Holds(TimecodeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
            return _entries.TryGetValue(source.Id, out var e) && e.Source.Kind == source.Kind && e.Source.Device == source.Device
                && e.Source.Channel == source.Channel;
    }

    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_gate)
                return [.. _entries.Keys];
        }
    }

    private Entry? Find(string id)
    {
        lock (_gate)
            return _entries.GetValueOrDefault(id);
    }

    /// <summary>Observations of a source from its input (any thread).</summary>
    public void Observe(string sourceId, IEnumerable<TcObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (Find(sourceId) is not { } e)
            return;
        lock (e.Gate)
            foreach (var o in observations)
                e.Tracker.Observe(o);
    }

    public void Observe(string sourceId, TcObservation observation) => Observe(sourceId, [observation]);

    /// <summary>An input's state beside its code: open, its level and its words (null words keep the last).</summary>
    public void Input(string sourceId, bool open, double levelDbfs, string? words)
    {
        if (Find(sourceId) is not { } e)
            return;
        lock (e.Gate)
        {
            e.Open = open;
            e.LevelDbfs = levelDbfs;
            if (words is not null)
                e.InputWords = words;
        }
    }

    public void Fault(string sourceId, string words, long nowNs)
    {
        if (Find(sourceId) is not { } e)
            return;
        lock (e.Gate)
        {
            e.Open = false;
            e.InputWords = words;
            e.Tracker.Fault(words, nowNs);
        }
    }

    public void Silent(string sourceId, long nowNs)
    {
        if (Find(sourceId) is not { } e)
            return;
        lock (e.Gate)
            e.Tracker.Silent(nowNs);
    }

    public void Count(string sourceId, TcCounters counts)
    {
        if (Find(sourceId) is not { } e)
            return;
        lock (e.Gate)
            e.Tracker.Count(counts);
    }

    /// <summary>TIMECODE SOURCE RESET: the counts start again.</summary>
    public bool Reset(string sourceId)
    {
        if (Find(sourceId) is not { } e)
            return false;
        lock (e.Gate)
            e.Tracker.ResetCounts();
        return true;
    }

    /// <summary>A source's reading at an instant (dropout and loss judged then); an internal source's exact line.</summary>
    public TcReading ReadingOf(string sourceId, long nowNs)
    {
        if (Find(sourceId) is not { } e)
            return TcReading.None with { State = "none", Words = $"no source {sourceId}" };
        lock (e.Gate)
        {
            if (e.Source.Kind == TimecodeKind.Internal)
                return e.InternalReading(nowNs);
            e.Tracker.Tick(nowNs);
            return e.Tracker.Reading;
        }
    }

    /// <summary>An input's open state, level and words.</summary>
    public (bool Open, double LevelDbfs, string Words) InputOf(string sourceId)
    {
        if (Find(sourceId) is not { } e)
            return (false, -120, "");
        lock (e.Gate)
            return (e.Open, e.LevelDbfs, e.InputWords);
    }

    /// <summary>TIMECODE INTERNAL: play, stop, locate or rate an internal source; answers the words, or a refusal.</summary>
    public (bool Ok, string Words) Internal(string sourceId, string verb, long tcNs, double rate, long nowNs)
    {
        if (Find(sourceId) is not { } e)
            return (false, $"no source {sourceId}");
        lock (e.Gate)
        {
            if (e.Source.Kind != TimecodeKind.Internal)
                return (false, $"{e.Source.Name} is not an internal source");
            var format = e.Format;
            var at = e.InternalTcAt(nowNs);
            switch (verb)
            {
                case "PLAY":
                    if (e.Playing)
                        return (false, $"{e.Source.Name} is already rolling");
                    (e.AnchorMono, e.AnchorTc, e.Playing) = (nowNs, at, true);
                    return (true, $"{e.Source.Name} rolling from {format.Format(format.FrameOf(at))}");
                case "STOP":
                    if (!e.Playing)
                        return (false, $"{e.Source.Name} is already stopped");
                    (e.AnchorMono, e.AnchorTc, e.Playing) = (nowNs, at, false);
                    return (true, $"{e.Source.Name} stopped at {format.Format(format.FrameOf(at))}");
                case "LOCATE":
                    if (tcNs == at && !e.Playing)
                        return (false, $"{e.Source.Name} is already there");
                    (e.AnchorMono, e.AnchorTc) = (nowNs, tcNs);
                    e.Run++;
                    return (true, $"{e.Source.Name} at {format.Format(format.FrameOf(tcNs))}");
                default:
                    if (Math.Abs(e.Rate - rate) < 1e-9)
                        return (false, $"{e.Source.Name} already runs at {rate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}×");
                    (e.AnchorMono, e.AnchorTc, e.Rate) = (nowNs, at, rate);
                    return (true, $"{e.Source.Name} runs at {rate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}×");
            }
        }
    }

    private sealed class Entry
    {
        public Entry(TimecodeSource source)
        {
            Source = source;
            var configured = TcFormat.TryParse(source.Format, out var f) ? f : (TcFormat?)null;
            Format = configured ?? TcFormat.Fps25;
            Tracker = new TimecodeTracker(configured, source.FitWindowMs, source.Kind == TimecodeKind.Mtc ? 16 : 3);
        }

        public Lock Gate { get; } = new();
        public TimecodeSource Source { get; set; }
        public TimecodeTracker Tracker { get; }
        public TcFormat Format { get; }
        public bool Open { get; set; }
        public double LevelDbfs { get; set; } = -120;
        public string InputWords { get; set; } = "";
        public long AnchorMono { get; set; }
        public long AnchorTc { get; set; }
        public double Rate { get; set; } = 1;
        public bool Playing { get; set; }
        public long Run { get; set; }

        public long InternalTcAt(long nowNs) => Playing ? AnchorTc + (long)Math.Round((nowNs - AnchorMono) * Rate) : AnchorTc;

        public TcReading InternalReading(long nowNs)
        {
            var line = new TcLine(AnchorMono, AnchorTc, Playing ? Rate : 0, 0, 1, Run, 1, Playing);
            var frame = Format.FrameOf(InternalTcAt(nowNs));
            return new TcReading(Playing ? "rolling" : "parked", line, Format, Playing ? Rate * Format.Rate.Value : 0, nowNs, frame, Tracker.Reading.Counters, 0,
                Playing ? $"internal, rolling at {Format.Text} fps" : "internal, stopped");
        }
    }
}
