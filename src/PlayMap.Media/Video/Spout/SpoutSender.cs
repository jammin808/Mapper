using System.Globalization;

namespace PlayMap.Media.Video.Spout;

/// <summary>One Spout sender, in Spout's order (Spout 2.007 SpoutSenderNames.cpp, SpoutGL.cpp):
/// <list type="bullet">
/// <item><b>Register</b> (off the frame path: the list mutex may wait 67 ms): the name checked, inserted in the sender list under its
/// mutex — refused when a live sender already has it — made the active sender when none is; the shared texture made; its 280-byte info
/// map and mutex written (the handle, size, format, usage 0, the exe path, partner id 0: a DirectX sender); the access mutex, the frame
/// counter (a semaphore from 1), the sync event and the data map made.</item>
/// <item><b>Each frame</b>: the access mutex tried at 0 ms (a receiver holding it skips the frame, counted busy — never a wait; one
/// whose holder died counts as taken, said once); the picture drawn or uploaded; the counter taken once and given back twice (net one
/// more); the mutex released; the sync event set; PlayMap's facts written to the data map at 0 ms (skipped when busy).</item>
/// <item><b>A new size</b> remakes the texture and rewrites the info under the same name.</item>
/// <item><b>Unregister</b> removes the name from the list and closes every object (the maps go with the last handle).</item>
/// </list>
/// The frame path is one thread (the sender's worker, or the render thread for a GPU target), which takes and releases the access
/// mutex; allocation-free per frame once registered.</summary>
public sealed class SpoutSender : IDisposable
{
    private readonly ISpoutKernel _kernel;
    private readonly ISpoutGpu _gpu;
    private readonly SpoutDataWriter? _data;
    private readonly uint _format;
    private ISpoutMap? _list;
    private ISpoutLock? _listLock;
    private ISpoutMap? _active;
    private ISpoutMap? _infoMap;
    private ISpoutLock? _infoLock;
    private ISpoutLock? _access;
    private ISpoutCounter? _counter;
    private ISpoutSignal? _sync;
    private ISpoutMap? _dataMap;
    private ISpoutLock? _dataLock;
    private ISpoutTexture? _texture;
    private bool _registered;
    private bool _holding;

    public SpoutSender(ISpoutKernel kernel, ISpoutGpu gpu, string name, uint format, SpoutDataWriter? data = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(gpu);
        _kernel = kernel;
        _gpu = gpu;
        Name = name;
        _format = format;
        _data = data;
    }

    public string Name { get; }

    public ISpoutTexture? Texture => _texture;

    public long Frames { get; private set; }

    /// <summary>Frames skipped because a receiver held the texture.</summary>
    public long Busy { get; private set; }

    /// <summary>A receiver died holding the access mutex (said once).</summary>
    public bool SawAbandoned { get; private set; }

    /// <summary>Registers the name and makes the shared texture and the sender's objects; throws <see cref="InvalidOperationException"/>
    /// with words (a bad name, a name taken, the list held by another app, Spout set to memory share).</summary>
    public void Register(int width, int height)
    {
        if (_registered)
            throw new InvalidOperationException($"{Name} is registered already");
        if (SpoutNames.Invalid(Name) is { Length: > 0 } bad)
            throw new InvalidOperationException(bad);
        if (_kernel.MemoryShare)
            throw new InvalidOperationException("this PC's Spout is set to 2.006 memory share (SpoutSettings); PlayMap shares GPU textures only");
        // The list and the active-sender maps are held open while registered: a page-file map lives only while a handle holds it.
        _list = _kernel.CreateMap(SpoutNames.List, SpoutNames.MaxSenders * SpoutNames.NameBytes);
        _listLock = _kernel.CreateMutex(SpoutNames.MutexOf(SpoutNames.List));
        _active = _kernel.CreateMap(SpoutNames.Active, SpoutNames.NameBytes);
        var list = _list;
        var listLock = _listLock;
        try
        {
            if (listLock.TryEnter(SpoutNames.ListWaitMs) == LockTry.Busy)
                throw new InvalidOperationException("another app holds the Spout sender list: try again");
            try
            {
                var names = SpoutNames.Read(list.Bytes);
                if (names.Contains(Name, StringComparer.Ordinal))
                {
                    using var live = _kernel.OpenMap(Name);
                    if (live is not null)
                    {
                        var owner = SpoutInfo.Read(live.Bytes).Description;
                        throw new InvalidOperationException($"a Spout sender named '{Name}' already exists{(owner.Length > 0 ? ": " + owner : "")}");
                    }
                }
                else if (names.Count >= SpoutNames.MaxSenders)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"the Spout sender list is full ({SpoutNames.MaxSenders} senders)"));
                }
                names.Add(Name);
                SpoutNames.Write(list.Bytes, names);
                MakeActiveWhenNone(names);
            }
            finally
            {
                listLock.Exit();
            }
        }
        catch
        {
            CloseList();
            throw;
        }
        _registered = true;
        try
        {
            _infoMap = _kernel.CreateMap(Name, SpoutInfo.Size);
            _infoLock = _kernel.CreateMutex(SpoutNames.MutexOf(Name));
            _access = _kernel.CreateMutex(SpoutNames.AccessMutex(Name));
            _counter = _kernel.CreateSemaphore(SpoutNames.Counter(Name), 1, int.MaxValue);
            _sync = _kernel.CreateEvent(SpoutNames.SyncEvent(Name));
            _dataMap = _kernel.CreateMap(SpoutNames.DataMap(Name), SpoutDataWriter.MapBytes);
            _dataLock = _kernel.CreateMutex(SpoutNames.MutexOf(SpoutNames.DataMap(Name)));
            Resize(width, height);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The active sender is ours when none is, or when it names a sender no longer listed.</summary>
    private void MakeActiveWhenNone(List<string> names)
    {
        var active = _active!;
        var current = SpoutNames.Read(active.Bytes);
        if (current.Count > 0 && names.Contains(current[0], StringComparer.Ordinal) && current[0] != Name)
            return;
        SpoutNames.Write(active.Bytes, [Name]);
    }

    /// <summary>Makes the texture at a new size (the same name) and writes its info. Off the frame path, or between frames.</summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_texture is { } t && t.Width == width && t.Height == height)
            return;
        _texture?.Dispose();
        _texture = _gpu.Create(width, height, _format);
        var info = new SpoutInfo(_texture.Handle, (uint)width, (uint)height, _format, 0, _kernel.ProcessPath, 0);
        var taken = _infoLock!.TryEnter(SpoutNames.ListWaitMs);
        try
        {
            info.Write(_infoMap!.Bytes);
        }
        finally
        {
            if (taken != LockTry.Busy)
                _infoLock.Exit();
        }
    }

    /// <summary>The texture to draw into this frame, or false (a receiver holds it: skipped, counted). The render thread asks with
    /// 0 ms; a sender's own worker may wait a little (<paramref name="waitMs"/>) for a receiver to finish its copy.</summary>
    public bool TryBegin(out ISpoutTexture texture, int waitMs = 0)
    {
        texture = _texture!;
        if (!_registered || _texture is null || _holding)
            return false;
        var taken = _access!.TryEnter(waitMs);
        if (taken == LockTry.Busy)
        {
            Busy++;
            return false;
        }
        if (taken == LockTry.Abandoned)
            SawAbandoned = true;
        _holding = true;
        return true;
    }

    /// <summary>Frame path: the frame drawn (or not) since <see cref="TryBegin"/>: counted, the lock released, receivers told.</summary>
    public void End(bool drawn, long leaderNs = 0)
    {
        if (!_holding)
            return;
        try
        {
            if (drawn)
            {
                _texture!.Flush();
                _ = _counter!.TryTake(0);
                _ = _counter.Release(2);
                Frames++;
            }
        }
        finally
        {
            _holding = false;
            _access!.Exit();
        }
        if (!drawn)
            return;
        _sync!.Set();
        if (_data is not null && _dataLock!.TryEnter(0) != LockTry.Busy)
        {
            try
            {
                _ = _data.Write(_dataMap!.Bytes, Frames, leaderNs, _texture!.Width, _texture.Height);
            }
            finally
            {
                _dataLock.Exit();
            }
        }
    }

    /// <summary>Uploads one frame's rows (top first) and tells receivers; false when a receiver held the texture past
    /// <paramref name="waitMs"/>.</summary>
    public bool Publish(ReadOnlySpan<byte> pixels, int stride, long leaderNs = 0, int waitMs = 0)
    {
        if (!TryBegin(out var texture, waitMs))
            return false;
        var drawn = false;
        try
        {
            texture.Upload(pixels, stride);
            drawn = true;
        }
        finally
        {
            End(drawn, leaderNs);
        }
        return true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            _registered = false;
            Unregister();
        }
        _texture?.Dispose();
        _dataLock?.Dispose();
        _dataMap?.Dispose();
        _sync?.Dispose();
        _counter?.Dispose();
        _access?.Dispose();
        _infoLock?.Dispose();
        _infoMap?.Dispose();
        (_texture, _dataLock, _dataMap, _sync, _counter, _access, _infoLock, _infoMap) = (null, null, null, null, null, null, null, null);
        CloseList();
    }

    private void CloseList()
    {
        _active?.Dispose();
        _listLock?.Dispose();
        _list?.Dispose();
        (_active, _listLock, _list) = (null, null, null);
    }

    /// <summary>Removes the name from the list (and from the active sender) under the list mutex; a list held past Spout's wait is
    /// written anyway, as Spout's own release does.</summary>
    private void Unregister()
    {
        if (_list is not { } list || _listLock is not { } listLock)
            return;
        var taken = listLock.TryEnter(SpoutNames.ListWaitMs);
        try
        {
            var names = SpoutNames.Read(list.Bytes);
            if (names.Remove(Name))
                SpoutNames.Write(list.Bytes, names);
            if (_active is { } active && SpoutNames.Read(active.Bytes) is [var current] && current == Name)
                SpoutNames.Write(active.Bytes, names.Count > 0 ? [names[0]] : []);
        }
        finally
        {
            if (taken != LockTry.Busy)
                listLock.Exit();
        }
        // The info map outlives us in a receiver that still holds it: no texture is what a gone sender looks like.
        if (_infoMap is not null)
            new SpoutInfo(0, 0, 0, 0, 0, "", 0).Write(_infoMap.Bytes);
    }
}
