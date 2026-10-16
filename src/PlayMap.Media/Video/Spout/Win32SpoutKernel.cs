using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PlayMap.Media.Video.Spout;

/// <summary>The Windows kernel's named objects as Spout 2 uses them: page-file-backed maps, mutexes, semaphores and auto-reset events
/// in the session's namespace (the same names Spout's ANSI calls make, since PlayMap's names are printable ASCII), the exe's path and
/// Spout's per-user memory-share value. A mutex is released by the thread that took it; an abandoned one counts as taken. A name
/// that is not there is asked of the kernel first (OpenFileMappingW), so looking for an absent sender — every discovery, every few
/// hundred milliseconds of a receiver waiting — throws nothing.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32SpoutKernel : ISpoutKernel
{
    private const uint FileMapReadWrite = 0x0002 | 0x0004;

    [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenFileMapping(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>The map of that name, or null when there is none (asked without an exception; one that goes between the question and
    /// the open is none too).</summary>
    public ISpoutMap? OpenMap(string name)
    {
        var probe = OpenFileMapping(FileMapReadWrite, false, name);
        if (probe == 0)
            return null;
        try
        {
            return new Map(MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        finally
        {
            _ = CloseHandle(probe);
        }
    }

    public ISpoutMap CreateMap(string name, int size) => new Map(MemoryMappedFile.CreateOrOpen(name, size, MemoryMappedFileAccess.ReadWrite));

    public ISpoutLock? OpenMutex(string name) => Mutex.TryOpenExisting(name, out var m) ? new NamedMutex(m) : null;

    public ISpoutLock CreateMutex(string name) => new NamedMutex(new Mutex(false, name));

    public ISpoutCounter? OpenSemaphore(string name) => Semaphore.TryOpenExisting(name, out var s) ? new Counter(s) : null;

    public ISpoutCounter CreateSemaphore(string name, int initial, int maximum) => new Counter(new Semaphore(initial, maximum, name));

    public ISpoutSignal? OpenEvent(string name) => EventWaitHandle.TryOpenExisting(name, out var e) ? new Signal(e) : null;

    public ISpoutSignal CreateEvent(string name) => new Signal(new EventWaitHandle(false, EventResetMode.AutoReset, name));

    public string ProcessPath => Environment.ProcessPath ?? "";

    public bool MemoryShare =>
        Registry.CurrentUser.OpenSubKey(@"Software\Leading Edge\Spout")?.GetValue("MemoryShare") is int v && v == 1;

    private sealed unsafe class Map : ISpoutMap
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _pointer;
        private readonly int _length;

        public Map(MemoryMappedFile file)
        {
            _file = file;
            _view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _pointer = p + _view.PointerOffset;
            _length = (int)Math.Min(int.MaxValue, _view.Capacity);
        }

        public Span<byte> Bytes => new(_pointer, _length);

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
        }
    }

    private sealed class NamedMutex(Mutex mutex) : ISpoutLock
    {
        public LockTry TryEnter(int milliseconds)
        {
            try
            {
                return mutex.WaitOne(milliseconds) ? LockTry.Acquired : LockTry.Busy;
            }
            catch (AbandonedMutexException)
            {
                return LockTry.Abandoned;
            }
        }

        public void Exit() => mutex.ReleaseMutex();

        public void Dispose() => mutex.Dispose();
    }

    private sealed class Counter(Semaphore semaphore) : ISpoutCounter
    {
        public bool TryTake(int milliseconds) => semaphore.WaitOne(milliseconds);

        public int Release(int count) => semaphore.Release(count);

        public void Dispose() => semaphore.Dispose();
    }

    private sealed class Signal(EventWaitHandle handle) : ISpoutSignal
    {
        public void Set() => handle.Set();

        public void Dispose() => handle.Dispose();
    }
}
