using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Frames;
using PlayMap.Media.Sources;

namespace PlayMap.Engine.Scanning;

/// <summary>One camera picture as a scan reads it: gray8, row-major, contiguous (no stride), with the monotonic
/// instant it arrived (ns, <see cref="MonoClock"/> — the clock <see cref="IScanDisplay.ShowAsync"/> stamps
/// presents with).</summary>
public sealed record CameraFrame(byte[] Gray, int Width, int Height, long ArrivalNs, long Generation);

/// <summary>A camera a scan captures through. The scan owns it for the scan's length and disposes it.</summary>
public interface IScanCamera : IDisposable
{
    /// <summary>Completes true once pictures flow, false (with <see cref="Problem"/>) when the camera cannot open.</summary>
    Task<bool> OpenAsync(CancellationToken ct);

    /// <summary>Why the camera is not giving pictures, in words ("" while it is).</summary>
    string Problem { get; }

    /// <summary>The next picture newer than <paramref name="afterGeneration"/>, or null when none arrived within
    /// <paramref name="timeout"/>.</summary>
    Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct);
}

/// <summary>A capture device of the model (<see cref="CameraRig"/>) through Media's <see cref="CaptureSource"/>:
/// the newest frame, converted to contiguous gray8 (luma, expanded to full range), stamped with its arrival.</summary>
public sealed class CaptureCamera : IScanCamera
{
    private readonly CaptureSource _source;

    public CaptureCamera(CaptureOptions options) => _source = new CaptureSource(options, "scan camera");

    /// <summary>The capture options a camera rig names: a device on this machine (the platform's capture format) or a
    /// network stream (FFmpeg probes the format). Null when the rig is not something a capture device can open.</summary>
    public static CaptureOptions? OptionsFor(CameraRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.Source.Length == 0)
            return null;
        var size = rig.Intrinsics.Width > 0 && rig.Intrinsics.Height > 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["video_size"] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rig.Intrinsics.Width}x{rig.Intrinsics.Height}"),
            }
            : null;
        return rig.SourceKind switch
        {
            CameraSourceKind.Device => new CaptureOptions { Device = rig.Source, DeviceOptions = size },
            CameraSourceKind.Stream or CameraSourceKind.Ndi => new CaptureOptions { Format = "", Device = rig.Source },
            _ => null,
        };
    }

    public string Problem => _source.Health.Ready ? "" : _source.Health.Status;

    public async Task<bool> OpenAsync(CancellationToken ct)
    {
        var opened = await _source.Opened.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return opened;
    }

    public async Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = MonoClock.Instance.NowNs + (long)timeout.TotalMilliseconds * 1_000_000;
        while (!ct.IsCancellationRequested)
        {
            using (var acquired = _source.Acquire(0))
            {
                if (acquired is MediaFrameLease lease && lease.Generation > afterGeneration)
                {
                    var gray = new byte[lease.Width * lease.Height];
                    if (!Gray8.TryCopy(lease, gray))
                        return null;
                    return new CameraFrame(gray, lease.Width, lease.Height, lease.ArrivalNs, lease.Generation);
                }
            }
            if (MonoClock.Instance.NowNs >= deadline)
                return null;
            // A frame every few milliseconds is what a camera gives; polling at 2 ms costs nothing beside a scan.
            await Task.Delay(2, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return null;
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>A decoded picture to the gray8 a structured-light decode reads: the luma plane of a YUV layout
/// (expanded from video range to full range), the BT.709 luma of an RGB one. Every row is copied without its
/// stride.</summary>
public static class Gray8
{
    /// <summary>Copies the lease's picture into <paramref name="destination"/> (width × height bytes); false for a
    /// layout it cannot read.</summary>
    public static unsafe bool TryCopy(VideoFrameLease lease, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var w = lease.Width;
        var h = lease.Height;
        if (destination.Length < w * h || lease.Planes.Count == 0)
            return false;
        var plane = lease.Planes[0];
        var limited = lease.Range == ColorRange.Limited;
        for (var y = 0; y < h; y++)
        {
            var row = new ReadOnlySpan<byte>((byte*)plane.Data + (long)y * plane.Stride, plane.Stride);
            var dst = destination.Slice(y * w, w);
            switch (lease.Layout)
            {
                case PixelLayout.Gray8:
                    row[..w].CopyTo(dst);
                    break;
                case PixelLayout.Nv12 or PixelLayout.Yuv420P or PixelLayout.Yuv422P or PixelLayout.Yuv444P:
                    for (var x = 0; x < w; x++)
                        dst[x] = Luma(row[x], limited);
                    break;
                case PixelLayout.P010:
                    // Ten significant bits in the high bits of a 16-bit sample: its high byte is the 8-bit level.
                    for (var x = 0; x < w; x++)
                        dst[x] = Luma(row[2 * x + 1], limited);
                    break;
                case PixelLayout.Yuv420P10:
                    // Ten significant low bits: shifted down by two.
                    for (var x = 0; x < w; x++)
                        dst[x] = Luma((byte)((row[2 * x] | (row[2 * x + 1] << 8)) >> 2), limited);
                    break;
                case PixelLayout.Rgba8:
                    for (var x = 0; x < w; x++)
                        dst[x] = Rgb(row[4 * x], row[4 * x + 1], row[4 * x + 2]);
                    break;
                case PixelLayout.Bgra8:
                    for (var x = 0; x < w; x++)
                        dst[x] = Rgb(row[4 * x + 2], row[4 * x + 1], row[4 * x]);
                    break;
                case PixelLayout.RgbaHalf:
                    for (var x = 0; x < w; x++)
                    {
                        var px = row.Slice(8 * x, 8);
                        var r = (float)BitConverter.ToHalf(px[..2]);
                        var g = (float)BitConverter.ToHalf(px.Slice(2, 2));
                        var b = (float)BitConverter.ToHalf(px.Slice(4, 2));
                        dst[x] = (byte)Math.Clamp(MathF.Round(255 * (0.2126f * r + 0.7152f * g + 0.0722f * b)), 0, 255);
                    }
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>Video range (16–235) stretched to 0–255; full range passed through.</summary>
    private static byte Luma(byte y, bool limited) =>
        limited ? (byte)Math.Clamp((y - 16) * 255 / 219, 0, 255) : y;

    private static byte Rgb(byte r, byte g, byte b) => (byte)((54 * r + 183 * g + 19 * b + 128) >> 8);
}
