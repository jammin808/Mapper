using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace PlayMap.Tests;

/// <summary>
/// A measurement of the machine a test host runs on, on only where CI asks for it (<see cref="Variable"/> is <c>1</c>; absent or
/// empty, nothing starts, so no desk and no local run meets it; any other word is refused and the host does not load). One background
/// thread of its own, at normal priority, writes a row every 50 ms to <c>&lt;project&gt;/TestResults/&lt;assembly&gt;-&lt;start&gt;-&lt;pid&gt;.latency.csv</c>:
/// the UTC time; how far past a 10 ms timed wait the thread woke (a normal-priority thread's wait for a processor: oversubscription,
/// over the OS timer's own floor, which on Windows is its tick); how long the work item it queued to the thread pool at the row before
/// waited to start (pool dispatch latency; while it has not started, the time since it was queued, marked pending, and no other is
/// queued); and the pool's thread count and queued items. Each row is formatted into one reused buffer and written straight to the
/// file at its offset, so a row allocates nothing and a host that is killed keeps every row it wrote. Its readings decide nothing by
/// themselves: no governor follows without a reading (docs/round2/CARRYOVER.md, fix-ci-windows-bounds).
/// </summary>
internal sealed class SchedulingProbe : IDisposable
{
    /// <summary>The variable CI's test steps set to <c>1</c> to turn the probe on in every test host.</summary>
    public const string Variable = "PLAYMAP_SCHEDULING_PROBE";

    /// <summary>The columns of every file, its first line.</summary>
    public const string Header = "utc,wake_overshoot_ms,dispatch_ms,dispatch_pending,pool_threads,pool_pending";

    /// <summary>The time between rows.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);

    /// <summary>The timed wait whose overshoot each row reads.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(10);


    private readonly ManualResetEvent _stop = new(false);
    private readonly SafeFileHandle _file;
    private readonly Thread _thread;
    private readonly byte[] _row = new byte[256];
    private readonly Dispatch _dispatch = new();
    private long _offset;
    private long _rows;

    private SchedulingProbe(string path)
    {
        _file = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        Path = path;
        var header = System.Text.Encoding.ASCII.GetBytes(Header + "\n");
        RandomAccess.Write(_file, header, 0);
        _offset = header.Length;
        _thread = new Thread(Run) { IsBackground = true, Name = "PlayMap scheduling probe" };
        _thread.Start();
    }

    /// <summary>The file this probe writes.</summary>
    public string Path { get; }

    /// <summary>The probe this host started (null when it is off), held for the host's life.</summary>
    internal static SchedulingProbe? Running { get; private set; }

    /// <summary>The rows written so far.</summary>
    public long Rows => Interlocked.Read(ref _rows);

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

            var rest = Period - Stopwatch.GetElapsedTime(period);
            if (rest > TimeSpan.Zero && _stop.WaitOne(rest))
                return;
            period = Stopwatch.GetTimestamp();

            var (dispatch, pending) = _dispatch.Read(period);
            Write(overshoot, dispatch, pending);
        }
    }

    /// <summary>One row into the reused buffer, culture-invariant, written at the file's end.</summary>
    private void Write(double overshoot, double dispatch, bool pending)
    {
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
        n += Put(span[n..], ThreadPool.ThreadCount, "D");
        span[n++] = (byte)',';
        n += Put(span[n..], ThreadPool.PendingWorkItemCount, "D");
        span[n++] = (byte)'\n';
        RandomAccess.Write(_file, span[..n], _offset);
        _offset += n;
        Interlocked.Increment(ref _rows);
    }

    private static int Put<T>(Span<byte> into, T value, string format) where T : IUtf8SpanFormattable =>
        value.TryFormat(into, out var written, format, CultureInfo.InvariantCulture)
            ? written
            : throw new InvalidOperationException("a scheduling probe row outgrew its buffer");

    /// <summary>Stops the thread and closes the file; a thread not stopped within 10 s keeps its file and event, as a background
    /// thread ends with the process.</summary>
    public void Dispose()
    {
        _stop.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
            return;
        _file.Dispose();
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
