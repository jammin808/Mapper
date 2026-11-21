using System.Globalization;
using System.Runtime.CompilerServices;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;

namespace PlayMap.PriorityProbe;

/// <summary>The victim: a process that stands for a desk or a supervisor on the same machine. Its class is set to Normal by its own
/// hand at its start and read back (or, in the runner's leg, left as it was started; or, in the legs that measure what HIGH buys, claimed
/// HIGH or NORMAL through the product's own platform as the burner claims its class, read back the same way, so the same loops stand for
/// the busy side's own deadline beside a competing load), then three things run from its ready until its
/// window's end, each stamping every period on the machine's monotonic clock into rows made before the first period (nothing is
/// allocated in a loop): a frame loop on one thread (the fixed piece of work the calibration made about 10 ms on the quiet runner,
/// every 16.667 ms, waiting on the timer the product's platform makes); a light thread that only waits 50 ms on the runtime's own wait
/// and wakes (a UI tick); and one pool item, queued at each tick while the one before has run (dispatch latency). A fourth thread, at
/// the highest thread priority, only waits for the window's start and end and stamps the process's processor time and the timer's
/// current resolution at each, with the instant it took them (a thread the load holds stamps late, and its stamp says how late). Its
/// files are written once the window has ended.</summary>
internal sealed class Victim : IDisposable
{
    public const long FramePeriodNs = 16_666_667;
    public const long TickPeriodNs = 50_000_000;

    private readonly string _dir;
    private readonly long _work;
    private readonly long _readyNs;
    private readonly long _startNs;
    private readonly long _endNs;
    private readonly IDeadlineTimer _frameTimer;
    private readonly RuntimeDeadlineTimer _tickTimer = new();
    private readonly RuntimeDeadlineTimer _boundsTimer = new();
    private readonly PoolItem _pool;

    private readonly long[] _frameDue;
    private readonly long[] _frameStart;
    private readonly long[] _frameWorkNs;
    private readonly long[] _frameCpuNs;
    private readonly int[] _frameMissedAfter;
    private int _frames;
    private int _framesDropped;

    private readonly long[] _tickDue;
    private readonly long[] _tickWoke;
    private readonly bool[] _tickPending;
    private int _ticks;
    private int _ticksDropped;

    private double _cpuAtStartMs = double.NaN;
    private long _cpuStartStampNs;
    private double _cpuAtEndMs = double.NaN;
    private long _cpuEndStampNs;
    private string _timerAtStart = "";
    private string _timerAtEnd = "";

    private Victim(string dir, long work, double seconds, double lead, IDeadlineTimer frameTimer)
    {
        _dir = dir;
        _work = work;
        _frameTimer = frameTimer;
        var span = lead + seconds + 2;
        var frames = (int)(span * 1e9 / FramePeriodNs) + 64;
        var ticks = (int)(span * 1e9 / TickPeriodNs) + 64;
        _frameDue = new long[frames];
        _frameStart = new long[frames];
        _frameWorkNs = new long[frames];
        _frameCpuNs = new long[frames];
        _frameMissedAfter = new int[frames];
        _tickDue = new long[ticks];
        _tickWoke = new long[ticks];
        _tickPending = new bool[ticks];
        _pool = new PoolItem(ticks);
        _readyNs = MonoClock.Instance.NowNs;
        _startNs = _readyNs + (long)(lead * 1e9);
        _endNs = _startNs + (long)(seconds * 1e9);
    }

    /// <summary>victim --dir D --seconds S --lead L --work N --class normal|keep|claim-high|claim-normal: exit 0, or 3 when told a class
    /// (set Normal, or claimed through the product's platform) and the OS reads back another on Windows (the rows are written either
    /// way).</summary>
    public static int Run(Args args)
    {
        var dir = args.Text("dir");
        var seconds = args.Number("seconds");
        var lead = args.Number("lead");
        var work = args.Integer("work");
        var mode = args.Word("class", "normal", "keep", "claim-high", "claim-normal");
        Directory.CreateDirectory(dir);

        using var platform = Os.Platform();
        var (startClass, startBase) = Os.ReadClass();
        var setWords = "";
        var claimApplied = "";
        var claimRead = "";
        var claim = mode is "claim-high" or "claim-normal";
        if (mode == "normal")
        {
            (_, setWords) = Os.SetNormal();
        }
        else if (claim && OperatingSystem.IsWindows())
        {
            // The product's own claim, as the burner makes it: what is measured is the class a node claims, never a hand-set one.
            var (priority, words) = WindowsPlatform.Claim(platform, mode == "claim-high" ? "high" : "normal");
            setWords = words;
            claimRead = priority?.ReadBack ?? "";
            claimApplied = priority is { Applied: true } ? "yes" : "no";
        }
        else if (claim)
        {
            setWords = "the class claim is Windows only (WindowsMachinePlatform.ClaimProcess): nothing is claimed on " +
                System.Runtime.InteropServices.RuntimeInformation.OSDescription + "; the loops run at the class the process was started at";
        }
        else
        {
            setWords = "kept as started: the runner's own condition";
        }
        var (readClass, readBase) = Os.ReadClass();
        var told = mode != "keep" && OperatingSystem.IsWindows();
        var expected = mode == "claim-high" ? "High" : "Normal";
        var classOk = !told || (readClass == expected && (!claim || claimApplied == "yes"));

        using var frameTimer = platform.CreateTimer();
        var timerKind = platform.TimerMode(frameTimer);
        using var victim = new Victim(dir, work, seconds, lead, frameTimer);
        var anchorMono = MonoClock.Instance.NowNs;
        var anchorUtc = DateTime.UtcNow;
        var cpu0 = Os.ProcessCpuMs();
        var gc0 = GC.CollectionCount(0);

        var frame = new Thread(victim.FrameLoop) { IsBackground = true, Name = "victim frame loop" };
        var tick = new Thread(victim.TickLoop) { IsBackground = true, Name = "victim tick" };
        var bounds = new Thread(victim.BoundsStamps) { IsBackground = true, Name = "victim window bounds", Priority = ThreadPriority.Highest };
        frame.Start();
        tick.Start();
        bounds.Start();
        // The ready file names the window the controller starts the burner into: [start, end) on the machine's monotonic clock.
        Files.WriteAllLines(Path.Combine(dir, "victim.ready"),
            [string.Create(CultureInfo.InvariantCulture, $"{victim._startNs},{victim._endNs},{Environment.ProcessId}")]);
        frame.Join();
        tick.Join();
        bounds.Join();
        var pendingAtEnd = victim._pool.WaitIdle(TimeSpan.FromSeconds(5));
        var poolWaitEnd = MonoClock.Instance.NowNs;
        var gc1 = GC.CollectionCount(0);
        var cpu1 = Os.ProcessCpuMs();
        victim.WriteRows(anchorMono, anchorUtc);
        Files.WriteCsv(Path.Combine(dir, "victim.csv"), Rows.VictimHeader,
        [[
            Environment.OSVersion.Platform.ToString(), Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture), mode, startClass, startBase, readClass, readBase,
            told ? (classOk ? "yes" : "no") : "not told", setWords, timerKind, "runtime", work.ToString(CultureInfo.InvariantCulture),
            victim._readyNs.ToString(CultureInfo.InvariantCulture), victim._startNs.ToString(CultureInfo.InvariantCulture),
            victim._endNs.ToString(CultureInfo.InvariantCulture),
            anchorMono.ToString(CultureInfo.InvariantCulture), anchorUtc.ToString("o", CultureInfo.InvariantCulture),
            victim._frames.ToString(CultureInfo.InvariantCulture), victim._framesDropped.ToString(CultureInfo.InvariantCulture),
            victim._ticks.ToString(CultureInfo.InvariantCulture), victim._ticksDropped.ToString(CultureInfo.InvariantCulture),
            victim._pool.Count.ToString(CultureInfo.InvariantCulture), pendingAtEnd ? "yes" : "no",
            (gc1 - gc0).ToString(CultureInfo.InvariantCulture), (cpu1 - cpu0).ToString("0.0", CultureInfo.InvariantCulture),
            Ms(victim._cpuAtStartMs), victim._cpuStartStampNs.ToString(CultureInfo.InvariantCulture),
            Ms(victim._cpuAtEndMs), victim._cpuEndStampNs.ToString(CultureInfo.InvariantCulture),
            victim._timerAtStart, victim._timerAtEnd, poolWaitEnd.ToString(CultureInfo.InvariantCulture), claimApplied, claimRead,
        ]]);
        // After the victim's own rows, and never throwing: the scheduler's reading never costs the leg its rows or its exit code.
        Scheduler.Write(Path.Combine(dir, Scheduler.File), "the victim's end");
        return classOk ? 0 : 3;
    }

    private static string Ms(double ms) => double.IsNaN(ms) ? "" : ms.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The process's processor time (all its threads, the runtime's own among them) and the timer's current resolution at the
    /// window's start and end, each with the instant it was read: the window's processor use is read from these, never from the whole
    /// life. On Windows a thread's processor time is counted in clock ticks, so the process's total moves by whole ticks per thread.</summary>
    private void BoundsStamps()
    {
        _boundsTimer.WaitUntil(_startNs);
        _cpuStartStampNs = MonoClock.Instance.NowNs;
        _cpuAtStartMs = Os.ProcessCpuMs();
        _timerAtStart = Os.TimerCurrent();
        _boundsTimer.WaitUntil(_endNs);
        _cpuEndStampNs = MonoClock.Instance.NowNs;
        _cpuAtEndMs = Os.ProcessCpuMs();
        _timerAtEnd = Os.TimerCurrent();
    }

    private void FrameLoop()
    {
        var due = MonoClock.Instance.NowNs + FramePeriodNs;
        while (due < _endNs)
        {
            _frameTimer.WaitUntil(due);
            var start = MonoClock.Instance.NowNs;
            var cpu0 = Os.ThreadCpuNs();
            Work.Run(_work);
            var done = MonoClock.Instance.NowNs;
            var cpu1 = Os.ThreadCpuNs();
            // Each slot after this frame's own that ends at or before this frame's work ends is missed (the frame was still at work
            // when it closed), so frames and missed slots tile the periods one for one; the loop never runs back to back to catch up.
            var next = due + FramePeriodNs;
            var missed = 0;
            while (next + FramePeriodNs <= done)
            {
                next += FramePeriodNs;
                missed++;
            }
            var i = _frames;
            if (i < _frameDue.Length)
            {
                _frameDue[i] = due;
                _frameStart[i] = start;
                _frameWorkNs[i] = done - start;
                _frameCpuNs[i] = cpu0 < 0 || cpu1 < 0 ? -1 : cpu1 - cpu0;
                _frameMissedAfter[i] = missed;
                _frames = i + 1;
            }
            else
            {
                _framesDropped++;
            }
            due = next;
        }
    }

    private void TickLoop()
    {
        var due = MonoClock.Instance.NowNs + TickPeriodNs;
        while (due < _endNs)
        {
            _tickTimer.WaitUntil(due);
            var woke = MonoClock.Instance.NowNs;
            var pending = !_pool.TryQueue(woke);
            var i = _ticks;
            if (i < _tickDue.Length)
            {
                _tickDue[i] = due;
                _tickWoke[i] = woke;
                _tickPending[i] = pending;
                _ticks = i + 1;
            }
            else
            {
                _ticksDropped++;
            }
            // A UI tick re-arms from its wake, as a dispatcher timer does.
            due = woke + TickPeriodNs;
        }
    }

    private void WriteRows(long anchorMono, DateTime anchorUtc)
    {
        string Utc(long mono) => anchorUtc.AddTicks((mono - anchorMono) / 100).ToString("o", CultureInfo.InvariantCulture);
        var frames = new List<string[]>(_frames);
        for (var i = 0; i < _frames; i++)
            frames.Add([Utc(_frameStart[i]), _frameDue[i].ToString(CultureInfo.InvariantCulture), _frameStart[i].ToString(CultureInfo.InvariantCulture),
                Rows.Ms(_frameStart[i] - _frameDue[i]), Rows.Ms(_frameWorkNs[i]), _frameCpuNs[i] < 0 ? "" : Rows.Ms(_frameCpuNs[i]),
                _frameMissedAfter[i].ToString(CultureInfo.InvariantCulture)]);
        Files.WriteCsv(Path.Combine(_dir, "frames.csv"), Rows.FramesHeader, frames);
        var ticks = new List<string[]>(_ticks);
        for (var i = 0; i < _ticks; i++)
            ticks.Add([Utc(_tickWoke[i]), _tickDue[i].ToString(CultureInfo.InvariantCulture), _tickWoke[i].ToString(CultureInfo.InvariantCulture),
                Rows.Ms(_tickWoke[i] - _tickDue[i]), _tickPending[i] ? "1" : "0"]);
        Files.WriteCsv(Path.Combine(_dir, "ticks.csv"), Rows.TicksHeader, ticks);
        var pool = new List<string[]>(_pool.Count);
        for (var i = 0; i < _pool.Count; i++)
            pool.Add([Utc(_pool.Started[i]), _pool.Queued[i].ToString(CultureInfo.InvariantCulture), _pool.Started[i].ToString(CultureInfo.InvariantCulture),
                Rows.Ms(_pool.Started[i] - _pool.Queued[i])]);
        Files.WriteCsv(Path.Combine(_dir, "pool.csv"), Rows.PoolHeader, pool);
    }

    /// <summary>Lets go of what the victim owns: the tick's and the bounds' timers and the pool item's event (the frame timer is its
    /// caller's).</summary>
    public void Dispose()
    {
        _tickTimer.Dispose();
        _boundsTimer.Dispose();
        _pool.Dispose();
    }

    /// <summary>One pool item, queued again only once it has run: each run records when it was queued and when it started.</summary>
    private sealed class PoolItem(int capacity) : IThreadPoolWorkItem, IDisposable
    {
        public readonly long[] Queued = new long[capacity];
        public readonly long[] Started = new long[capacity];
        private readonly ManualResetEventSlim _idle = new(true);
        private long _queuedNs;
        private int _pending;
        private int _count;

        public int Count => Volatile.Read(ref _count);

        /// <summary>Queues the item stamped <paramref name="now"/>; false while the one before has not run.</summary>
        public bool TryQueue(long now)
        {
            if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
                return false;
            _idle.Reset();
            _queuedNs = now;
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return true;
        }

        public void Execute()
        {
            var started = MonoClock.Instance.NowNs;
            var i = _count;
            if (i < Queued.Length)
            {
                Queued[i] = _queuedNs;
                Started[i] = started;
                Volatile.Write(ref _count, i + 1);
            }
            // Set before the item reads as not pending, so an event seen with nothing pending is never set again by this run.
            _idle.Set();
            Volatile.Write(ref _pending, 0);
        }

        /// <summary>Waits for a queued item to run, at most <paramref name="bound"/>: true when one is still pending after it.</summary>
        public bool WaitIdle(TimeSpan bound) => !_idle.Wait(bound);

        /// <summary>Lets the event go once no item is pending: an item still queued at the end (the pool held past the wait) sets it
        /// when it runs, so then it is left to the collector rather than set after its dispose.</summary>
        public void Dispose()
        {
            if (Volatile.Read(ref _pending) == 0)
                _idle.Dispose();
        }
    }
}

/// <summary>The fixed piece of work every frame does: a dependent chain of floating-point steps the compiler cannot drop, so its
/// processor time is the same in every leg and only its wall time can grow.</summary>
internal static class Work
{
    private static double _sink = 1.0;

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Run(long iterations)
    {
        var x = Volatile.Read(ref _sink);
        for (var i = 0L; i < iterations; i++)
        {
            x = x * 1.0000001 + 1e-9;
            if (x > 2.0)
                x -= 1.0;
        }
        Volatile.Write(ref _sink, x);
    }
}
