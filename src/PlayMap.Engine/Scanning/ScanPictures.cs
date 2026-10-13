using PlayMap.Core.Scan;

namespace PlayMap.Engine.Scanning;

/// <summary>The scan's pictures as preview frames: the smart scan in colour when the camera gave colour, and the
/// confidence as a heat ramp (red unsure, yellow, green sure; black where nothing was decoded).</summary>
public static class ScanPictures
{
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

    /// <summary>A colour picture (0xRRGGBB per pixel) as BGRA.</summary>
    public static byte[] FromColour(int[] rgb)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        var bgra = new byte[rgb.Length * 4];
        for (var k = 0; k < rgb.Length; k++)
        {
            bgra[4 * k] = (byte)(rgb[k] & 0xFF);
            bgra[4 * k + 1] = (byte)((rgb[k] >> 8) & 0xFF);
            bgra[4 * k + 2] = (byte)((rgb[k] >> 16) & 0xFF);
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
