using System.Globalization;

namespace PlayMap.Media.Video.Spout;

/// <summary>What one receive step did.</summary>
public enum ReceiveStep
{
    /// <summary>A new frame was copied (into a staging slot, read back at a later step).</summary>
    Copied,
    /// <summary>The frame counter has not moved: the sender drew nothing new.</summary>
    NotNew,
    /// <summary>The sender holds the texture: nothing was read, the frame counter untouched (the next step copies its frame).</summary>
    Busy,
    /// <summary>No sender of that name, or its texture could not be opened (words beside it).</summary>
    Absent,
    /// <summary>The copy failed; the frame counter untouched, so no sender frame is skipped.</summary>
    Failed,
}

/// <summary>One Spout sender received, in Spout's order (Spout 2.007 SpoutFrameCount.cpp): the sender's info read under its info
/// mutex (0 ms; the last one kept while it is busy) and its texture opened again whenever the handle or size changes; then the access
/// lock first — the texture's keyed mutex at key 0 where it has one, else <c>&lt;sender&gt;_SpoutAccessMutex</c> at 0 ms (an old
/// sender with neither is read without one); inside it the frame counter read (take one, give one back, the count before) and a
/// count equal to the last copied one is not new; then the copy into a staging slot, the lock released, the sync event set, and only
/// then the count committed — a busy lock or a failed copy leaves it, so the next step copies frame N. A copied slot is read back at
/// a later step without waiting. Never blocks. One thread (the receive thread); allocation-free once its texture is open.</summary>
public sealed class SpoutReceiver : IDisposable
{
    public const int StagingSlots = 3;

    private readonly ISpoutKernel _kernel;
    private readonly ISpoutGpu _gpu;
    private readonly string _name;
    private ISpoutMap? _infoMap;
    private ISpoutLock? _infoLock;
    private ISpoutLock? _access;
    private ISpoutCounter? _counter;
    private ISpoutSignal? _sync;
    private ISpoutTexture? _texture;
    private SpoutInfo _info;
    private bool _haveInfo;
    private int _lastCount = -1;
    private int _next;
    private int _pending = -1;
    private bool _abandonedSeen;

    public SpoutReceiver(ISpoutKernel kernel, ISpoutGpu gpu, string name)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(name);
        _kernel = kernel;
        _gpu = gpu;
        _name = name;
    }

    public string Name => _name;

    /// <summary>The sender's info as last read.</summary>
    public SpoutInfo Info => _info;

    /// <summary>The frame counter's value at the last copy (−1 before any).</summary>
    public int LastCount => _lastCount;

    /// <summary>Steps that found the sender holding its texture.</summary>
    public long Busy { get; private set; }

    /// <summary>Frames copied.</summary>
    public long Copies { get; private set; }

    /// <summary>Why the last step found no sender or failed ("" otherwise).</summary>
    public string Words { get; private set; } = "";

    /// <summary>A staging slot holds a copy not yet read back.</summary>
    public bool HasPending => _pending >= 0;

    /// <summary>A lock whose holder died was taken (once said).</summary>
    public bool SawAbandoned => _abandonedSeen;

    public int Width => _texture?.Width ?? 0;

    public int Height => _texture?.Height ?? 0;

    public uint Format => _texture?.Format ?? 0;

    /// <summary>One receive step (see the type's summary).</summary>
    public ReceiveStep Step()
    {
        if (!RefreshInfo())
            return ReceiveStep.Absent;
        if (_texture is null && !OpenTexture())
            return ReceiveStep.Absent;
        var texture = _texture!;
        var keyed = texture.Keyed;
        var taken = keyed ? texture.AcquireKeyed() : _access?.TryEnter(0) ?? LockTry.Acquired;
        if (taken == LockTry.Busy)
        {
            Busy++;
            return ReceiveStep.Busy;
        }
        if (taken == LockTry.Abandoned)
            _abandonedSeen = true;
        var count = _lastCount + 1;
        try
        {
            if (_counter is { } counter)
            {
                // Given back only what was taken: another receiver racing for the count must not raise the sender's counter.
                count = counter.TryTake(0) ? counter.Release(1) : 0;
                if (count == _lastCount)
                    return ReceiveStep.NotNew;
            }
            var slot = _next;
            try
            {
                texture.CopyToStaging(slot);
            }
            catch (InvalidOperationException ex)
            {
                Words = $"the copy of {_name}'s frame failed: {ex.Message}";
                return ReceiveStep.Failed;
            }
            _next = (_next + 1) % StagingSlots;
            _pending = slot;
        }
        finally
        {
            if (keyed)
                texture.ReleaseKeyed();
            else if (_access is not null && taken != LockTry.Busy)
                _access.Exit();
        }
        _sync?.Set();
        _lastCount = count;
        Copies++;
        Words = "";
        return ReceiveStep.Copied;
    }

    /// <summary>Reads the oldest copied slot back into <paramref name="destination"/> without waiting: Ready (the slot is free
    /// again), Pending (try at a later step) or Failed (the slot is let go).</summary>
    public StagingRead ReadPending(Span<byte> destination, int stride)
    {
        if (_pending < 0 || _texture is null)
            return StagingRead.Failed;
        var r = _texture.ReadStaging(_pending, destination, stride);
        if (r != StagingRead.Pending)
            _pending = -1;
        return r;
    }

    /// <summary>Reads the sender's info (0 ms on its mutex; a busy mutex keeps the last info) and reopens the texture when its handle,
    /// size or format moved. False with words when no sender of the name exists.</summary>
    private bool RefreshInfo()
    {
        if (_infoMap is null)
        {
            _infoMap = _kernel.OpenMap(_name);
            if (_infoMap is null)
            {
                Words = $"no Spout sender named '{_name}'";
                return false;
            }
            _infoLock = _kernel.OpenMutex(SpoutNames.MutexOf(_name));
            _access = _kernel.OpenMutex(SpoutNames.AccessMutex(_name));
            _counter = _kernel.OpenSemaphore(SpoutNames.Counter(_name));
            _sync = _kernel.OpenEvent(SpoutNames.SyncEvent(_name));
        }
        var taken = _infoLock?.TryEnter(0) ?? LockTry.Acquired;
        if (taken == LockTry.Busy)
            return _haveInfo;
        SpoutInfo info;
        try
        {
            // The texture's fields first: the description (a string) is read only when the texture changed, so a steady step
            // allocates nothing.
            if (_haveInfo && SpoutInfo.SameTexture(_infoMap.Bytes, _info))
                return true;
            info = SpoutInfo.Read(_infoMap.Bytes);
        }
        finally
        {
            if (taken != LockTry.Busy)
                _infoLock?.Exit();
        }
        _info = info;
        _haveInfo = true;
        CloseTexture();
        if (info.ShareHandle == 0)
        {
            // The sender has gone: its info map is left with no texture.
            Words = $"Spout sender '{_name}' shares no texture";
            CloseObjects();
            return false;
        }
        return true;
    }

    private bool OpenTexture()
    {
        if (SpoutFormats.Layout(_info.Format, out var why) is null)
        {
            Words = why;
            return false;
        }
        _texture = _gpu.Open(_info.ShareHandle, out var words);
        if (_texture is null)
        {
            Words = string.Create(CultureInfo.InvariantCulture, $"{_name}'s texture (handle 0x{_info.ShareHandle:X}) could not be opened: {words}");
            return false;
        }
        _lastCount = -1;
        _pending = -1;
        return true;
    }

    private void CloseTexture()
    {
        _texture?.Dispose();
        _texture = null;
        _pending = -1;
    }

    private void CloseObjects()
    {
        _sync?.Dispose();
        _counter?.Dispose();
        _access?.Dispose();
        _infoLock?.Dispose();
        _infoMap?.Dispose();
        (_sync, _counter, _access, _infoLock, _infoMap) = (null, null, null, null, null);
        _haveInfo = false;
    }

    public void Dispose()
    {
        CloseTexture();
        CloseObjects();
    }
}
