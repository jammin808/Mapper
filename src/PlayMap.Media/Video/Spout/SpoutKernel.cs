namespace PlayMap.Media.Video.Spout;

/// <summary>What a try on a shared lock gave: it is ours, ours because its last holder died holding it, or another app holds it.</summary>
public enum LockTry
{
    Acquired,
    Abandoned,
    Busy,
}

/// <summary>A named shared-memory map: its bytes for as long as the handle is held.</summary>
public interface ISpoutMap : IDisposable
{
    Span<byte> Bytes { get; }
}

/// <summary>A named mutex: tried with a timeout (0 on the frame path), released by the thread that took it.</summary>
public interface ISpoutLock : IDisposable
{
    LockTry TryEnter(int milliseconds);

    void Exit();
}

/// <summary>A named semaphore: Spout's frame counter (a receiver takes one and gives it back to read the count; a sender takes one
/// and gives two, net one more per frame).</summary>
public interface ISpoutCounter : IDisposable
{
    bool TryTake(int milliseconds);

    /// <summary>Gives <paramref name="count"/> back; answers the count before.</summary>
    int Release(int count);
}

/// <summary>A named auto-reset event (Spout's sync event: a receiver says it took a frame; PlayMap never waits on it).</summary>
public interface ISpoutSignal : IDisposable
{
    void Set();
}

/// <summary>The operating system's named objects Spout 2 shares between apps: maps, mutexes, semaphores and events by name, the
/// running exe's path and Spout's per-user memory-share setting. The Windows kernel stands behind it on a show machine; a fake in
/// tests. Open* answers null when no object of that name exists; Create* makes it or opens the existing one.</summary>
public interface ISpoutKernel
{
    ISpoutMap? OpenMap(string name);

    ISpoutMap CreateMap(string name, int size);

    ISpoutLock? OpenMutex(string name);

    ISpoutLock CreateMutex(string name);

    ISpoutCounter? OpenSemaphore(string name);

    ISpoutCounter CreateSemaphore(string name, int initial, int maximum);

    ISpoutSignal? OpenEvent(string name);

    ISpoutSignal CreateEvent(string name);

    /// <summary>The running exe's path (a sender's description).</summary>
    string ProcessPath { get; }

    /// <summary>Spout 2.006 memory share is chosen for this user (HKCU\Software\Leading Edge\Spout\MemoryShare = 1).</summary>
    bool MemoryShare { get; }
}
