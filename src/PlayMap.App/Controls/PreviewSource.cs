using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PlayMap.App.State;
using PlayMap.Core.Media;

namespace PlayMap.App.Controls;

/// <summary>Where a preview view's pictures come from. A local engine writes a <see cref="PreviewRing"/> per view (shared memory)
/// and names its file; the integrator can supply another feed (a remote node's JPEG frames) through <see cref="PreviewFeeds.Resolve"/>.</summary>
public interface IPreviewSource : IDisposable
{
    /// <summary>The newest picture when it is newer than the last one returned; null otherwise (or when the
    /// view is not up). The bitmap is reused between calls (one per source).</summary>
    WriteableBitmap? Poll();

    /// <summary>False while the source has nothing to read (no ring named, or the one named not open): the view shows its stand-in,
    /// not the last picture.</summary>
    bool Live => true;

    /// <summary>The words the stand-in shows while no picture can come (the node's refusal, or why the ring it names does not open);
    /// null otherwise.</summary>
    string? Words => null;
}

/// <summary>Picks the preview source for a node and a view. The default reads this machine's preview ring that the node names for the
/// view (<see cref="PreviewNames"/>: the VIEW … SHOW reply and STATE <c>machine.views</c>), and no other file; an integrator replaces
/// <see cref="Resolve"/> to add remote feeds.</summary>
public static class PreviewFeeds
{
    /// <summary>The source for a view of a node, given the names the desk's session holds (null: none, so no ring is read); set once
    /// at start-up by the composition.</summary>
    public static Func<string, string, PreviewNames?, IPreviewSource> Resolve { get; set; } =
        (_, view, names) => new RingPreviewSource(() => names?.Of(view));
}

/// <summary>Reads the preview ring the node names for one view, and no other file: opens it when it is named (and again, at most once a
/// second, while it does not open), copies the newest complete BGRA slot into a bitmap, and follows the name: when the node names another
/// ring (a new generation, a restart) the mapped one is let go, disposed, and the new one opened; when it names none the mapped one is let
/// go and the stand-in shows the node's words. A ring that brings no new picture for a second is checked against its file: when the
/// engine took it away, it is let go too.</summary>
public sealed class RingPreviewSource : IPreviewSource
{
    /// <summary>How many of the rings opened <see cref="Opened"/> keeps.</summary>
    private const int OpenedKept = 16;

    private readonly Func<PreviewRingName?> _named;
    private readonly TimeSpan _probeEvery;
    private readonly Queue<string> _opened = new();
    private string? _path;
    private string? _words;
    private PreviewRingReader? _reader;
    private byte[] _buffer = [];
    private WriteableBitmap? _bitmap;
    private long _sequence;
    private long _lastProbe;
    private long _lastNews;

    /// <param name="named">What the node names for the view now (read on every poll); null or no ring: nothing is read.</param>
    public RingPreviewSource(Func<PreviewRingName?> named)
        : this(named, TimeSpan.FromSeconds(1))
    {
    }

    /// <param name="named">What the node names for the view now.</param>
    /// <param name="probeEvery">How often a named ring that does not open is tried again, and how long a quiet ring waits before it is checked.</param>
    internal RingPreviewSource(Func<PreviewRingName?> named, TimeSpan probeEvery)
    {
        ArgumentNullException.ThrowIfNull(named);
        _named = named;
        _probeEvery = probeEvery;
    }

    public bool Live => _reader is not null;

    public string? Words => _reader is null ? _words : null;

    /// <summary>The ring files this source opened, oldest first (the last <see cref="OpenedKept"/>): each one the node named.</summary>
    internal IReadOnlyList<string> Opened => [.. _opened];

    /// <summary>The reader of the ring mapped now, or null.</summary>
    internal PreviewRingReader? Reader => _reader;

    public WriteableBitmap? Poll()
    {
        Follow(_named());
        if (_path is null)
            return null;
        if (_reader is null && !TryOpen())
            return null;
        if (Quiet() && Replaced())
        {
            // The ring mapped here is not the file the node names any more: let it go and open what is there now.
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
            // The engine took the ring away mid-read: close it and try the named ring again on the next probe.
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

    /// <summary>Takes the node's name for the view: a ring other than the one mapped (or none) lets the mapped one go before anything
    /// else is read, and the named one is tried at once.</summary>
    private void Follow(PreviewRingName? named)
    {
        var ring = named?.Ring is { Length: > 0 } r ? r : null;
        if (!string.Equals(ring, _path, StringComparison.Ordinal))
        {
            Close();
            _path = ring;
            _lastProbe = 0;
            _words = null;
        }
        if (ring is null)
            _words = named?.Words;
    }

    private bool TryOpen()
    {
        var path = _path!;
        var now = Stopwatch.GetTimestamp();
        if (_lastProbe != 0 && Stopwatch.GetElapsedTime(_lastProbe, now) < _probeEvery)
            return false;
        _lastProbe = now;
        var file = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            _words = $"The node names the ring {file} for this view, and it is not there";
            return false;
        }
        try
        {
            _reader = new PreviewRingReader(path);
            _sequence = 0;
            _lastNews = now;
            _words = null;
            _opened.Enqueue(path);
            while (_opened.Count > OpenedKept)
                _opened.Dequeue();
            return true;
        }
        catch (IOException ex)
        {
            // Not ready yet (the engine is creating it): the next probe tries again.
            _words = $"The ring {file} the node names for this view does not open: {ex.Message}";
            return false;
        }
        catch (InvalidDataException)
        {
            // A file that is not a ring (yet): the next probe tries again.
            _words = $"The file {file} the node names for this view is not a preview ring";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Another user's ring: never ours to read.
            _words = $"The ring {file} the node names for this view is another user's";
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

    /// <summary>True when the file the node names is not the ring mapped here: it is gone, or its header or its newest picture's number
    /// differs from what the mapping shows (the same file read twice shows the same bytes).</summary>
    private bool Replaced()
    {
        var reader = _reader!;
        try
        {
            using var file = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
