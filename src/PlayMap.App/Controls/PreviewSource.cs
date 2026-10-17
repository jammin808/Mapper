using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PlayMap.Core.Media;

namespace PlayMap.App.Controls;

/// <summary>Where a preview view's pictures come from. Local engines write a <see cref="PreviewRing"/> per view
/// (shared memory); the integrator can supply another feed (a remote node's JPEG frames) through
/// <see cref="PreviewFeeds.Resolve"/>.</summary>
public interface IPreviewSource : IDisposable
{
    /// <summary>The newest picture when it is newer than the last one returned; null otherwise (or when the
    /// view is not up). The bitmap is reused between calls (one per source).</summary>
    WriteableBitmap? Poll();
}

/// <summary>Picks the preview source for a node and a view. The default reads this machine's preview rings
/// (PreviewRing.PathFor(node, view)); an integrator replaces <see cref="Resolve"/> to add remote feeds.</summary>
public static class PreviewFeeds
{
    /// <summary>The source for a view of a node; set once at start-up by the composition.</summary>
    public static Func<string, string, IPreviewSource> Resolve { get; set; } = (node, view) => new RingPreviewSource(PreviewRing.PathFor(node, view));
}

/// <summary>Reads one preview ring: opens it when its file appears (checked at most once a second), copies the
/// newest complete BGRA slot into a bitmap, and lets go when the ring goes away (engine restarted). A ring that brings no
/// new picture for a second is checked against the file at its path: when the engine took the view's ring away or made a
/// new one there (the view shown again after its lease ran out), the mapped ring is released and the new one opened.</summary>
public sealed class RingPreviewSource : IPreviewSource
{
    private readonly string _path;
    private readonly TimeSpan _probeEvery;
    private PreviewRingReader? _reader;
    private byte[] _buffer = [];
    private WriteableBitmap? _bitmap;
    private long _sequence;
    private long _lastProbe;
    private long _lastNews;

    public RingPreviewSource(string path)
        : this(path, TimeSpan.FromSeconds(1))
    {
    }

    /// <param name="probeEvery">How often a missing ring is looked for, and how long a quiet ring waits before it is checked.</param>
    internal RingPreviewSource(string path, TimeSpan probeEvery)
    {
        _path = path;
        _probeEvery = probeEvery;
    }

    public WriteableBitmap? Poll()
    {
        if (_reader is null && !TryOpen())
            return null;
        if (Quiet() && Replaced())
        {
            // The ring mapped here is not the one at the path any more: let it go and open what is there now.
            Close();
            _lastProbe = 0;
            if (!TryOpen())
                return null;
        }
        var reader = _reader!;
        var need = reader.MaxWidth * reader.MaxHeight * 4;
        if (_buffer.Length < need)
            _buffer = new byte[need];
        PreviewFrameInfo info;
        try
        {
            if (!reader.TryRead(_buffer, _sequence, out info))
                return null;
        }
        catch (IOException)
        {
            // The engine took the ring away mid-read: close it and look for a new one on the next probe.
            Close();
            return null;
        }
        _sequence = info.Sequence;
        _lastNews = Stopwatch.GetTimestamp();
        if (_bitmap is null || _bitmap.PixelSize.Width != info.Width || _bitmap.PixelSize.Height != info.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(info.Width, info.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var fb = _bitmap.Lock())
        {
            for (var y = 0; y < info.Height; y++)
                Marshal.Copy(_buffer, y * info.Stride, fb.Address + y * fb.RowBytes, info.Width * 4);
        }
        return _bitmap;
    }

    private bool TryOpen()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastProbe != 0 && Stopwatch.GetElapsedTime(_lastProbe, now) < _probeEvery)
            return false;
        _lastProbe = now;
        if (!File.Exists(_path))
            return false;
        try
        {
            _reader = new PreviewRingReader(_path);
            _sequence = 0;
            _lastNews = now;
            return true;
        }
        catch (IOException)
        {
            // Not ready yet (the engine is creating it): the next probe tries again.
            return false;
        }
        catch (InvalidDataException)
        {
            // A file that is not a ring (yet): the next probe tries again.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Another user's ring: never ours to read.
            return false;
        }
    }

    /// <summary>True when the open ring has brought no picture for the probe interval (and that long since the last check).</summary>
    private bool Quiet()
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastNews, now) < _probeEvery)
            return false;
        _lastNews = now;
        return true;
    }

    /// <summary>True when the file at the path is not the ring mapped here: it is gone, or its header or its newest picture's number
    /// differs from what the mapping shows (the same file read twice shows the same bytes).</summary>
    private bool Replaced()
    {
        var reader = _reader!;
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[24];
            file.ReadExactly(head);
            if (BinaryPrimitives.ReadInt32LittleEndian(head) != PreviewRing.Magic || BinaryPrimitives.ReadInt32LittleEndian(head[4..]) != reader.MaxWidth
                || BinaryPrimitives.ReadInt32LittleEndian(head[8..]) != reader.MaxHeight)
                return true;
            var slot = BinaryPrimitives.ReadInt64LittleEndian(head[16..]);
            if (slot < 0)
                return _sequence > 0;
            file.Position = PreviewRing.HeaderBytes + slot * PreviewRing.SlotBytes(reader.MaxWidth, reader.MaxHeight) + 24;
            Span<byte> sequence = stackalloc byte[8];
            file.ReadExactly(sequence);
            return BinaryPrimitives.ReadInt64LittleEndian(sequence) != _sequence;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or EndOfStreamException or UnauthorizedAccessException)
        {
            // Gone, cut short, or (on Windows) deleted while this mapping still holds it: not a ring to keep.
            return true;
        }
        catch (IOException)
        {
            // Busy for a moment: the next quiet second checks again.
            return false;
        }
    }

    private void Close()
    {
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose()
    {
        Close();
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
