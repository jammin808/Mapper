using PlayMap.Core.Media;

namespace PlayMap.Render.Video;

/// <summary>The numbers the YUV shader needs for one picture format: how a normalised sample becomes a code
/// value, the range's offsets and scales at the format's bit depth, the matrix's Kr and Kb, and the transfer.
/// Pure, so the shader's arithmetic has a CPU twin the tests hold it to.</summary>
public readonly record struct YuvConstants(
    float CodeScale,
    float YOffset,
    float YScale,
    float COffset,
    float CScale,
    float Kr,
    float Kb,
    int Transfer,
    bool Wide,
    int Planes)
{
    /// <summary>The constants for a layout, matrix, range and transfer.</summary>
    public static YuvConstants For(PixelLayout layout, ColorMatrix matrix, ColorRange range, TransferFunction transfer)
    {
        // A sample comes back from the GPU normalised to its container (8 or 16 bits); the code value is that
        // times the container's maximum, divided by the shift when the significant bits sit high (P010).
        var (bits, codeScale) = layout switch
        {
            PixelLayout.P010 => (10, 65535f / 64f),
            PixelLayout.Yuv420P10 => (10, 65535f),
            _ => (8, 255f),
        };
        var step = (float)(1 << (bits - 8));
        float yOff, yScale, cOff, cScale;
        if (range == ColorRange.Limited)
        {
            yOff = 16 * step;
            yScale = 1f / (219 * step);
            cOff = 128 * step;
            cScale = 1f / (224 * step);
        }
        else
        {
            var max = (float)((1 << bits) - 1);
            yOff = 0;
            yScale = 1f / max;
            cOff = 1 << (bits - 1);
            cScale = 1f / max;
        }
        var (kr, kb) = matrix switch
        {
            ColorMatrix.Bt601 => (0.299f, 0.114f),
            ColorMatrix.Bt2020 => (0.2627f, 0.0593f),
            _ => (0.2126f, 0.0722f),
        };
        var t = transfer switch
        {
            TransferFunction.Pq => 1,
            TransferFunction.Hlg => 2,
            TransferFunction.Linear => 3,
            _ => 0,
        };
        var planes = layout is PixelLayout.Nv12 or PixelLayout.P010 ? 2 : 3;
        return new YuvConstants(codeScale, yOff, yScale, cOff, cScale, kr, kb, t, matrix == ColorMatrix.Bt2020, planes);
    }

    /// <summary>The SDR conversion of one sample's code values, as the shader does it (no transfer).</summary>
    public (float R, float G, float B) ToRgb(float yCode, float cbCode, float crCode)
    {
        var y = (yCode - YOffset) * YScale;
        var cb = (cbCode - COffset) * CScale;
        var cr = (crCode - COffset) * CScale;
        var kg = 1 - Kr - Kb;
        var r = y + 2 * (1 - Kr) * cr;
        var b = y + 2 * (1 - Kb) * cb;
        var g = (y - Kr * r - Kb * b) / kg;
        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }

    /// <summary>The code values of an RGB colour (0..1) in this format — the encoder's side, for tests and
    /// for generating reference frames.</summary>
    public (float Y, float Cb, float Cr) FromRgb(float r, float g, float b)
    {
        var kg = 1 - Kr - Kb;
        var y = Kr * r + kg * g + Kb * b;
        var cb = (b - y) / (2 * (1 - Kb));
        var cr = (r - y) / (2 * (1 - Kr));
        return (y / YScale + YOffset, cb / CScale + COffset, cr / CScale + COffset);
    }
}
