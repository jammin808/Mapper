using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace PlayMap.Core.Media;

/// <summary>What a reader got from a preview ring.</summary>
public readonly record struct PreviewFrameInfo(int Width, int Height, int Stride, long Sequence, long StampNs);

/// <summary>Preview pictures from the engine to the desk across the process boundary: a file-backed memory
/// map with three BGRA slots and a seqlock per slot. The
/// writer never waits for the reader (it writes the slot after the one last published); the reader copies the
/// newest complete slot and retries when the writer lapped it mid-copy. One writer per ring. The file lives in a
/// folder only this user can write.</summary>
public static class PreviewRing
{
    public const int Magic = 0x504D5052; // "PMPR"
    public const int Slots = 3;
    public const int HeaderBytes = 64;
    public const int SlotHeaderBytes = 64;

    /// <summary>A folder only this user can write: the session's runtime folder on Linux (memory-backed),
    /// the user's local application data elsewhere.</summary>
    public static string DefaultFolder()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var folder = !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)
            ? Path.Combine(runtime, "playmap")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "PlayMap", "run");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The ring's file for an engine and a view ("playmap-&lt;node&gt;-&lt;view&gt;.preview").</summary>
    public static string PathFor(string nodeId, string viewId) =>
        Path.Combine(DefaultFolder(), $"playmap-{nodeId}-{viewId}.preview");

    public static long SlotBytes(int maxWidth, int maxHeight) => SlotHeaderBytes + (long)maxWidth * maxHeight * 4;

    public static long FileBytes(int maxWidth, int maxHeight) => HeaderBytes + Slots * SlotBytes(maxWidth, maxHeight);
}

/// <summary>The engine's side of a preview ring.</summary>
public sealed class PreviewRingWriter : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _slotBytes;
    private int _next;
    private long _sequence;

    public PreviewRingWriter(string path, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeight, 1);
        Path = path;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        _slotBytes = PreviewRing.SlotBytes(maxWidth, maxHeight);
        var bytes = PreviewRing.FileBytes(maxWidth, maxHeight);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            fs.SetLength(bytes);
        _map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, bytes, MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.ReadWrite);
        _view.Write(4, maxWidth);
        _view.Write(8, maxHeight);
        _view.Write(12, PreviewRing.Slots);
        _view.Write(16, -1L); // newest published slot: none yet
        _view.Write(0, PreviewRing.Magic);
        _view.Flush();
    }

    public string Path { get; }
    public int MaxWidth { get; }
    public int MaxHeight { get; }

    /// <summary>Publishes a BGRA picture (rows of <paramref name="stride"/> bytes). Larger than the ring's
    /// maximum is refused (the caller scales previews to the ring).</summary>
    public unsafe bool Write(ReadOnlySpan<byte> bgra, int width, int height, int stride, long stampNs)
    {
        if (width < 1 || height < 1 || width > MaxWidth || height > MaxHeight || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
            return false;
        var slot = _next;
        _next = (_next + 1) % PreviewRing.Slots;
        var off = PreviewRing.HeaderBytes + slot * _slotBytes;
        byte* basePtr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            basePtr += _view.PointerOffset;
            var header = basePtr + off;
            // Seqlock: odd while writing, even when complete.
            if ((Interlocked.Increment(ref *(long*)header) & 1) == 0)
                Interlocked.Increment(ref *(long*)header);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(header + 8, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(header + 12, 4), height);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(header + 16, 4), width * 4);
            var frameSeq = ++_sequence;
            BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(header + 24, 8), frameSeq);
            BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(header + 32, 8), stampNs);
            var dst = new Span<byte>(header + PreviewRing.SlotHeaderBytes, width * 4 * height);
            for (var y = 0; y < height; y++)
                bgra.Slice(y * stride, width * 4).CopyTo(dst.Slice(y * width * 4, width * 4));
            Interlocked.Increment(ref *(long*)header);
            Interlocked.Exchange(ref *(long*)(basePtr + 16), slot);
        }
        finally
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        return true;
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A reader still holding the map on Windows keeps the file; it is recreated on the next run.
        }
        catch (UnauthorizedAccessException)
        {
            // As above: the file is reused, not leaked across runs.
        }
    }
}

/// <summary>The desk's side of a preview ring.</summary>
public sealed class PreviewRingReader : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _slotBytes;

    public PreviewRingReader(string path)
    {
        _map = MemoryMappedFile.CreateFromFile(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete),
            null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        if (_view.ReadInt32(0) != PreviewRing.Magic)
        {
            _view.Dispose();
            _map.Dispose();
            throw new InvalidDataException("Not a PlayMap preview ring.");
        }
        MaxWidth = _view.ReadInt32(4);
        MaxHeight = _view.ReadInt32(8);
        _slotBytes = PreviewRing.SlotBytes(MaxWidth, MaxHeight);
    }

    public int MaxWidth { get; }
    public int MaxHeight { get; }

    /// <summary>Copies the newest complete picture into <paramref name="destination"/> (tightly packed BGRA)
    /// when it is newer than <paramref name="haveSequence"/>. False when there is nothing newer or the writer
    /// kept lapping the copy (the next poll will get it).</summary>
    public unsafe bool TryRead(Span<byte> destination, long haveSequence, out PreviewFrameInfo info)
    {
        info = default;
        byte* basePtr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            basePtr += _view.PointerOffset;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var slot = Volatile.Read(ref *(long*)(basePtr + 16));
                if (slot < 0)
                    return false;
                var header = basePtr + PreviewRing.HeaderBytes + slot * _slotBytes;
                var before = Volatile.Read(ref *(long*)header);
                if ((before & 1) != 0)
                    continue;
                var width = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(header + 8, 4));
                var height = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(header + 12, 4));
                var stride = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(header + 16, 4));
                var seq = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(header + 24, 8));
                var stamp = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(header + 32, 8));
                if (seq <= haveSequence)
                    return false;
                var bytes = stride * height;
                if (width < 1 || height < 1 || width > MaxWidth || height > MaxHeight || destination.Length < bytes)
                    return false;
                new ReadOnlySpan<byte>(header + PreviewRing.SlotHeaderBytes, bytes).CopyTo(destination);
                if (Volatile.Read(ref *(long*)header) != before)
                    continue;
                info = new PreviewFrameInfo(width, height, stride, seq, stamp);
                return true;
            }
            return false;
        }
        finally
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
    }
}
