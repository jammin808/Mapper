using System.Globalization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>What the decoder concluded about one camera pixel.</summary>
[Flags]
public enum DecodeStatus : byte
{
    None = 0,

    /// <summary>The pixel decoded: its projector coordinates are trustworthy to its confidence.</summary>
    Valid = 1,

    /// <summary>White beat black by the contrast asked for: the projector reaches this pixel.</summary>
    Lit = 2,

    /// <summary>The white frame clipped here; the pixel is not used.</summary>
    Saturated = 4,

    /// <summary>A suspect the jump cleanup looked at: more code bits were too close to call than a stripe edge
    /// explains, the Gray code and the phase disagreed by more than a Gray cell, or (XOR-coded) the pixel lies
    /// on an edge of the XOR base.</summary>
    Uncertain = 8,

    /// <summary>The sinusoid's modulation was too small to place the pixel (blur, scatter, a dark surface).</summary>
    LowModulation = 16,

    /// <summary>The code named a position outside the scanned region.</summary>
    OutOfRange = 32,

    /// <summary>The jump cleanup moved the pixel by whole phase periods to agree with its neighbours.</summary>
    Corrected = 64,

    /// <summary>A suspect pixel that disagreed with its neighbours and could not be reconciled.</summary>
    Rejected = 128,
}

/// <summary>A decode's totals. Fractions are of the lit pixels; bits are the Gray decisions taken on them;
/// <see cref="NoiseSigma"/> is the camera noise the decode allowed for (levels; measured from the sinusoids
/// when the caller did not give it, 0 when neither).</summary>
public sealed record DecodeStats(
    int Pixels,
    int Lit,
    int Valid,
    int Saturated,
    int LowModulation,
    int OutOfRange,
    int Rejected,
    int Corrected,
    long UncertainBits,
    long DecidedBits,
    double MeanConfidence,
    double NoiseSigma)
{
    public double LitFraction => Pixels > 0 ? (double)Lit / Pixels : 0;
    public double DecodedFraction => Lit > 0 ? (double)Valid / Lit : 0;
    public double SaturationShare => Lit > 0 ? (double)Saturated / Lit : 0;
    public double UncertainBitShare => DecidedBits > 0 ? (double)UncertainBits / DecidedBits : 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{DecodedFraction:P1} of {Lit} lit pixels decoded, mean confidence {MeanConfidence:0.00}, {UncertainBitShare:P2} bits uncertain, {SaturationShare:P1} saturated, noise {NoiseSigma:0.0}");
}

/// <summary>A camera pixel and the projector point that lights it — a dense correspondence for calibration.
/// Both are continuous pixel coordinates with (0, 0) the raster's top-left corner, so a pixel's centre is at
/// integer + 0.5.</summary>
public readonly record struct CameraProjectorPair(Vec2d Camera, Vec2d Projector, float Confidence);

/// <summary>
/// The decode: per camera pixel (row-major, <see cref="Width"/> × <see cref="Height"/>), the projector point
/// that lights it as continuous projector pixel coordinates — the same convention as the camera model: (0, 0)
/// is the raster's top-left corner and a column's centre is at column + 0.5. NaN where the pixel did not
/// decode (or the axis was not scanned). Confidence is 0 where invalid and 1 for a clean, bright, sharp
/// decode. <see cref="Contrast"/> is white − black in linear camera units (0–255): a free albedo image.
/// </summary>
public sealed class CorrespondenceMap
{
    public CorrespondenceMap(int width, int height, int projectorWidth, int projectorHeight, PixelRect region, ScanAxes axes, bool subPixel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        ProjectorWidth = projectorWidth;
        ProjectorHeight = projectorHeight;
        Region = region;
        Axes = axes;
        SubPixel = subPixel;
        var n = width * height;
        ProjectorX = new float[n];
        ProjectorY = new float[n];
        Confidence = new float[n];
        Contrast = new float[n];
        Flags = new DecodeStatus[n];
        Array.Fill(ProjectorX, float.NaN);
        Array.Fill(ProjectorY, float.NaN);
        Stats = new DecodeStats(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    public int Width { get; }
    public int Height { get; }
    public int ProjectorWidth { get; }
    public int ProjectorHeight { get; }

    /// <summary>The projector rectangle that was scanned.</summary>
    public PixelRect Region { get; }

    public ScanAxes Axes { get; }

    /// <summary>True when the coordinates carry a fraction of a pixel (the plan showed sinusoids).</summary>
    public bool SubPixel { get; }

    public float[] ProjectorX { get; }
    public float[] ProjectorY { get; }
    public float[] Confidence { get; }
    public float[] Contrast { get; }
    public DecodeStatus[] Flags { get; }

    /// <summary>The camera's white frame as captured (gray8), when the decode was given bytes.</summary>
    public byte[]? White { get; init; }

    /// <summary>The camera's white frame in colour (packed 0xRRGGBB), when the caller had one.</summary>
    public int[]? WhiteColour { get; init; }

    public DecodeStats Stats { get; internal set; }

    public bool IsValid(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && (Flags[y * Width + x] & DecodeStatus.Valid) != 0;

    /// <summary>The projector point camera pixel (x, y) decoded to; false when it did not decode.</summary>
    public bool TryGet(int x, int y, out Vec2d projector)
    {
        if (!IsValid(x, y))
        {
            projector = default;
            return false;
        }
        var k = y * Width + x;
        projector = new Vec2d(ProjectorX[k], ProjectorY[k]);
        return true;
    }

    /// <summary>The valid pairs at or above a confidence, every <paramref name="stride"/>-th pixel each way —
    /// what self-calibration and pose solving take.</summary>
    public List<CameraProjectorPair> Pairs(float minConfidence = 0, int stride = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);
        var pairs = new List<CameraProjectorPair>();
        for (var y = 0; y < Height; y += stride)
        {
            for (var x = 0; x < Width; x += stride)
            {
                var k = y * Width + x;
                if ((Flags[k] & DecodeStatus.Valid) == 0 || Confidence[k] < minConfidence)
                    continue;
                pairs.Add(new CameraProjectorPair(new Vec2d(x + 0.5, y + 0.5), new Vec2d(ProjectorX[k], ProjectorY[k]), Confidence[k]));
            }
        }
        return pairs;
    }

    /// <summary>Recounts the totals from the per-pixel results (the decoder's bit counts are carried over).</summary>
    internal void Recount(long uncertainBits, long decidedBits, double noiseSigma)
    {
        int lit = 0, valid = 0, saturated = 0, lowModulation = 0, outOfRange = 0, rejected = 0, corrected = 0;
        double confidence = 0;
        for (var k = 0; k < Flags.Length; k++)
        {
            var f = Flags[k];
            if ((f & DecodeStatus.Lit) == 0)
                continue;
            lit++;
            if ((f & DecodeStatus.Saturated) != 0)
                saturated++;
            if ((f & DecodeStatus.LowModulation) != 0)
                lowModulation++;
            if ((f & DecodeStatus.OutOfRange) != 0)
                outOfRange++;
            if ((f & DecodeStatus.Rejected) != 0)
                rejected++;
            if ((f & DecodeStatus.Corrected) != 0)
                corrected++;
            if ((f & DecodeStatus.Valid) != 0)
            {
                valid++;
                confidence += Confidence[k];
            }
        }
        Stats = new DecodeStats(Flags.Length, lit, valid, saturated, lowModulation, outOfRange, rejected, corrected,
            uncertainBits, decidedBits, valid > 0 ? confidence / valid : 0, noiseSigma);
    }
}
