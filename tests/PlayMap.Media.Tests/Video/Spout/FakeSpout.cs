using PlayMap.Media.Video.Spout;

namespace PlayMap.Media.Tests.Video.Spout;

/// <summary>The kernel's named objects in memory, as Windows keeps them: a map lives while any handle to it is open (an Open of a map
/// nobody holds answers null), a mutex has one holder (a handle, or "another app" the test names), a semaphore a count, an event the
/// times it was set. Every call is counted by name so a test can assert Spout's order.</summary>
internal sealed class FakeSpoutKernel : ISpoutKernel
{
    private sealed class MapObject(int size)
    {
        public readonly byte[] Bytes = new byte[size];
        public int Handles;
    }

    private sealed class MutexObject
    {
        public object? Holder;
        public bool Abandoned;
    }

    private readonly Dictionary<string, MapObject> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutexObject> _mutexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _semaphores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _events = new(StringComparer.Ordinal);

    private readonly Lock _logGate = new();

    /// <summary>Every call by name, in order (a send's worker and the lane may both call: the log is locked).</summary>
    public List<string> Log { get; } = [];

    private void Note(string call)
    {
        lock (_logGate)
            Log.Add(call);
    }

    public string ProcessPath { get; set; } = @"C:\Shows\PlayMap.exe";

    public bool MemoryShare { get; set; }

    public byte[]? MapBytes(string name) => _maps.TryGetValue(name, out var m) && m.Handles > 0 ? m.Bytes : null;

    public int Count(string semaphore) => _semaphores[semaphore][0];

    public int Sets(string ev) => _events.TryGetValue(ev, out var e) ? e[0] : 0;

    public bool Exists(string name) => _mutexes.ContainsKey(name) || _semaphores.ContainsKey(name) || _events.ContainsKey(name) || MapBytes(name) is not null;

    /// <summary>Another app takes a mutex (true when it got it).</summary>
    public bool HoldByAnotherApp(string mutex)
    {
        var m = _mutexes[mutex];
        if (m.Holder is not null)
            return false;
        m.Holder = "another app";
        return true;
    }

    public void ReleaseByAnotherApp(string mutex) => _mutexes[mutex].Holder = null;

    /// <summary>The app holding a mutex dies: the next taker gets it, abandoned.</summary>
    public void AbandonByAnotherApp(string mutex)
    {
        var m = _mutexes[mutex];
        m.Holder = null;
        m.Abandoned = true;
    }

    public ISpoutMap? OpenMap(string name)
    {
        Note("open map " + name);
        if (!_maps.TryGetValue(name, out var m) || m.Handles == 0)
            return null;
        m.Handles++;
        return new Map(m);
    }

    public ISpoutMap CreateMap(string name, int size)
    {
        Note("create map " + name);
        if (!_maps.TryGetValue(name, out var m) || m.Handles == 0)
            _maps[name] = m = new MapObject(size);
        m.Handles++;
        return new Map(m);
    }

    public ISpoutLock? OpenMutex(string name)
    {
        Note("open mutex " + name);
        return _mutexes.TryGetValue(name, out var m) ? new MutexHandle(this, name, m) : null;
    }

    public ISpoutLock CreateMutex(string name)
    {
        Note("create mutex " + name);
        if (!_mutexes.TryGetValue(name, out var m))
            _mutexes[name] = m = new MutexObject();
        return new MutexHandle(this, name, m);
    }

    public ISpoutCounter? OpenSemaphore(string name)
    {
        Note("open semaphore " + name);
        return _semaphores.TryGetValue(name, out var c) ? new Counter(this, name, c) : null;
    }

    public ISpoutCounter CreateSemaphore(string name, int initial, int maximum)
    {
        Note("create semaphore " + name);
        if (!_semaphores.TryGetValue(name, out var c))
            _semaphores[name] = c = [initial];
        return new Counter(this, name, c);
    }

    public ISpoutSignal? OpenEvent(string name)
    {
        Note("open event " + name);
        return _events.TryGetValue(name, out var e) ? new Signal(this, name, e) : null;
    }

    public ISpoutSignal CreateEvent(string name)
    {
        Note("create event " + name);
        if (!_events.TryGetValue(name, out var e))
            _events[name] = e = [0];
        return new Signal(this, name, e);
    }

    private sealed class Map(MapObject m) : ISpoutMap
    {
        private bool _closed;

        public Span<byte> Bytes => m.Bytes;

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            m.Handles--;
        }
    }

    private sealed class MutexHandle(FakeSpoutKernel k, string name, MutexObject m) : ISpoutLock
    {
        public LockTry TryEnter(int milliseconds)
        {
            k.Note($"try {name} {milliseconds}");
            if (m.Holder is not null && !ReferenceEquals(m.Holder, this))
                return LockTry.Busy;
            m.Holder = this;
            if (!m.Abandoned)
                return LockTry.Acquired;
            m.Abandoned = false;
            return LockTry.Abandoned;
        }

        public void Exit()
        {
            k.Note("release " + name);
            if (!ReferenceEquals(m.Holder, this))
                throw new InvalidOperationException($"{name} released by a handle that does not hold it");
            m.Holder = null;
        }

        public void Dispose()
        {
            if (ReferenceEquals(m.Holder, this))
                m.Holder = null;
        }
    }

    private sealed class Counter(FakeSpoutKernel k, string name, int[] c) : ISpoutCounter
    {
        public bool TryTake(int milliseconds)
        {
            k.Note($"take {name}");
            if (c[0] == 0)
                return false;
            c[0]--;
            return true;
        }

        public int Release(int count)
        {
            k.Note($"give {name} {count}");
            var before = c[0];
            c[0] += count;
            return before;
        }

        public void Dispose()
        {
            // A handle holds nothing of the count.
        }
    }

    private sealed class Signal(FakeSpoutKernel k, string name, int[] e) : ISpoutSignal
    {
        public void Set()
        {
            k.Note("set " + name);
            e[0]++;
        }

        public void Dispose()
        {
            // As Counter.
        }
    }
}

/// <summary>Textures in memory: a sender's is made with the next share handle and its rows kept; a receiver opens it by handle and sees
/// the same rows; a copy into a staging slot snapshots them; a slot reads back after <see cref="PendingReads"/> "still drawing"
/// answers. <see cref="FailNextCopy"/> makes the next copy throw, <see cref="KeyedNext"/> makes the next texture keyed.</summary>
internal sealed class FakeSpoutGpu : ISpoutGpu
{
    private readonly Dictionary<uint, FakeTexture> _byHandle = [];
    private uint _nextHandle = 0x4000_0040;

    public string Describe => "fake Direct3D 11";

    public int PendingReads { get; set; }

    public bool FailNextCopy { get; set; }

    public bool KeyedNext { get; set; }

    public int Opens { get; private set; }

    public FakeTexture? Sender(uint handle) => _byHandle.GetValueOrDefault(handle);

    public ISpoutTexture? Open(uint handle, out string words)
    {
        Opens++;
        if (!_byHandle.TryGetValue(handle, out var t) || t.Disposed)
        {
            words = "no texture of that handle";
            return null;
        }
        words = "";
        return new FakeView(this, t);
    }

    public ISpoutTexture Create(int width, int height, uint format)
    {
        var t = new FakeTexture(_nextHandle++, width, height, format, KeyedNext);
        KeyedNext = false;
        _byHandle[t.Handle] = t;
        return t;
    }

    public void Dispose()
    {
        // Nothing native.
    }

    internal sealed class FakeTexture(uint handle, int width, int height, uint format, bool keyed) : ISpoutTexture
    {
        public byte[] Rows = new byte[width * height * 4];
        public object? KeyHolder;

        public uint Handle => handle;
        public int Width => width;
        public int Height => height;
        public uint Format => format;
        public bool Keyed => keyed;
        public nint Native => (nint)handle;
        public bool Disposed { get; private set; }
        public int Flushes { get; private set; }

        public LockTry AcquireKeyed() => LockTry.Acquired;

        public void ReleaseKeyed()
        {
            // The sender's own texture holds no key here.
        }

        public void CopyToStaging(int slot) => throw new InvalidOperationException("a sender's texture has no staging copies");

        public StagingRead ReadStaging(int slot, Span<byte> destination, int stride) => StagingRead.Failed;

        public void Upload(ReadOnlySpan<byte> pixels, int stride)
        {
            for (var y = 0; y < height; y++)
                pixels.Slice(y * stride, width * 4).CopyTo(Rows.AsSpan(y * width * 4));
        }

        public void Flush() => Flushes++;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeView(FakeSpoutGpu gpu, FakeTexture t) : ISpoutTexture
    {
        private readonly byte[][] _staging = [new byte[t.Rows.Length], new byte[t.Rows.Length], new byte[t.Rows.Length]];
        private readonly int[] _waits = new int[3];

        public uint Handle => t.Handle;
        public int Width => t.Width;
        public int Height => t.Height;
        public uint Format => t.Format;
        public bool Keyed => t.Keyed;
        public nint Native => (nint)t.Handle;

        public LockTry AcquireKeyed()
        {
            if (t.KeyHolder is not null)
                return LockTry.Busy;
            t.KeyHolder = this;
            return LockTry.Acquired;
        }

        public void ReleaseKeyed() => t.KeyHolder = null;

        public void CopyToStaging(int slot)
        {
            if (gpu.FailNextCopy)
            {
                gpu.FailNextCopy = false;
                throw new InvalidOperationException("the device was removed");
            }
            t.Rows.CopyTo(_staging[slot], 0);
            _waits[slot] = gpu.PendingReads;
        }

        public StagingRead ReadStaging(int slot, Span<byte> destination, int stride)
        {
            if (_waits[slot]-- > 0)
                return StagingRead.Pending;
            for (var y = 0; y < t.Height; y++)
                _staging[slot].AsSpan(y * t.Width * 4, t.Width * 4).CopyTo(destination[(y * stride)..]);
            return StagingRead.Ready;
        }

        public void Upload(ReadOnlySpan<byte> pixels, int stride) => throw new InvalidOperationException("a received texture is read only");

        public void Flush()
        {
            // Nothing queued.
        }

        public void Dispose()
        {
            // The sender owns the texture.
        }
    }
}
