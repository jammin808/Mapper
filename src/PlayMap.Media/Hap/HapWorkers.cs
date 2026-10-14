using PlayMap.Core.Media.Hap;

namespace PlayMap.Media.Hap;

/// <summary>The chunks of one frame still being worked, and whether any failed. One per decode thread, reused frame after
/// frame: the decode thread arms it, posts the chunks, and waits until <see cref="Left"/> reaches zero.</summary>
internal sealed class FrameCountdown : IDisposable
{
    private readonly ManualResetEventSlim _done = new(false);
    private int _left;
    private int _failed;

    public int Left => Volatile.Read(ref _left);

    public bool Failed => Volatile.Read(ref _failed) != 0;

    public void Arm(int chunks)
    {
        _done.Reset();
        Volatile.Write(ref _failed, 0);
        Volatile.Write(ref _left, chunks);
    }

    public void Finish(bool ok)
    {
        if (!ok)
            Volatile.Write(ref _failed, 1);
        if (Interlocked.Decrement(ref _left) == 0)
            _done.Set();
    }

    public void Wait()
    {
        while (Left > 0)
            _done.Wait(100);
    }

    public void Dispose() => _done.Dispose();
}

/// <summary>One chunk to work: stored bytes in, decoded bytes out, both native memory valid until the countdown is done.</summary>
internal readonly record struct ChunkJob(nint Source, int SourceLength, nint Target, int TargetLength, HapCompressor Compressor,
    FrameCountdown Done);

/// <summary>The process's pool for HAP's second stage: a bounded ring of chunk jobs worked by <c>n</c> threads (normal
/// priority, named "hap worker n"), so four 4K sources share every core without each holding threads of its own. Posting
/// and taking a job allocate nothing; a full ring makes the poster work the job itself. The shared pool lives as long as
/// the process; a pool made for a test is disposed, which ends its threads.</summary>
internal sealed unsafe class HapWorkers : IDisposable
{
    private const int RingSize = 1024;
    private static readonly Lock CreateGate = new();
    private static HapWorkers? _shared;

    private readonly Lock _gate = new();
    private readonly ChunkJob[] _ring = new ChunkJob[RingSize];
    private readonly SemaphoreSlim _ready = new(0, int.MaxValue);
    private readonly Thread[] _threads;
    private int _head;
    private int _count;
    private volatile bool _stop;

    internal HapWorkers(int threads)
    {
        _threads = new Thread[threads];
        Threads = threads;
        for (var i = 0; i < threads; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Name = "hap worker " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            _threads[i].Start();
        }
    }

    public int Threads { get; }

    /// <summary>The process's pool, made on first use with <paramref name="threads"/> threads (0 = cores − 1, at least one);
    /// later callers share it as it is.</summary>
    public static HapWorkers Shared(int threads)
    {
        lock (CreateGate)
            return _shared ??= new HapWorkers(threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount - 1));
    }

    /// <summary>Queues a job; false when the ring is full (the caller works it).</summary>
    public bool Post(in ChunkJob job)
    {
        lock (_gate)
        {
            if (_count == RingSize)
                return false;
            _ring[(_head + _count) % RingSize] = job;
            _count++;
        }
        _ready.Release();
        return true;
    }

    /// <summary>Works one queued job on the calling thread; false when none is queued. A decode thread helps this way while
    /// its own frame's chunks are out.</summary>
    public bool TryHelp()
    {
        if (!_ready.Wait(0))
            return false;
        Run(Take());
        return true;
    }

    private ChunkJob Take()
    {
        lock (_gate)
        {
            var job = _ring[_head];
            _ring[_head] = default;
            _head = (_head + 1) % RingSize;
            _count--;
            return job;
        }
    }

    private void Work()
    {
        while (true)
        {
            _ready.Wait();
            if (_stop)
                return;
            Run(Take());
        }
    }

    public void Dispose()
    {
        if (_stop)
            return;
        // Jobs still queued are worked first: a decode thread may be waiting on them.
        while (TryHelp())
        {
            // Each call works one queued job.
        }
        _stop = true;
        _ready.Release(_threads.Length);
        foreach (var t in _threads)
            t.Join();
        _ready.Dispose();
    }

    /// <summary>Decodes one chunk: Snappy into the target, or a copy; the decoded length must be the target's.</summary>
    public static void Run(in ChunkJob job) => job.Done.Finish(Decode((byte*)job.Source, job.SourceLength, (byte*)job.Target, job.TargetLength, job.Compressor));

    public static bool Decode(byte* source, int sourceLength, byte* target, int targetLength, HapCompressor compressor)
    {
        if (compressor == HapCompressor.None)
        {
            if (sourceLength != targetLength)
                return false;
            Buffer.MemoryCopy(source, target, targetLength, sourceLength);
            return true;
        }
        return SnappyBlock.TryDecompress(new ReadOnlySpan<byte>(source, sourceLength), new Span<byte>(target, targetLength), out var written)
            && written == targetLength;
    }
}
