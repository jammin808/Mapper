using System.Collections.Immutable;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tc;

/// <summary>One item of a chased playlist laid on the timecode: it starts at <see cref="StartNs"/> (TC ns) and runs to
/// <see cref="EndNs"/> (long.MaxValue when it holds or loops until the next region, or its length is unknown), showing media from
/// <see cref="InNs"/> at <see cref="Rate"/>.</summary>
public sealed record TcRegion(int Index, string ItemId, long StartNs, long EndNs, long InNs, long OutNs, double Rate, bool Loop);

/// <summary>A chased playlist's items on the timecode (pure). An item with a TC in starts a region there; an item without one follows
/// the region before it, so one TC in on item 1 lays the whole list out. A region runs for its item's length ((out − in) / rate);
/// an item that loops or holds runs until the next region starts; the latest start at or before a code wins.</summary>
public static class TimecodeRegions
{
    /// <param name="mediaDurationNs">The probed length of an item's media (0 = unknown).</param>
    public static (ImmutableArray<TcRegion> Regions, string Problem) Build(Playlist list, TcFormat format, Func<PlaylistItem, long> mediaDurationNs)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(mediaDurationNs);
        var regions = ImmutableArray.CreateBuilder<TcRegion>();
        long? next = null;
        var problem = "";
        for (var i = 0; i < list.Items.Length; i++)
        {
            var item = list.Items[i];
            long start;
            if (item.TcIn is { Length: > 0 } tc)
            {
                if (!format.TryParseLabel(tc, out var frame))
                {
                    problem = $"item {i + 1}'s TC in {tc} does not exist at {format.Text}";
                    next = null;
                    continue;
                }
                start = format.Ns(frame);
            }
            else if (next is { } follow)
            {
                start = follow;
            }
            else
            {
                if (regions.Count > 0 && problem.Length == 0)
                    problem = $"item {i + 1} has no TC in and the item before it has no known length";
                continue;
            }
            var outNs = item.OutNs > 0 ? item.OutNs : item.DurationNs > 0 ? item.InNs + item.DurationNs : mediaDurationNs(item) is > 0 and var d ? d : 0;
            var rate = item.Rate > 0 ? item.Rate : 1;
            var length = outNs > item.InNs ? (long)Math.Round((outNs - item.InNs) / rate) : 0;
            var held = item.End is EndAction.Loop or EndAction.Hold;
            regions.Add(new TcRegion(i, item.Id, start, held || length == 0 ? long.MaxValue : start + length, item.InNs, outNs, rate, item.End == EndAction.Loop));
            next = length > 0 ? start + length : null;
        }
        // The latest start wins: sort by start (stable for equal starts, the later item last).
        var sorted = regions.OrderBy(r => r.StartNs).ThenBy(r => r.Index).ToImmutableArray();
        return (sorted, problem);
    }

    /// <summary>The region a code falls in (the latest start at or before it, cut at the next start), or −1, and the next region's
    /// position in the array (or −1).</summary>
    public static (int At, int Next) Find(ImmutableArray<TcRegion> regions, long tcNs)
    {
        var lo = 0;
        var hi = regions.Length - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (regions[mid].StartNs <= tcNs)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        var next = found + 1 < regions.Length ? found + 1 : -1;
        if (found < 0)
            return (-1, regions.Length > 0 ? 0 : -1);
        var r = regions[found];
        var end = next >= 0 ? Math.Min(r.EndNs, regions[next].StartNs) : r.EndNs;
        return tcNs < end ? (found, next) : (-1, next);
    }
}

/// <summary>A chase's state: disengaged, waiting (engaged, no code), acquiring, locked, parked, outside (rolling code in no region),
/// freewheel (riding a dropout on the last line), held / stopped / free (the freewheel ran out: OnLoss hold, stop or continue),
/// reverse (backwards code: holding).</summary>
public enum ChaseState { Disengaged, Waiting, Acquiring, Locked, Parked, Outside, Freewheel, Held, Stopped, Free, Reverse }

/// <summary>What a chase's lane should show: a region anchored (its item at media <see cref="MediaNs"/> at leader instant
/// <see cref="AtLeaderNs"/>, advancing at <see cref="Rate"/> while running), a gap (black until the next region's start at
/// <see cref="NextAtLeaderNs"/>), a hold between items (the region before's last frame at <see cref="MediaNs"/>, held from
/// <see cref="AtLeaderNs"/>, the instant the shown code left that region — long.MinValue when the code stands still and names none;
/// a reverse hold has no region and holds where the lane is), idle (black), or no change; and the words.</summary>
public sealed record ChasePlan(ChaseState State, ChaseAction Action, int Region, long MediaNs, long AtLeaderNs, double Rate, bool Running,
    int NextRegion, long NextAtLeaderNs, long TcNs, string Words);

public enum ChaseAction { None, Anchor, Gap, Idle, Hold }

/// <summary>A chase's settings as the logic and its lane read them (from the model's <see cref="TimecodeChase"/>).</summary>
public sealed record ChaseSettings(long DelayNs, ChaseLoss OnLoss, long FreewheelNs, ChaseGap Gap)
{
    /// <summary>The jump threshold (0 = auto): the chase's sources' code is judged by it (<see cref="TimecodeTracker"/>), so code
    /// further off its line than this relocates the chase at once and code within it is drift the fit takes in; a re-anchor that
    /// moves the picture's code by more than it is the chase's last jump.</summary>
    public long JumpNs { get; init; }

    /// <summary>The jump threshold in force for a line of a format and a scatter (σ).</summary>
    public double JumpThresholdNs(TcFormat format, double sigmaNs) => TimecodeTracker.JumpThresholdNs(JumpNs, format, sigmaNs);

    public static ChaseSettings Of(TimecodeChase chase)
    {
        ArgumentNullException.ThrowIfNull(chase);
        return new ChaseSettings(chase.DelayNs, chase.OnLoss, chase.FreewheelNs, chase.Gap) { JumpNs = chase.JumpNs };
    }
}

/// <summary>The chase's memory between ticks: its state, when the freewheel began, the last line it used (leader domain).</summary>
public sealed record ChaseMemory(ChaseState State, long FreewheelSinceNs, TcLine? LastLine)
{
    public static readonly ChaseMemory Start = new(ChaseState.Waiting, long.MinValue, null);
}

/// <summary>The chase as a pure function: from the source's state and fitted line (already in the leader's time), the regions, the
/// settings and the memory, what the lane shows. The content is a function of the code: media(L) = In + (tc(L − delay) − S)·rate,
/// anchored at the line's own (past) instant so the anchor holds for every later instant. A dropout rides the last line for the
/// freewheel, then holds, stops or continues by rule; returning code re-locks.</summary>
public static class ChaseLogic
{
    public static (ChasePlan Plan, ChaseMemory Memory) Decide(ChaseSettings settings, ImmutableArray<TcRegion> regions, string sourceState, TcLine? line,
        ChaseMemory memory, long nowLeaderNs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(memory);
        switch (sourceState)
        {
            case "rolling" when line is { Running: true } l:
                return (Place(settings, regions, l, ChaseState.Locked, nowLeaderNs), new ChaseMemory(ChaseState.Locked, long.MinValue, l));
            case "parked" when line is { } p:
            {
                var plan = Place(settings, regions, p with { Running = false, Rate = 0 }, ChaseState.Parked, nowLeaderNs);
                return (plan, new ChaseMemory(ChaseState.Parked, long.MinValue, p));
            }
            case "reverse":
                // The code is the one running backwards (its line), not where the last forward line would have it.
                return (new ChasePlan(ChaseState.Reverse, ChaseAction.Hold, -1, 0, nowLeaderNs, 0, false, -1, long.MaxValue,
                    line?.TcAt(nowLeaderNs - settings.DelayNs) ?? memory.LastLine?.TcAt(nowLeaderNs) ?? 0, "timecode running backwards — holding"),
                    memory with { State = ChaseState.Reverse });
            case "dropout" or "lost" or "silent" or "unavailable" when memory.LastLine is { Running: true } last:
            {
                var since = memory.State == ChaseState.Freewheel || memory.FreewheelSinceNs != long.MinValue ? memory.FreewheelSinceNs : nowLeaderNs;
                if (memory.State is ChaseState.Held or ChaseState.Stopped or ChaseState.Free)
                    return Lost(settings, regions, last, memory, nowLeaderNs);
                if (nowLeaderNs - since < settings.FreewheelNs)
                {
                    var plan = Place(settings, regions, last, ChaseState.Freewheel, nowLeaderNs);
                    var left = (settings.FreewheelNs - (nowLeaderNs - since)) / 1_000_000;
                    return (plan with { Words = $"freewheeling on the last line ({left} ms left)" }, new ChaseMemory(ChaseState.Freewheel, since, last));
                }
                return Lost(settings, regions, last, memory with { FreewheelSinceNs = since }, nowLeaderNs);
            }
            case "acquiring":
                return (new ChasePlan(ChaseState.Acquiring, ChaseAction.None, -1, 0, nowLeaderNs, 0, false, -1, long.MaxValue, 0, "code arriving — not locked yet"),
                    memory with { State = memory.State is ChaseState.Locked or ChaseState.Freewheel ? memory.State : ChaseState.Acquiring });
            default:
                if (memory.State is ChaseState.Held or ChaseState.Stopped or ChaseState.Free && memory.LastLine is { } held)
                    return Lost(settings, regions, held, memory, nowLeaderNs);
                return (new ChasePlan(ChaseState.Waiting, ChaseAction.None, -1, 0, nowLeaderNs, 0, false, -1, long.MaxValue, 0, "engaged — waiting for code"),
                    ChaseMemory.Start);
        }
    }

    private static (ChasePlan, ChaseMemory) Lost(ChaseSettings settings, ImmutableArray<TcRegion> regions, TcLine last, ChaseMemory memory, long nowLeaderNs)
    {
        switch (settings.OnLoss)
        {
            case ChaseLoss.Continue:
            {
                var plan = Place(settings, regions, last, ChaseState.Free, nowLeaderNs);
                return (plan with { Words = "the code stopped — running on at its last rate" }, memory with { State = ChaseState.Free });
            }
            case ChaseLoss.Stop:
                return (new ChasePlan(ChaseState.Stopped, ChaseAction.Idle, -1, 0, nowLeaderNs, 0, false, -1, long.MaxValue, 0, "the code stopped — stopped by rule"),
                    memory with { State = ChaseState.Stopped });
            default:
            {
                // Hold where the line said the picture would be when the freewheel ran out.
                var at = memory.FreewheelSinceNs == long.MinValue ? nowLeaderNs : memory.FreewheelSinceNs + settings.FreewheelNs;
                var frozen = last with { AtNs = at, TcNs = last.TcAt(at), Running = false, Rate = 0 };
                var plan = Place(settings, regions, frozen, ChaseState.Held, nowLeaderNs);
                return (plan with { Words = "the code stopped — holding by rule" }, memory with { State = ChaseState.Held });
            }
        }
    }

    /// <summary>The lane for a line: the region at the shown code, anchored at the line's instant; or the gap before the next region.</summary>
    private static ChasePlan Place(ChaseSettings settings, ImmutableArray<TcRegion> regions, TcLine line, ChaseState state, long nowLeaderNs)
    {
        var shownNow = line.TcAt(nowLeaderNs - settings.DelayNs);
        var (at, next) = TimecodeRegions.Find(regions, shownNow);
        // The picture shown at leader instant L is the code at L − delay.
        var nextAt = next >= 0 && line.Running ? line.At(regions[next].StartNs) + settings.DelayNs : long.MaxValue;
        if (at < 0)
        {
            if (settings.Gap == ChaseGap.Hold && next != 0 && regions.Length > 0)
            {
                var prev = next < 0 ? regions[^1] : regions[next - 1];
                var lastFrame = prev.OutNs > prev.InNs ? prev.OutNs - 1 : prev.InNs;
                // Held from the instant the shown code left the item: the same on every node, however late a node begins holding. Code
                // that stands still names no such instant.
                var heldFrom = line.Running && line.Rate > 0 && prev.EndNs != long.MaxValue ? line.At(prev.EndNs) + settings.DelayNs : long.MinValue;
                return new ChasePlan(state, ChaseAction.Hold, Array(regions, prev), lastFrame, heldFrom, 0, false, next, nextAt, shownNow,
                    "between items — holding the last frame");
            }
            return new ChasePlan(state == ChaseState.Locked ? ChaseState.Outside : state, ChaseAction.Gap, -1, 0, nowLeaderNs, 0, false, next, nextAt, shownNow,
                next >= 0 ? "between items — black until the next starts" : "past the last item — black");
        }
        var r = regions[at];
        var refInstant = line.AtNs;
        var tcAtRef = line.TcAt(refInstant - settings.DelayNs);
        var media = r.InNs + (long)Math.Round((tcAtRef - r.StartNs) * r.Rate);
        var plan = new ChasePlan(state, ChaseAction.Anchor, at, media, refInstant, line.Rate * r.Rate, line.Running, next, nextAt, shownNow,
            state == ChaseState.Locked ? "locked" : state == ChaseState.Parked ? "parked — the code stands still" : "");
        return plan;
    }

    private static int Array(ImmutableArray<TcRegion> regions, TcRegion r) => regions.IndexOf(r);
}
