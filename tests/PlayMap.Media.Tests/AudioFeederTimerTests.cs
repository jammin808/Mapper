using PlayMap.Core.Machine;
using PlayMap.Core.Time;
using PlayMap.Media.Audio;

namespace PlayMap.Media.Tests;

/// <summary>The audio feeder's thread waits on a deadline timer it makes as it starts, for instants on its own cadence clock (the
/// machine's monotonic clock, never the output's): a service step every 5 ms of that clock, and the output's clock read by the steps
/// alone. Stepped by hand, every wait seen.</summary>
public sealed class AudioFeederTimerTests
{
    [Fact]
    public void TheFeederWaitsOnItsOwnTimerFiveMillisecondsAtATimeOfItsCadenceAndNeverOnTheOutputsClock()
    {
        var output = new CountingClock(7_000_000_000);
        var cadence = new ManualClock(1_000_000_000);
        using var timer = new SteppedTimer(cadence);
        string? madeOn = null;
        var audio = AudioOut.OpenNullPaced("tests have no sound card", output, cadence, () =>
        {
            madeOn = Thread.CurrentThread.Name;
            return timer;
        });
        try
        {
            for (var i = 1; i <= 3; i++)
            {
                Assert.True(timer.NextWait(), $"the feeder did not wait on its timer before step {i}");
                timer.Step();
            }
            Assert.True(timer.NextWait(), "the feeder did not wait on its timer after three steps");
        }
        finally
        {
            audio.Dispose();
        }
        Assert.Equal("audio out", madeOn);
        Assert.Equal([1_005_000_000L, 1_010_000_000, 1_015_000_000, 1_020_000_000], timer.Instants);
        // Three instants reached; the output's end ends the fourth wait.
        Assert.Equal([true, true, true, false], timer.Answers);
        Assert.Equal(["audio out"], timer.Threads.Distinct());
        Assert.True(timer.Disposed, "the feeder did not dispose its timer");
        // Nothing plays, so no step read the output's clock, and no wait read it either.
        Assert.Equal(0, output.Reads);
    }

    /// <summary>A hand-set clock that counts its reads.</summary>
    private sealed class CountingClock(long startNs) : IMonoClock
    {
        private long _reads;

        public long Reads => Interlocked.Read(ref _reads);

        public long NowNs
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return startNs;
            }
        }
    }

    /// <summary>A deadline timer a test steps: each wait keeps its instant and its thread, and blocks until the test lets it reach its
    /// instant (true, the clock moved there) or its cancel is set (false).</summary>
    private sealed class SteppedTimer(ManualClock clock) : IDeadlineTimer
    {
        private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

        private readonly Lock _gate = new();
        private readonly List<long> _instants = [];
        private readonly List<bool> _answers = [];
        private readonly List<string> _threads = [];
        private readonly SemaphoreSlim _waiting = new(0);
        private readonly AutoResetEvent _go = new(false);
        private readonly ManualResetEvent _never = new(false);
        private int _disposed;

        public IReadOnlyList<long> Instants
        {
            get
            {
                lock (_gate)
                    return [.. _instants];
            }
        }

        public IReadOnlyList<bool> Answers
        {
            get
            {
                lock (_gate)
                    return [.. _answers];
            }
        }

        public IReadOnlyList<string> Threads
        {
            get
            {
                lock (_gate)
                    return [.. _threads];
            }
        }

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public double LateP99Us => 0;

        public bool WaitUntil(long monoNs, WaitHandle? cancel = null)
        {
            lock (_gate)
            {
                _instants.Add(monoNs);
                _threads.Add(Thread.CurrentThread.Name ?? "");
            }
            _waiting.Release();
            var reached = WaitHandle.WaitAny([cancel ?? _never, _go]) == 1;
            if (reached)
                clock.NowNs = Math.Max(clock.NowNs, monoNs);
            lock (_gate)
                _answers.Add(reached);
            return reached;
        }

        /// <summary>Waits until the next wait has begun (false past the hang guard).</summary>
        public bool NextWait() => _waiting.Wait(HangGuard);

        /// <summary>The wait under way reaches its instant.</summary>
        public void Step() => _go.Set();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _waiting.Dispose();
            _go.Dispose();
            _never.Dispose();
        }
    }
}
