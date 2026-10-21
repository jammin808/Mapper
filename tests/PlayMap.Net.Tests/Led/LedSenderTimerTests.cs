using PlayMap.Core.Machine;
using PlayMap.Core.Time;
using PlayMap.Net.Led;

namespace PlayMap.Net.Tests.Led;

/// <summary>The LED sender's thread waits on a deadline timer it makes as it starts, until a millisecond before the next instant it has
/// work at, and whatever hands it work (a program, a master change, a stop, a frame, its end) ends the wait early: stepped by hand on a
/// manual clock, every wait seen.</summary>
public sealed class LedSenderTimerTests
{
    [Fact]
    public async Task TheSendThreadWaitsOnItsOwnTimerUntilAMillisecondBeforeItsNextInstantAndWorkEndsTheWaitEarly()
    {
        var clock = new ManualClock(1_000_000_000);
        using var timer = new SteppedTimer(clock);
        string? madeOn = null;
        var sender = new LedSender(new LedSenderOptions
        {
            Clock = clock,
            Timer = () =>
            {
                madeOn = Thread.CurrentThread.Name;
                return timer;
            },
        });
        try
        {
            sender.Start();
            // Nothing is patched: the next pump is 100 ms on, so the thread waits until 99 ms.
            Assert.True(timer.NextWait(), "the send thread did not wait on its timer");
            // A master change wakes the wait before its instant, and the next pump waits for the same instant again.
            sender.SetMaster(true, blackFirst: false);
            Assert.True(timer.NextWait(), "the send thread did not wait again after the master change");
            // The instant comes: the pump there finds the next one 100 ms on.
            timer.Step();
            Assert.True(timer.NextWait(), "the send thread did not wait again after its instant");
        }
        finally
        {
            await sender.DisposeAsync();
        }
        Assert.Equal("PlayMap led-send", madeOn);
        Assert.Equal([1_099_000_000L, 1_099_000_000, 1_198_000_000], timer.Instants);
        // The master change and the sender's end answer false; the instant reached answers true.
        Assert.Equal([false, true, false], timer.Answers);
        Assert.Equal(["PlayMap led-send"], timer.Threads.Distinct());
        Assert.True(sender.Master);
        Assert.True(timer.Disposed, "the send thread did not dispose its timer");
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
