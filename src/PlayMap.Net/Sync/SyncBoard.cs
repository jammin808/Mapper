using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Sync;

namespace PlayMap.Net.Sync;

/// <summary>One output as the leader's board reads it: whose, which, its lock state and path, its measured refresh and ppm against
/// the nearest standard rate, its vblank phase against the group's reference output (µs, in (−P/2, P/2]) with the uncertainty of
/// that number (both members' clock uncertainty), when it next slips a refresh against the reference (s; null when it never
/// will, or the pair is locked), its missed vblanks of the last minute and board count, and whether it was heard in the last 5 s.</summary>
public sealed record SyncOutputRow(string Node, string Output, string State, string Path, double RefreshHz, double Ppm, double PhaseUs,
    double UncertaintyUs, double? NextSlipS, uint Missed60s, uint FrameCount, bool Heard, bool Local, double AirLatencyMs);

/// <summary>One output against the group's reference output: phase and its uncertainty (µs), the next slip (s), and whether the two
/// run at different rates (more than 500 ppm apart: 59.94 against 60 is 1001).</summary>
public sealed record SyncPairRow(string A, string B, double PhaseUs, double UncertaintyUs, double? NextSlipS, bool MixedRate)
{
    /// <summary>The nodes the two outputs are on (output ids repeat across nodes).</summary>
    public string ANode { get; init; } = "";

    public string BNode { get; init; } = "";
}

/// <summary>The board's picture at one instant: every output, every pair against the reference, the verdict in one word ("same
/// vblank", "free-running", "mixed rate", "unknown") with its light and words, the soonest slip and the group's air latency (the
/// largest any heard member reports).</summary>
public sealed record SyncPicture(ImmutableArray<SyncOutputRow> Outputs, ImmutableArray<SyncPairRow> Pairs, string Verdict, Light Light,
    string Words, bool MixedRate, int Nodes, double? SoonestSlipS, double GroupLatencyMs)
{
    public static readonly SyncPicture Empty = new([], [], "unknown", Light.Grey, "no output reported yet", false, 0, null, 0);
}

/// <summary>The leader's table of vblank reports, turned into numbers (framelock's measured software tier). Pure: reports and
/// instants in, a picture out; any thread (one lock).
/// <list type="bullet">
/// <item>Entries are keyed by (node, output hash); a report older than or equal to the last of its key is refused, unless that
/// key went unheard for 5 s (a member that restarted counts from one again). An entry unheard for 5 s reads "not heard".</item>
/// <item>The reference output is the first local one offered (the leader's own), else the first heard.</item>
/// <item>Phase of o against R: each one's vblank nearest the common instant, extrapolated along its own period; the difference
/// wrapped into (−P_R/2, P_R/2].</item>
/// <item>Relative rate δ = (P_o − P_R)/P_R moves the phase δ ns per ns: the next slip comes when it reaches ±P_R/2 — at
/// (P_R/2 − φ)/δ for δ &gt; 0, (φ + P_R/2)/|δ| for δ &lt; 0; none when |δ| &lt; 0.01 ppm or both are locked. (50 ppm at 60 Hz
/// slips every 5 min 33 s; 59.94 against 60 every 16.65 s.)</item>
/// <item>"Same vblank" when every heard output is locked and every locked pair measures as one: phase within both uncertainties
/// and <see cref="LockedPhaseSlackNs"/>, and board counts that agree (framelock 3.5).</item>
/// </list></summary>
public sealed class SyncBoard
{
    /// <summary>An entry unheard this long reads "not heard" (and its sequence may start again).</summary>
    public const long HeardWithinNs = 5_000_000_000;

    /// <summary>Two outputs further apart in rate than this are mixed (59.94 against 60 is 1001 ppm).</summary>
    public const double MixedRateLimit = 500e-6;

    /// <summary>A pair whose relative rate is under this never slips (0.01 ppm: once in more than two days at 60 Hz).</summary>
    public const double NoSlipBelow = 0.01e-6;

    /// <summary>A free-running pair that slips sooner than this while the show plays is amber (10 min).</summary>
    public const double SlipAmberS = 600;

    /// <summary>A locked pair reads one vblank only within both clocks' uncertainty and this much more (a software vblank timestamp's
    /// jitter): further apart, the two are on different lock chains whatever their drivers say.</summary>
    public const double LockedPhaseSlackNs = 1_000_000;

    private const long Count24 = 1 << 24;

    /// <summary>The standard rates, the 1001 family exact (a display at 60000/1001 Hz reads 0 ppm).</summary>
    private static readonly double[] StandardHz = [24000 / 1001.0, 24, 25, 30000 / 1001.0, 30, 48000 / 1001.0, 48, 50, 60000 / 1001.0, 60, 72, 75, 90, 100,
        120000 / 1001.0, 120, 144, 165, 180, 240];

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];

    /// <summary>An output joining the lock for longer than this is amber.</summary>
    public const long JoiningAmberNs = 5_000_000_000;

    private sealed record Entry(uint Node, uint Hash, string NodeText, VblankReport Report, long ReceivedMonoNs,
        double UncertaintyNs, bool Local)
    {
        /// <summary>Since when (local monotonic) the output has reported joining, or long.MinValue.</summary>
        public long JoiningSinceMonoNs { get; init; } = long.MinValue;
    }

    /// <summary>Names an output the board hears by hash (the leader's own, a member's from its subscription).</summary>
    private readonly Dictionary<(uint Node, uint Hash), string> _names = [];

    /// <summary>Reports refused (older than the last of their key).</summary>
    public long Stale { get; private set; }

    /// <summary>Gives an output its id for the board's rows (an unnamed one reads "#" and its hash in hex).</summary>
    public void Name(string nodeId, string outputId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(outputId);
        lock (_gate)
            _names[(VblankReport.NodeNumber(nodeId), VblankReport.HashId(outputId))] = outputId;
    }

    /// <summary>Takes a report heard at a local monotonic instant; its clock uncertainty is the sender's own (the report's), the
    /// leader's own outputs say 0. False when it is older than or equal to the last of its key.</summary>
    public bool Offer(in VblankReport report, long receivedMonoNs, bool local = false)
    {
        var clockUncertaintyNs = report.ClockUncertaintyUs * 1000.0;
        if (!report.IsValid)
            return false;
        var (node, hash) = (report.NodeId, report.OutputIdHash);
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Node == node && e.Hash == hash);
            if (i >= 0)
            {
                var old = _entries[i];
                var silent = receivedMonoNs - old.ReceivedMonoNs > HeardWithinNs;
                if (report.Seq <= old.Report.Seq && !silent)
                {
                    Stale++;
                    return false;
                }
                _entries[i] = old with
                {
                    Report = report, ReceivedMonoNs = receivedMonoNs, UncertaintyNs = Math.Max(0, clockUncertaintyNs), Local = local,
                    JoiningSinceMonoNs = report.State != LockState.Joining ? long.MinValue
                        : old.JoiningSinceMonoNs == long.MinValue ? receivedMonoNs : old.JoiningSinceMonoNs,
                };
                return true;
            }
            var entry = new Entry(node, hash, report.NodeText, report, receivedMonoNs, Math.Max(0, clockUncertaintyNs), local)
            {
                JoiningSinceMonoNs = report.State == LockState.Joining ? receivedMonoNs : long.MinValue,
            };
            // The leader's own outputs come first, so the first of them is the reference.
            if (local)
                _entries.Insert(_entries.FindLastIndex(e => e.Local) + 1, entry);
            else
                _entries.Add(entry);
            return true;
        }
    }

    /// <summary>Forgets the names a node's outputs were given (its subscription ended).</summary>
    public void Unname(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        var node = VblankReport.NodeNumber(nodeId);
        lock (_gate)
        {
            foreach (var key in _names.Keys.Where(k => k.Node == node).ToList())
                _names.Remove(key);
        }
    }

    /// <summary>Forgets every entry and name (a node that stops leading).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _names.Clear();
        }
    }

    /// <summary>The picture at a leader instant (phases are compared there) and a local monotonic instant (what is heard).
    /// <paramref name="playing"/> lights a near slip amber.</summary>
    public SyncPicture Picture(long nowLeaderNs, long nowMonoNs, bool playing = false)
    {
        List<Entry> entries;
        Dictionary<(uint, uint), string> names;
        lock (_gate)
        {
            entries = [.. _entries];
            names = new Dictionary<(uint, uint), string>(_names);
        }
        if (entries.Count == 0)
            return SyncPicture.Empty;
        var heard = entries.Where(e => nowMonoNs - e.ReceivedMonoNs <= HeardWithinNs).ToList();
        var reference = heard.FirstOrDefault(e => e.Local) ?? heard.FirstOrDefault();
        var rows = ImmutableArray.CreateBuilder<SyncOutputRow>(entries.Count);
        var pairs = ImmutableArray.CreateBuilder<SyncPairRow>();
        var mixed = false;
        double? soonest = null;
        double worstPhase = 0, worstUncertainty = 0;
        (Entry Entry, double PhaseUs, bool CountsAgree)? apart = null;
        string Label(Entry e) => names.TryGetValue((e.Node, e.Hash), out var id) ? id : "#" + e.Hash.ToString("x8", CultureInfo.InvariantCulture);
        foreach (var e in entries)
        {
            var r = e.Report;
            var isHeard = heard.Contains(e);
            var refreshHz = 1e12 / r.PeriodPs;
            var nominal = Nominal(refreshHz);
            var ppm = (r.PeriodNs - 1e9 / nominal) / (1e9 / nominal) * 1e6;
            double phaseUs = 0, uncertaintyUs = e.UncertaintyNs / 1000;
            double? slip = null;
            if (isHeard && reference is not null && !ReferenceEquals(e, reference))
            {
                var pr = reference.Report.PeriodNs;
                var phase = Phase(reference.Report, r, nowLeaderNs);
                var delta = (r.PeriodNs - pr) / pr;
                var pairMixed = Math.Abs(r.PeriodNs / pr - 1) > MixedRateLimit;
                // Locked on both sides means one vblank only when the measurement agrees: the phase within both uncertainties and a
                // timestamp's jitter, and (with boards) the counts one count per period apart.
                var counts = CountsAgree(reference.Report, r);
                var locked = Locked(r.State) && Locked(reference.Report.State);
                if (locked && (Math.Abs(phase) > e.UncertaintyNs + reference.UncertaintyNs + LockedPhaseSlackNs || !counts))
                {
                    apart ??= (e, phase / 1000, counts);
                    locked = false;
                }
                slip = locked || Math.Abs(delta) < NoSlipBelow ? null
                    : delta > 0 ? (pr / 2 - phase) / delta / 1e9 : (phase + pr / 2) / -delta / 1e9;
                phaseUs = phase / 1000;
                uncertaintyUs = (e.UncertaintyNs + reference.UncertaintyNs) / 1000;
                mixed |= pairMixed;
                pairs.Add(new SyncPairRow(Label(reference), Label(e), Round(phaseUs, 3), Round(uncertaintyUs, 3), slip is { } s ? Round(s, 3) : null, pairMixed)
                {
                    ANode = reference.NodeText,
                    BNode = e.NodeText,
                });
                if (Math.Abs(phaseUs) > Math.Abs(worstPhase))
                    worstPhase = phaseUs;
                worstUncertainty = Math.Max(worstUncertainty, uncertaintyUs);
                if (slip is { } sl && (soonest is null || sl < soonest))
                    soonest = sl;
            }
            rows.Add(new SyncOutputRow(e.NodeText, Label(e), FrameLockMachine.Word(r.State), r.PathWord, Round(refreshHz, 6), Round(ppm, 2),
                Round(phaseUs, 3), Round(uncertaintyUs, 3), slip is { } x ? Round(x, 3) : null, r.Missed60s, r.BoardFrameCount, isHeard, e.Local,
                r.AirLatencyUs / 1000.0));
        }
        var nodes = heard.Select(e => e.Node).Distinct().Count();
        var latency = heard.Count == 0 ? 0 : heard.Max(e => e.Report.AirLatencyUs) / 1000.0;
        var count = $"{Plural(nodes, "node")}, {Plural(heard.Count, "output")}";
        string verdict, words;
        Light light;
        var fell = heard.FirstOrDefault(e => e.Report.State == LockState.FellOut);
        var joining = heard.FirstOrDefault(e => e.JoiningSinceMonoNs != long.MinValue && nowMonoNs - e.JoiningSinceMonoNs > JoiningAmberNs);
        if (heard.Count == 0)
        {
            (verdict, light, words) = ("unknown", Light.Grey, $"{Plural(entries.Count, "output")} known, none heard in the last 5 s");
        }
        else if (fell is not null)
        {
            (verdict, light, words) = ("free-running", Light.Red, $"{count}: {Label(fell)} on {fell.NodeText} fell out of the lock — presenting free; SYNC REJOIN there when ready");
        }
        else if (mixed)
        {
            var odd = pairs.First(p => p.MixedRate);
            (verdict, light, words) = ("mixed rate", Light.Amber,
                $"{count}: {odd.B} runs at a different rate from {odd.A} — they slip every few seconds; set every output to one rate");
        }
        else if (heard.All(e => Locked(e.Report.State)) && apart is null)
        {
            (verdict, light, words) = ("same vblank", Light.Green, $"{count} on one vblank");
        }
        else if (apart is { } a && reference is not null)
        {
            (verdict, light, words) = ("free-running", Light.Amber, string.Create(CultureInfo.InvariantCulture,
                $"{count}: {Label(a.Entry)} on {a.Entry.NodeText} reports locked but is not on {Label(reference)}'s vblank ({(a.CountsAgree ? $"{a.PhaseUs:0} µs apart" : "its frame count disagrees")}) — check the sync chain"));
        }
        else if (joining is not null)
        {
            (verdict, light, words) = ("free-running", Light.Amber, $"{count}: {Label(joining)} on {joining.NodeText} has been joining the lock for over 5 s");
        }
        else
        {
            verdict = "free-running";
            light = playing && soonest is < SlipAmberS ? Light.Amber : Light.Green;
            words = heard.Count == 1
                ? $"{count} free-running; nothing to compare it with"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{count} free-running; worst pair {worstPhase:0} µs ± {worstUncertainty:0} µs; {(soonest is { } s ? "next slip in " + Duration(s) : "no slip expected")}");
        }
        return new SyncPicture(rows.ToImmutable(), pairs.ToImmutable(), verdict, light, words, mixed, nodes, soonest is { } so ? Round(so, 3) : null, latency);
    }

    /// <summary>The phase of b against a at a common leader instant, ns in (−P_a/2, P_a/2]: each one's vblank nearest the instant,
    /// along its own period.</summary>
    public static double Phase(in VblankReport a, in VblankReport b, long atLeaderNs)
    {
        var va = Nearest(a, atLeaderNs);
        var vb = Nearest(b, atLeaderNs);
        return Wrap(vb - va, a.PeriodNs);
    }

    /// <summary>The nearest standard refresh rate to a measured one (its ppm is read against it).</summary>
    public static double Nominal(double hz)
    {
        var best = StandardHz[0];
        foreach (var s in StandardHz)
            if (Math.Abs(s - hz) < Math.Abs(best - hz))
                best = s;
        return Math.Abs(best - hz) / best < 0.01 ? best : hz;
    }

    /// <summary>A duration in words: "4 min 12 s", "16.6 s", "2 h 3 min".</summary>
    public static string Duration(double seconds)
    {
        if (seconds < 60)
            return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0} s");
        var whole = (long)Math.Round(seconds);
        if (whole < 3600)
            return string.Create(CultureInfo.InvariantCulture, $"{whole / 60} min {whole % 60} s");
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 3600} h {whole % 3600 / 60} min");
    }

    private static bool Locked(LockState s) => s is LockState.Framelocked or LockState.HouseLocked;

    /// <summary>Two boards on one chain count the same vblanks: b's count less a's is the number of a's periods between their last
    /// vblanks, mod 2²⁴ (framelock 3.5). True when either has no board to count.</summary>
    private static bool CountsAgree(in VblankReport a, in VblankReport b)
    {
        if ((a.Flags & VblankReport.FlagBoard) == 0 || (b.Flags & VblankReport.FlagBoard) == 0)
            return true;
        var periods = (long)Math.Round((b.LastVblankLeaderNs - (double)a.LastVblankLeaderNs) / a.PeriodNs);
        var off = ((long)b.BoardFrameCount - a.BoardFrameCount - periods) % Count24;
        return (off + Count24) % Count24 == 0;
    }

    private static double Nearest(in VblankReport r, long atLeaderNs)
    {
        var p = r.PeriodNs;
        var n = Math.Round((atLeaderNs - (double)r.LastVblankLeaderNs) / p);
        return r.LastVblankLeaderNs + n * p - atLeaderNs;
    }

    private static double Wrap(double x, double period)
    {
        var w = x - Math.Floor(x / period) * period;   // [0, P)
        return w > period / 2 ? w - period : w;
    }

    private static double Round(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);

    private static string Plural(int n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");
}
