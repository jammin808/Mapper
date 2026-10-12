using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Imaging;

/// <summary>How a picture is fitted into a model's square-ish input.</summary>
public enum FitMode
{
    /// <summary>Scaled to fill the input on both axes (aspect not kept) — SAM 2 and most depth models.</summary>
    Stretch,
    /// <summary>Scaled by one factor to fit, padded at the right and bottom — SAM 1.</summary>
    LetterboxTopLeft,
    /// <summary>Scaled by one factor to fit, padded equally on both sides.</summary>
    LetterboxCentre,
}

/// <summary>A model's input picture: its size, the fit, and the normalisation (value/255 − mean) / std per
/// channel, the ImageNet constants by default.</summary>
public sealed record ModelInputSpec(int Width, int Height, FitMode Fit, float[] Mean, float[] Std, float PadValue = 0, bool Bgr = false)
{
    public static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    public static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    public static ModelInputSpec ImageNet(int width, int height, FitMode fit) => new(width, height, fit, ImageNetMean, ImageNetStd);

    public ModelInputSpec WithSize(int width, int height) => this with { Width = width, Height = height };
}

/// <summary>The affine map between picture pixels and model-input pixels (both continuous, (0, 0) the top-left
/// corner): model = picture · scale + offset per axis. <see cref="Content"/> is where the picture lies in the
/// model input (all of it for a stretch; less for a letterbox).</summary>
public readonly record struct ImageToModel(double ScaleX, double ScaleY, double OffsetX, double OffsetY, int ModelWidth, int ModelHeight, int ImageWidth, int ImageHeight)
{
    public Vec2d ToModel(Vec2d image) => new(image.X * ScaleX + OffsetX, image.Y * ScaleY + OffsetY);

    public Vec2d ToImage(Vec2d model) => new((model.X - OffsetX) / ScaleX, (model.Y - OffsetY) / ScaleY);

    public RectD Content => new(OffsetX, OffsetY, ImageWidth * ScaleX, ImageHeight * ScaleY);

    public static ImageToModel For(int imageWidth, int imageHeight, int modelWidth, int modelHeight, FitMode fit)
    {
        if (fit == FitMode.Stretch)
            return new ImageToModel(modelWidth / (double)imageWidth, modelHeight / (double)imageHeight, 0, 0, modelWidth, modelHeight, imageWidth, imageHeight);
        var s = Math.Min(modelWidth / (double)imageWidth, modelHeight / (double)imageHeight);
        var w = imageWidth * s;
        var h = imageHeight * s;
        var (ox, oy) = fit == FitMode.LetterboxCentre ? ((modelWidth - w) / 2, (modelHeight - h) / 2) : (0.0, 0.0);
        return new ImageToModel(s, s, ox, oy, modelWidth, modelHeight, imageWidth, imageHeight);
    }
}

/// <summary>Fills a model's NCHW input from a picture: fit, resample, normalise, pad.</summary>
public static class ModelInput
{
    /// <summary>Writes the 3 × H × W planes of <paramref name="spec"/> into <paramref name="chw"/> and returns the
    /// map from picture to model pixels. The content rectangle is snapped to whole model pixels so no tensor
    /// pixel is half picture, half padding.</summary>
    public static ImageToModel Fill(ImageRgb8 image, ModelInputSpec spec, Span<float> chw)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(spec);
        var plane = spec.Width * spec.Height;
        if (chw.Length < plane * 3)
            throw new ArgumentException("The tensor is smaller than the input.", nameof(chw));
        Span<float> gain = stackalloc float[3];
        Span<float> bias = stackalloc float[3];
        for (var c = 0; c < 3; c++)
        {
            gain[c] = 1f / (255f * spec.Std[c]);
            bias[c] = -spec.Mean[c] / spec.Std[c];
        }
        var map = ImageToModel.For(image.Width, image.Height, spec.Width, spec.Height, spec.Fit);
        var x0 = (int)Math.Round(map.OffsetX);
        var y0 = (int)Math.Round(map.OffsetY);
        var w = Math.Clamp((int)Math.Round(image.Width * map.ScaleX), 1, spec.Width - x0);
        var h = Math.Clamp((int)Math.Round(image.Height * map.ScaleY), 1, spec.Height - y0);
        if (w != spec.Width || h != spec.Height)
        {
            for (var c = 0; c < 3; c++)
                chw.Slice(c * plane, plane).Fill(spec.PadValue);
        }
        Resampler.RgbToPlanar(image, new RectD(0, 0, image.Width, image.Height), chw, spec.Width, spec.Height, x0, y0, w, h, gain, bias, spec.Bgr);
        // The snapped rectangle is the exact map the tensor holds.
        return new ImageToModel(w / (double)image.Width, h / (double)image.Height, x0, y0, spec.Width, spec.Height, image.Width, image.Height);
    }
}
