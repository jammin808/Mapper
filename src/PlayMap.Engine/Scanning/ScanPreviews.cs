using PlayMap.Core.Media;
using PlayMap.Core.Scan;

namespace PlayMap.Engine.Scanning;

/// <summary>Where a scan's pictures go for the desk: "scan-camera" (what the camera sees while a scan runs),
/// "scan-&lt;scan&gt;-smart" (the first projector's smart scan) and "scan-&lt;scan&gt;-confidence" (how sure the decode
/// was, in the same projector space). A small seam: the node's preview views take these pictures when they offer
/// a feed for cameras and scans; until then <see cref="PreviewRingPublisher"/> writes the rings directly.</summary>
public interface IScanPreviews
{
    /// <summary>Publishes one picture: gray8 (<paramref name="channels"/> 1) or BGRA (4), rows of width × channels.</summary>
    void Publish(string viewId, ReadOnlySpan<byte> pixels, int width, int height, int channels, long stampNs);
}

/// <summary>The preview rings themselves (<see cref="PreviewRing.PathFor"/> under the node's id), one writer per
/// view, made at the first picture and kept until disposed; a picture larger than the ring's bound is thinned by
/// whole steps; grey becomes BGRA. Thread-safe; a publish never waits for a reader.</summary>
public sealed class PreviewRingPublisher(string nodeId, int maxWidth = 1920, int maxHeight = 1200) : IScanPreviews, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PreviewRingWriter> _rings = new(StringComparer.Ordinal);
    private byte[] _scratch = [];
    private bool _disposed;

    /// <summary>The last failure to make or write a ring, in words ("" while they work).</summary>
    public string Problem { get; private set; } = "";

    public void Publish(string viewId, ReadOnlySpan<byte> pixels, int width, int height, int channels, long stampNs)
    {
        if (width < 1 || height < 1 || channels is not (1 or 4) || pixels.Length < width * height * channels)
            return;
        var step = Math.Max(1, Math.Max((width + maxWidth - 1) / maxWidth, (height + maxHeight - 1) / maxHeight));
        var w = width / step;
        var h = height / step;
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                if (!_rings.TryGetValue(viewId, out var ring) || ring.MaxWidth < w || ring.MaxHeight < h)
                {
                    ring?.Dispose();
                    ring = new PreviewRingWriter(PreviewRing.PathFor(nodeId, viewId), w, h);
                    _rings[viewId] = ring;
                }
                if (_scratch.Length < w * h * 4)
                    _scratch = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var s = ((y * step) * width + x * step) * channels;
                        var o = (y * w + x) * 4;
                        if (channels == 1)
                            _scratch[o] = _scratch[o + 1] = _scratch[o + 2] = pixels[s];
                        else
                        {
                            _scratch[o] = pixels[s];
                            _scratch[o + 1] = pixels[s + 1];
                            _scratch[o + 2] = pixels[s + 2];
                        }
                        _scratch[o + 3] = 255;
                    }
                }
                ring.Write(_scratch.AsSpan(0, w * h * 4), w, h, w * 4, stampNs);
                Problem = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A ring that cannot be made leaves the desk drawing its stand-in; the scan carries on.
                Problem = $"the preview {viewId} could not be written: {ex.Message}";
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var ring in _rings.Values)
                ring.Dispose();
            _rings.Clear();
        }
    }
}

/// <summary>The scan's pictures as preview frames: the smart scan in colour when the camera gave colour, and the
/// confidence as a heat ramp (red unsure, yellow, green sure; black where nothing was decoded).</summary>
public static class ScanPictures
{
    public static string CameraView => "scan-camera";

    public static string SmartView(string scanId) => $"scan-{scanId}-smart";

    public static string ConfidenceView(string scanId) => $"scan-{scanId}-confidence";

    /// <summary>The smart scan as BGRA.</summary>
    public static byte[] Smart(ProjectorView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var n = view.Width * view.Height;
        var bgra = new byte[n * 4];
        for (var k = 0; k < n; k++)
        {
            if (view.SmartScanColour is { } colour)
            {
                var c = colour[k];
                bgra[4 * k] = (byte)(c & 0xFF);
                bgra[4 * k + 1] = (byte)((c >> 8) & 0xFF);
                bgra[4 * k + 2] = (byte)((c >> 16) & 0xFF);
            }
            else
            {
                bgra[4 * k] = bgra[4 * k + 1] = bgra[4 * k + 2] = view.SmartScan[k];
            }
            bgra[4 * k + 3] = 255;
        }
        return bgra;
    }

    /// <summary>The decode's confidence in projector space as BGRA.</summary>
    public static byte[] Confidence(ProjectorView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var n = view.Width * view.Height;
        var bgra = new byte[n * 4];
        for (var k = 0; k < n; k++)
        {
            bgra[4 * k + 3] = 255;
            if (view.Coverage[k] == ProjectorCoverage.None)
                continue;
            var c = Math.Clamp(view.Confidence[k], 0f, 1f);
            // Red at 0 through yellow at 0.5 to green at 1.
            var r = c < 0.5f ? 1f : 2 * (1 - c);
            var g = c < 0.5f ? 2 * c : 1f;
            bgra[4 * k + 1] = (byte)(255 * g);
            bgra[4 * k + 2] = (byte)(255 * r);
        }
        return bgra;
    }
}
