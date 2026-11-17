using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace PlayMap.Tests;

/// <summary>
/// A measurement of the machine a test host runs on, on only where CI asks for it (<see cref="Variable"/> is <c>1</c>; absent or
/// empty, nothing starts, so no desk and no local run meets it; any other word is refused and the host does not load). Two background
/// threads of its own: the probe, at normal priority, writes a row every 50 ms to
/// <c>&lt;project&gt;/TestResults/&lt;assembly&gt;-&lt;start&gt;-&lt;pid&gt;.latency.csv</c>, and a beat, at the highest priority, does
/// nothing but stamp the time after each short timed wait, so a row says whether one thread or the whole process stood still.
/// The file's first line is the machine's facts (<see cref="Facts"/>: the processor count, the pool's minimum and maximum threads, the
/// periods), its second the columns (<see cref="Header"/>). A row: the UTC time; how far past a 10 ms timed wait the probe woke (a
/// normal-priority thread's wait for a processor, over the OS timer's own floor, which on Windows is its tick); how long the work item it
/// queued to the thread pool at the row before waited to start (pool dispatch latency; while it has not started, the time since it was
/// queued, marked pending, and no other is queued); the pool's thread count (threads that exist, not threads that work) and queued
/// items; then, each since the row before unless named a count: the pool's completed work items, the collector's pause and its gen2
/// count, the working set, the process's user and kernel time, how far past the row's rest wait the probe woke, how long the row
/// before's file write took (a row cannot carry its own), how long this row's pool reads took, the beat's age at the row, the
/// longest the beat stood still since the row before, and how long this row's process reads took (the collector's pause and count,
/// the working set, the processor times). Each row is formatted into one reused buffer and written straight to the file at its offset,
/// so a row allocates nothing, takes no lock a test takes, and a host that is killed keeps every row it wrote. The pool's completed
/// count is the one read that shares a lock: it takes the pool's own counter lock, as the pool's hill climbing does, held for a walk
/// of the pool's per-thread counts, and <c>pool_read_ms</c> times it. Its readings
/// decide nothing by themselves: no governor follows without a reading (docs/round2/CARRYOVER.md, fix-ci-windows-bounds and
/// fix-ci-stall-measure).
/// </summary>
internal sealed class SchedulingProbe : IDisposable
{
    /// <summary>The variable CI's test steps set to <c>1</c> to turn the probe on in every test host.</summary>
    public const string Variable = "PLAYMAP_SCHEDULING_PROBE";

    /// <summary>The start of every file's first line, the machine's facts as <c>key=value</c> pairs after it.</summary>
    public const string FactsMark = "# PlayMap scheduling probe:";

    /// <summary>The columns of every row, the file's second line. The first six are the first probe's, in its order and meaning, so a
    /// reader of its files reads the first six of these.</summary>
    public const string Header = "utc,wake_overshoot_ms,dispatch_ms,dispatch_pending,pool_threads,pool_pending,"
        + "pool_completed,gc_pause_ms,gc_gen2,working_set_mb,cpu_user_ms,cpu_kernel_ms,rest_overshoot_ms,last_write_ms,pool_read_ms,"
        + "beat_age_ms,beat_gap_ms,process_read_ms";

    /// <summary>The time between rows.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);

    /// <summary>The timed wait whose overshoot each row reads.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(10);

    /// <summary>The beat's timed wait: the beat stamps the time after each.</summary>
    public static readonly TimeSpan BeatWait = TimeSpan.FromMilliseconds(5);

    private const double Mebibyte = 1024.0 * 1024.0;

    private readonly ManualResetEvent _stop = new(false);
    private readonly SafeFileHandle _file;
    private readonly Thread _thread;
    private readonly Thread _beat;
    private readonly byte[] _row = new byte[512];
    private readonly byte[] _statm = new byte[128];
    private readonly SafeFileHandle? _resident;
    private readonly Dispatch _dispatch = new();
    private long _offset;
    private long _rows;
    private long _beatAt;
    private long _beatGap;

    // What the row before read, for the columns that are the change since it; touched by the probe's thread alone.
    private long _completed;
    private TimeSpan _paused;
    private TimeSpan _user;
    private TimeSpan _kernel;
    private double _lastWrite;

    private SchedulingProbe(string path)
    {
        _file = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        Path = path;
        // Environment.WorkingSet builds a Process on Linux (about 3 KB a read); there the resident pages are read from the kernel
        // straight into a reused buffer instead.
        if (OperatingSystem.IsLinux())
            _resident = File.OpenHandle("/proc/self/statm", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = System.Text.Encoding.ASCII.GetBytes(Facts() + "\n" + Header + "\n");
        RandomAccess.Write(_file, head, 0);
        _offset = head.Length;
        _completed = ThreadPool.CompletedWorkItemCount;
        _paused = GC.GetTotalPauseDuration();
        var cpu = Environment.CpuUsage;
        _user = cpu.UserTime;
        _kernel = cpu.PrivilegedTime;
        _beatAt = Stopwatch.GetTimestamp();
        _beat = new Thread(Beat) { IsBackground = true, Name = "PlayMap scheduling probe beat", Priority = ThreadPriority.Highest };
        _beat.Start();
        _thread = new Thread(Run) { IsBackground = true, Name = "PlayMap scheduling probe" };
        _thread.Start();
    }

    /// <summary>The file this probe writes.</summary>
    public string Path { get; }

    /// <summary>The probe this host started (null when it is off), held for the host's life.</summary>
    internal static SchedulingProbe? Running { get; private set; }

    /// <summary>The rows written so far.</summary>
    public long Rows => Interlocked.Read(ref _rows);

    /// <summary>The file's first line: the machine's facts a reader needs beside the rows, as <c>key=value</c> pairs.</summary>
    public static string Facts()
    {
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        return string.Create(CultureInfo.InvariantCulture,
            $"{FactsMark} processors={Environment.ProcessorCount} pool_min_workers={minWorkers} pool_min_io={minIo} "
            + $"pool_max_workers={maxWorkers} pool_max_io={maxIo} period_ms={Period.TotalMilliseconds} wait_ms={Wait.TotalMilliseconds} "
            + $"beat_wait_ms={BeatWait.TotalMilliseconds} beat_priority={ThreadPriority.Highest} pid={Environment.ProcessId} "
            + $"os={Environment.OSVersion.Platform} runtime={Environment.Version}");
    }

    /// <summary>The module's own start: the probe runs in this host when <see cref="Variable"/> asks for it.</summary>
#pragma warning disable CA2255 // a test assembly's own initializer: the probe must run in every test host, and no test can start it in all of them
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void StartWhenAsked()
    {
        var assembly = typeof(SchedulingProbe).Assembly.GetName().Name!;
        Running = Start(Environment.GetEnvironmentVariable(Variable), () => ResultsFolder(AppContext.BaseDirectory, assembly), assembly);
    }

    /// <summary>
    /// Starts a probe writing into <paramref name="folder"/> when <paramref name="word"/> is <c>1</c>; answers null, touching nothing,
    /// when it is absent or empty; refuses any other word.
    /// </summary>
    public static SchedulingProbe? Start(string? word, Func<string> folder, string name)
    {
        if (string.IsNullOrEmpty(word))
            return null;
        if (word != "1")
            throw new ArgumentException($"{Variable} is '{word}': only 1 turns the scheduling probe on", nameof(word));
        var dir = folder();
        Directory.CreateDirectory(dir);
        var file = string.Create(CultureInfo.InvariantCulture,
            $"{name}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss}-{Environment.ProcessId}.latency.csv");
        return new SchedulingProbe(System.IO.Path.Combine(dir, file));
    }

    /// <summary>The TestResults folder of the project that built <paramref name="assembly"/>: the nearest folder above
    /// <paramref name="from"/> holding <c>&lt;assembly&gt;.csproj</c>, where dotnet test writes that project's results. A host with no
    /// such folder above it is refused: the rows would go nowhere CI uploads.</summary>
    public static string ResultsFolder(string from, string assembly)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, assembly + ".csproj")))
                return System.IO.Path.Combine(dir.FullName, "TestResults");
        }
        throw new InvalidOperationException($"no {assembly}.csproj above {from}: the scheduling probe has no TestResults folder to write to");
    }

    private void Run()
    {
        var period = Stopwatch.GetTimestamp();
        while (true)
        {
            var asked = Stopwatch.GetTimestamp();
            if (_stop.WaitOne(Wait))
                return;
            var overshoot = Stopwatch.GetElapsedTime(asked).TotalMilliseconds - Wait.TotalMilliseconds;

            // The rest of the period, asked in whole milliseconds: its own overshoot, so a stop inside it is seen as one (no rest to
            // wait reads 0).
            var restAsked = Stopwatch.GetTimestamp();
            var rest = (int)(Period - Stopwatch.GetElapsedTime(period, restAsked)).TotalMilliseconds;
            var restOvershoot = 0.0;
            if (rest > 0)
            {
                if (_stop.WaitOne(rest))
                    return;
                restOvershoot = Stopwatch.GetElapsedTime(restAsked).TotalMilliseconds - rest;
            }
            period = Stopwatch.GetTimestamp();

            var (dispatch, pending) = _dispatch.Read(period);
            Write(overshoot, dispatch, pending, restOvershoot);
        }
    }

    /// <summary>The beat: a stamp after every short wait, and the longest time between two stamps since the probe last took it.</summary>
    private void Beat()
    {
        var last = Stopwatch.GetTimestamp();
        while (!_stop.WaitOne(BeatWait))
        {
            var now = Stopwatch.GetTimestamp();
            var gap = now - last;
            last = now;
            Volatile.Write(ref _beatAt, now);
            long seen;
            do
            {
                seen = Volatile.Read(ref _beatGap);
                if (gap <= seen)
                    break;
            }
            while (Interlocked.CompareExchange(ref _beatGap, gap, seen) != seen);
        }
    }

    /// <summary>One row into the reused buffer, culture-invariant, written at the file's end.</summary>
    private void Write(double overshoot, double dispatch, bool pending, double restOvershoot)
    {
        var reading = Stopwatch.GetTimestamp();
        var threads = ThreadPool.ThreadCount;
        var queued = ThreadPool.PendingWorkItemCount;
        var completed = ThreadPool.CompletedWorkItemCount;
        var poolRead = Stopwatch.GetElapsedTime(reading).TotalMilliseconds;

        var process = Stopwatch.GetTimestamp();
        var paused = GC.GetTotalPauseDuration();
        var gen2 = GC.CollectionCount(2);
        var workingSet = WorkingSet() / Mebibyte;
        var cpu = Environment.CpuUsage;
        var now = Stopwatch.GetTimestamp();
        var processRead = Stopwatch.GetElapsedTime(process, now).TotalMilliseconds;
        var beatAge = Stopwatch.GetElapsedTime(Volatile.Read(ref _beatAt), now).TotalMilliseconds;
        var beatGap = Interlocked.Exchange(ref _beatGap, 0) * 1000.0 / Stopwatch.Frequency;

        var span = _row.AsSpan();
        var n = 0;
        n += Put(span[n..], DateTime.UtcNow, "O");
        span[n++] = (byte)',';
        n += Put(span[n..], overshoot, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], dispatch, "F3");
        span[n++] = (byte)',';
        span[n++] = pending ? (byte)'1' : (byte)'0';
        span[n++] = (byte)',';
        n += Put(span[n..], threads, "D");
        span[n++] = (byte)',';
        n += Put(span[n..], queued, "D");
        span[n++] = (byte)',';
        n += Put(span[n..], completed - _completed, "D");
        span[n++] = (byte)',';
        n += Put(span[n..], (paused - _paused).TotalMilliseconds, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], gen2, "D");
        span[n++] = (byte)',';
        n += Put(span[n..], workingSet, "F1");
        span[n++] = (byte)',';
        n += Put(span[n..], (cpu.UserTime - _user).TotalMilliseconds, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], (cpu.PrivilegedTime - _kernel).TotalMilliseconds, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], restOvershoot, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], _lastWrite, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], poolRead, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], beatAge, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], beatGap, "F3");
        span[n++] = (byte)',';
        n += Put(span[n..], processRead, "F3");
        span[n++] = (byte)'\n';
        _completed = completed;
        _paused = paused;
        _user = cpu.UserTime;
        _kernel = cpu.PrivilegedTime;

        var writing = Stopwatch.GetTimestamp();
        RandomAccess.Write(_file, span[..n], _offset);
        _lastWrite = Stopwatch.GetElapsedTime(writing).TotalMilliseconds;
        _offset += n;
        Interlocked.Increment(ref _rows);
    }

    /// <summary>The process's working set in bytes: Windows' own count, or on Linux the resident pages of /proc/self/statm (its
    /// second field); -1 when Linux's file cannot be read.</summary>
    private long WorkingSet()
    {
        if (_resident is null)
            return Environment.WorkingSet;
        var read = RandomAccess.Read(_resident, _statm, 0);
        var text = _statm.AsSpan(0, read);
        var space = text.IndexOf((byte)' ');
        return space > 0 && Utf8Parser.TryParse(text[(space + 1)..], out long pages, out _)
            ? pages * Environment.SystemPageSize
            : -1;
    }

    private static int Put<T>(Span<byte> into, T value, string format) where T : IUtf8SpanFormattable =>
        value.TryFormat(into, out var written, format, CultureInfo.InvariantCulture)
            ? written
            : throw new InvalidOperationException("a scheduling probe row outgrew its buffer");

    /// <summary>Stops both threads and closes the file; a thread not stopped within 10 s keeps the file and event, as a background
    /// thread ends with the process.</summary>
    public void Dispose()
    {
        _stop.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(10)) || !_beat.Join(TimeSpan.FromSeconds(10)))
            return;
        _file.Dispose();
        _resident?.Dispose();
        _stop.Dispose();
    }

    /// <summary>The one work item the probe keeps in the pool: queued again only once it has started, so a starved pool holds one
    /// item of the probe's, never a backlog of them.</summary>
    private sealed class Dispatch : IThreadPoolWorkItem
    {
        private long _queued;
        private long _started;

        public Dispatch()
        {
            _queued = Stopwatch.GetTimestamp();
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        public void Execute() => Volatile.Write(ref _started, Stopwatch.GetTimestamp());

        /// <summary>The wait of the item queued last (its start, or <paramref name="now"/> while it has not started, pending), then
        /// the next one queued if it has started.</summary>
        public (double Ms, bool Pending) Read(long now)
        {
            var started = Volatile.Read(ref _started);
            if (started == 0)
                return (Stopwatch.GetElapsedTime(_queued, now).TotalMilliseconds, true);
            var waited = Stopwatch.GetElapsedTime(_queued, started).TotalMilliseconds;
            Volatile.Write(ref _started, 0);
            _queued = Stopwatch.GetTimestamp();
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return (waited, false);
        }
    }
}
