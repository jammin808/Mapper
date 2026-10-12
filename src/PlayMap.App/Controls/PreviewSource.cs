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
/// newest complete BGRA slot into a bitmap, and lets go when the ring goes away (engine restarted).</summary>
public sealed class RingPreviewSource : IPreviewSource
{
    private readonly string _path;
    private PreviewRingReader? _reader;
    private byte[] _buffer = [];
    private WriteableBitmap? _bitmap;
    private long _sequence;
    private long _lastProbe;

    public RingPreviewSource(string path) => _path = path;

    public WriteableBitmap? Poll()
    {
        if (_reader is null && !TryOpen())
            return null;
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
        if (_lastProbe != 0 && Stopwatch.GetElapsedTime(_lastProbe, now) < TimeSpan.FromSeconds(1))
            return false;
        _lastProbe = now;
        if (!File.Exists(_path))
            return false;
        try
        {
            _reader = new PreviewRingReader(_path);
            _sequence = 0;
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
