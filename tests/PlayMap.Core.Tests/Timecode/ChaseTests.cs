using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using PlayMap.Core.Tc;

namespace PlayMap.Core.Tests.Tc;

/// <summary>The chase as a pure function of the code: seeded runs of timecode (a start, a rate off by up to ±50 ppm, observations
/// jittered by up to ±50 µs) through the tracker's fit and the chase logic every 10 ms, and the frame chosen at every simulated
/// 60 Hz vblank compared with the reference — the item's frame at the true code then. Dropouts, loss and jumps follow their rules.</summary>
public sealed class ChaseTests
{
    private static readonly TcFormat Format = TcFormat.Fps25;
    private static readonly FrameRate MediaFps = FrameRate.Fps25;

    /// <summary>Item 1 at 01:00:00:00 for 10 s; item 2 follows it for 8 s; item 3 at 01:00:20:00 (a 2 s gap) for 5 s.</summary>
    private static Playlist Show()
    {
        static PlaylistItem Item(string id, string? tcIn, long lengthNs) =>
            PlaylistItem.For(id, SourceRef.Media("md_" + id), id) with { OutNs = lengthNs, TcIn = tcIn };
        return new Playlist("pl_show", "Show", [Item("it1", "01:00:00:00", 10_000_000_000), Item("it2", null, 8_000_000_000),
            Item("it3", "01:00:20:00", 5_000_000_000)], false, null) { Chase = new TimecodeChase(["tc1"]) };
    }

    private static long Tc(string label) => Format.TryParseLabel(label, out var f) ? Format.Ns(f) : throw new ArgumentException(label);

    /// <summary>The lane as the engine keeps it: an item on an anchor (or black), and the next item from its exact instant.</summary>
    private sealed class Lane
    {
        public int Region { get; set; } = -1;
        public TimelineAnchor Anchor { get; set; }
        public int NextRegion { get; set; } = -1;
        public long SwitchAt { get; set; } = long.MaxValue;
        public TimelineAnchor NextAnchor { get; set; }

        public void Apply(ChasePlan plan, ImmutableArray<TcRegion> regions, long now)
        {
            switch (plan.Action)
            {
                case ChaseAction.Anchor:
                    Region = plan.Region;
                    Anchor = new TimelineAnchor(plan.MediaNs, plan.AtLeaderNs, plan.Running ? plan.Rate : regions[plan.Region].Rate, plan.Running);
                    Next(plan, regions);
                    break;
                case ChaseAction.Hold when plan.Region >= 0:
                    Region = plan.Region;
                    Anchor = new TimelineAnchor(plan.MediaNs, now, 1, false);
                    Next(plan, regions);
                    break;
                case ChaseAction.Gap:
                    Region = -1;
                    Next(plan, regions);
                    break;
                case ChaseAction.Idle:
                    Region = -1;
                    NextRegion = -1;
                    SwitchAt = long.MaxValue;
                    break;
            }
        }

        private void Next(ChasePlan plan, ImmutableArray<TcRegion> regions)
        {
            NextRegion = plan.NextRegion;
            SwitchAt = plan.NextAtLeaderNs;
            if (plan.NextRegion >= 0 && plan.NextAtLeaderNs != long.MaxValue)
            {
                var lineRate = plan.Action == ChaseAction.Anchor ? plan.Rate / regions[plan.Region].Rate : 1;
                NextAnchor = new TimelineAnchor(regions[plan.NextRegion].InNs, plan.NextAtLeaderNs, lineRate * regions[plan.NextRegion].Rate, true);
            }
        }

        /// <summary>(region, frame) shown at an instant, or (−1, −1) for black.</summary>
        public (int Region, long Frame) At(long v, ImmutableArray<TcRegion> regions)
        {
            if (NextRegion >= 0 && v >= SwitchAt)
                return (NextRegion, FrameSelector.FrameAt(NextAnchor, v, MediaFps));
            if (Region < 0)
                return (-1, -1);
            var media = Math.Clamp(Anchor.MediaAt(v), regions[Region].InNs, regions[Region].OutNs - 1);
            return (Region, MediaFps.FrameAt(media));
        }
    }

    /// <summary>The reference: the region and frame of the true code at an instant (black outside every region).</summary>
    private static (int Region, long Frame, bool NearEdge) Reference(ImmutableArray<TcRegion> regions, long tc)
    {
        var (at, _) = TimecodeRegions.Find(regions, tc);
        if (at < 0)
            return (-1, -1, false);
        var r = regions[at];
        var media = r.InNs + (tc - r.StartNs) * r.Rate;
        var phase = media * MediaFps.Value / 1e9 % 1;
        // Within 0.2 ms of a frame boundary the fit's error may fall either side (counted, never excused wholesale).
        var near = phase < 0.005 || phase > 0.995;
        return (at, MediaFps.FrameAt((long)media), near);
    }

    /// <summary>One simulated run: <paramref name="code"/> answers the TC the source sends at a true instant (null = none then), observed
    /// at each frame end; the chase every 10 ms; at every 1/60 s vblank that <paramref name="check"/> takes, the frame shown is compared
    /// with the reference at <paramref name="truth"/>'s code (the code itself when not given).</summary>
    private static (int Checked, int Near, List<string> Misses, Lane Lane, ChaseMemory Memory) Run(ulong seed, Func<long, long?> code, long seconds,
        Func<long, bool> check, ChaseSettings? settings = null, Action<long, (int Region, long Frame), Lane>? watch = null, Func<long, long?>? truth = null)
    {
        truth ??= code;
        var rng = new Pcg32(seed);
        var regions = TimecodeRegions.Build(Show(), Format, _ => 0).Regions;
        var tracker = new TimecodeTracker(Format);
        var lane = new Lane();
        var memory = ChaseMemory.Start;
        settings ??= new ChaseSettings(0, ChaseLoss.Hold, 500_000_000, ChaseGap.Black);
        var t0 = 10_000_000_000L;
        var end = t0 + seconds * 1_000_000_000;
        long nextTick = t0, nextVblank = t0;
        long lastFrame = long.MinValue;
        var misses = new List<string>();
        int checkedCount = 0, near = 0;
        for (var t = t0; t <= end; t += 100_000)
        {
            // An observation when the code crosses a frame boundary (stamped with jitter).
            if (code(t) is { } tc)
            {
                var frame = Format.FrameOf(tc);
                if (lastFrame != long.MinValue && frame != lastFrame)
                {
                    var jitter = (long)(rng.Range(-50_000.0, 50_000.0));
                    tracker.Observe(new TcObservation(t + jitter, frame, 0, Format, 1, TcObsKind.FrameEnd, 0, false));
                }
                lastFrame = frame;
            }
            else
            {
                lastFrame = long.MinValue;
            }
            if (t >= nextTick)
            {
                nextTick += 10_000_000;
                tracker.Tick(t);
                var reading = tracker.Reading;
                (var plan, memory) = ChaseLogic.Decide(settings, regions, reading.State, reading.Line, memory, t);
                lane.Apply(plan, regions, t);
            }
            if (t >= nextVblank)
            {
                nextVblank += 16_666_667;
                var shown = lane.At(t, regions);
                watch?.Invoke(t, shown, lane);
                if (!check(t) || truth(t - settings.DelayNs) is not { } trueTc)
                    continue;
                var (region, refFrame, isNear) = Reference(regions, trueTc);
                checkedCount++;
                if (isNear)
                {
                    near++;
                    continue;
                }
                if ((region, refFrame) != shown)
                    misses.Add($"{Format.Format(Format.FrameOf(trueTc))}: shown {shown}, reference {(region, refFrame)}");
            }
        }
        return (checkedCount, near, misses, lane, memory);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    [InlineData(5UL)]
    public void TheFrameAtEveryVblankIsTheItemsFrameAtTheCode(ulong seed)
    {
        var rng = new Pcg32(seed * 977);
        var start = Tc("00:59:59:00") + (long)(rng.NextDouble() * 1e9);
        var rate = 1 + rng.Range(-50e-6, 50e-6);
        const long t0 = 10_000_000_000;
        long? Code(long t) => start + (long)Math.Round((t - t0) * rate);
        // From 0.3 s (locked), through both regions, the gap and into the third; gap entries are judged from one tick after
        // a region's end (a region ends into black by the tick; the next starts on its exact frame).
        var regions = TimecodeRegions.Build(Show(), Format, _ => 0).Regions;
        bool Check(long t)
        {
            if (t < t0 + 300_000_000)
                return false;
            var tc = Code(t)!.Value;
            return !regions.Any(r => r.EndNs != long.MaxValue && tc >= r.EndNs && tc < r.EndNs + 12_000_000);
        }
        var (count, near, misses, _, memory) = Run(seed, Code, 27, Check);
        Assert.True(count > 1500, $"{count} vblanks checked");
        Assert.Empty(misses);
        Assert.True(near < count / 50, $"{near} of {count} near a boundary");
        Assert.Equal(ChaseState.Locked, memory.State);
    }

    [Fact]
    public void ADelayShowsThePictureLaterThanItsCode()
    {
        const long t0 = 10_000_000_000;
        var start = Tc("01:00:01:00");
        long? Code(long t) => start + (t - t0);
        var settings = new ChaseSettings(40_000_000, ChaseLoss.Hold, 500_000_000, ChaseGap.Black);
        var (count, _, misses, _, _) = Run(7, Code, 3, t => t > t0 + 300_000_000, settings);
        Assert.True(count > 100);
        Assert.Empty(misses);
    }

    [Fact]
    public void ADropoutFreewheelsOnTheLineThenHoldsByRule()
    {
        const long t0 = 10_000_000_000;
        var start = Tc("01:00:02:00");
        long? Truth(long t) => start + (t - t0);
        // The code drops out from 2.0 s to 2.3 s, then stops for good at 4.0 s.
        long? Code(long t) => t is >= t0 + 2_000_000_000 and < t0 + 2_300_000_000 || t >= t0 + 4_000_000_000 ? null : Truth(t);
        var frames = new List<(long T, long Frame)>();
        var run = Run(11, Code, 6, t => t > t0 + 300_000_000 && t < t0 + 4_000_000_000, watch: (t, shown, _) => frames.Add((t, shown.Frame)), truth: Truth);
        // Every vblank of the 300 ms dropout still shows the code's frame: it rode the line.
        Assert.Empty(run.Misses);
        Assert.True(run.Checked > 200);
        // After the code stops: 2.5 frames to see the dropout, the 500 ms freewheel, then the picture holds where the line said.
        var held = frames.Where(f => f.T > t0 + 4_800_000_000).Select(f => f.Frame).Distinct().ToList();
        Assert.Single(held);
        Assert.Equal(ChaseState.Held, run.Memory.State);
        var expected = MediaFps.FrameAt(start + 4_000_000_000 + 100_000_000 + 500_000_000 - Tc("01:00:00:00"));
        Assert.InRange(held[0], expected - 1, expected + 1);
    }

    [Theory]
    [InlineData(ChaseLoss.Stop, -1)]
    [InlineData(ChaseLoss.Continue, 1)]
    public void WhenTheFreewheelRunsOutTheChaseStopsOrRunsOnByRule(ChaseLoss onLoss, int expectMoving)
    {
        const long t0 = 10_000_000_000;
        var start = Tc("01:00:02:00");
        long? Code(long t) => t >= t0 + 1_000_000_000 ? null : start + (t - t0);
        var shown = new List<(long T, (int Region, long Frame) Shown)>();
        var settings = new ChaseSettings(0, onLoss, 500_000_000, ChaseGap.Black);
        var run = Run(3, Code, 3, _ => false, settings, (t, s, _) => shown.Add((t, s)));
        var late = shown.Where(s => s.T > t0 + 2_000_000_000).ToList();
        if (expectMoving < 0)
        {
            Assert.All(late, s => Assert.Equal((-1, -1L), s.Shown));
            Assert.Equal(ChaseState.Stopped, run.Memory.State);
        }
        else
        {
            Assert.True(late[^1].Shown.Frame > late[0].Shown.Frame + 20);
            Assert.Equal(ChaseState.Free, run.Memory.State);
        }
    }

    [Fact]
    public void AJumpIsTakenOnTheSecondObservationThatAgrees()
    {
        const long t0 = 10_000_000_000;
        var start = Tc("01:00:01:00");
        // At 2 s the code jumps 5 s ahead.
        long? Code(long t) => start + (t - t0) + (t >= t0 + 2_000_000_000 ? 5_000_000_000 : 0);
        var run = Run(5, Code, 4, t => t < t0 + 2_000_000_000 && t > t0 + 300_000_000 || t > t0 + 2_150_000_000);
        Assert.Empty(run.Misses);
        Assert.Equal(ChaseState.Locked, run.Memory.State);
    }

    [Fact]
    public void ALoneOutlierIsCountedAndChangesNothing()
    {
        var tracker = new TimecodeTracker(Format);
        var t = 1_000_000_000L;
        for (var f = 100; f < 110; f++, t += 40_000_000)
            tracker.Observe(new TcObservation(t, f, 0, Format, 1, TcObsKind.FrameEnd, 0, false));
        var before = tracker.Reading.Line!.Value;
        tracker.Observe(new TcObservation(t, 500, 0, Format, 1, TcObsKind.FrameEnd, 0, false));
        tracker.Observe(new TcObservation(t + 40_000_000, 111, 0, Format, 1, TcObsKind.FrameEnd, 0, false));
        Assert.Equal(1, tracker.Reading.Counters.Invalid);
        Assert.Equal(0, tracker.Reading.Counters.Discontinuity);
        Assert.Equal(before.Run, tracker.Reading.Line!.Value.Run);
        Assert.Equal("rolling", tracker.Reading.State);
    }

    [Fact]
    public void AGapHoldIsHeldFromTheInstantTheShownCodeLeftTheItem()
    {
        var regions = TimecodeRegions.Build(Show(), Format, _ => 0).Regions;
        var settings = new ChaseSettings(40_000_000, ChaseLoss.Hold, 500_000_000, ChaseGap.Hold);
        // 01:00:18:12 at 10 s, rolling 50 ppm fast, the picture 40 ms behind its code: item 2 ended at 01:00:18:00 on this line.
        var line = new TcLine(10_000_000_000, Tc("01:00:18:12"), 1.00005, 0, 25, 1, 1, true);
        var left = line.At(Tc("01:00:18:00")) + 40_000_000;
        foreach (var now in new[] { 10_100_000_000L, 11_000_000_000L })
        {
            var (plan, _) = ChaseLogic.Decide(settings, regions, "rolling", line, ChaseMemory.Start, now);
            Assert.Equal((ChaseAction.Hold, 1, 8_000_000_000L - 1, left, 2), (plan.Action, plan.Region, plan.MediaNs, plan.AtLeaderNs, plan.NextRegion));
        }
        // Code that stands still in the gap names no such instant.
        var (parked, _) = ChaseLogic.Decide(settings, regions, "parked", line, ChaseMemory.Start, 11_000_000_000);
        Assert.Equal((ChaseAction.Hold, long.MinValue), (parked.Action, parked.AtLeaderNs));
    }

    [Fact]
    public void ReverseCodeHoldsAndALocateParksOnItsFrame()
    {
        var regions = TimecodeRegions.Build(Show(), Format, _ => 0).Regions;
        var settings = new ChaseSettings(0, ChaseLoss.Hold, 500_000_000, ChaseGap.Black);
        var (reverse, _) = ChaseLogic.Decide(settings, regions, "reverse", null, ChaseMemory.Start, 1);
        Assert.Equal((ChaseState.Reverse, ChaseAction.Hold), (reverse.State, reverse.Action));
        Assert.Equal("timecode running backwards — holding", reverse.Words);
        var tracker = new TimecodeTracker(null);
        var at = Tc("01:00:05:00");
        tracker.Observe(MtcParser.FullFrame(1, 1, 0, 5, 0, 2_000_000_000)!.Value);
        Assert.Equal("parked", tracker.Reading.State);
        var (parked, _) = ChaseLogic.Decide(settings, regions, tracker.Reading.State, tracker.Reading.Line, ChaseMemory.Start, 3_000_000_000);
        Assert.Equal((ChaseState.Parked, ChaseAction.Anchor, 0, at - Tc("01:00:00:00"), false), (parked.State, parked.Action, parked.Region, parked.MediaNs, parked.Running));
        // A parked locate never drops out.
        tracker.Tick(10_000_000_000);
        Assert.Equal("parked", tracker.Reading.State);
    }

    [Fact]
    public void RegionsFollowTcInsAndTheLatestStartWins()
    {
        var (regions, problem) = TimecodeRegions.Build(Show(), Format, _ => 0);
        Assert.Equal("", problem);
        Assert.Equal([Tc("01:00:00:00"), Tc("01:00:10:00"), Tc("01:00:20:00")], regions.Select(r => r.StartNs));
        Assert.Equal((0, 1), TimecodeRegions.Find(regions, Tc("01:00:09:24")));
        Assert.Equal((1, 2), TimecodeRegions.Find(regions, Tc("01:00:10:00")));
        Assert.Equal((-1, 2), TimecodeRegions.Find(regions, Tc("01:00:19:00")));
        Assert.Equal((-1, 0), TimecodeRegions.Find(regions, Tc("00:59:00:00")));
        Assert.Equal((-1, -1), TimecodeRegions.Find(regions, Tc("01:00:30:00")));
        // A later TC in cuts the item before it short.
        var cut = Show() with { Items = Show().Items.SetItem(1, Show().Items[1] with { TcIn = "01:00:05:00" }) };
        var (cutRegions, _) = TimecodeRegions.Build(cut, Format, _ => 0);
        Assert.Equal((1, 2), TimecodeRegions.Find(cutRegions, Tc("01:00:06:00")));
        // An item with no length leaves the next without a place.
        var unknown = Show() with { Items = [Show().Items[0], Show().Items[1] with { OutNs = 0 }, Show().Items[2] with { TcIn = null }] };
        Assert.Equal("item 3 has no TC in and the item before it has no known length", TimecodeRegions.Build(unknown, Format, _ => 0).Problem);
    }
}
