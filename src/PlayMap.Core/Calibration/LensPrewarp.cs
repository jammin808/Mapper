using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>A distortion pre-warp lookup: for each texel of a Width × Height map laid over the output raster,
/// the normalised source coordinate (u, v in [0, 1] of the undistorted render) the output pixel there must
/// show. Interleaved RG floats, row-major, ready for an RG32F texture sampled bilinearly with a black border.
/// A coordinate outside [0, 1] is real (a barrel lens's edge pixels look past the render) and samples black;
/// a texel with no source at all (beyond the fold of a strongly distorting lens) holds <see cref="NoSource"/>,
/// so far outside that bilinear filtering beside it cannot smear render pixels across the fold.</summary>
public sealed record PrewarpMap(int Width, int Height, int OutputWidth, int OutputHeight, float[] Uv)
{
    public const float NoSource = -1e6f;

    /// <summary>The source pixel (in output-raster pixels) texel (x, y) samples, or null where there is none.</summary>
    public Vec2d? SourcePixel(int x, int y)
    {
        var i = (y * Width + x) * 2;
        var u = Uv[i];
        var v = Uv[i + 1];
        return u <= NoSource || v <= NoSource ? null : new Vec2d(u * OutputWidth, v * OutputHeight);
    }

    /// <summary>The output-raster position at the centre of texel (x, y).</summary>
    public Vec2d TexelCentre(int x, int y) =>
        new((x + 0.5) * OutputWidth / Width, (y + 0.5) * OutputHeight / Height);
}

/// <summary>Projector lens pre-distortion: render the stage undistorted (an ideal pinhole with the same
/// intrinsics), then let each output pixel fetch from where its light will actually go. A panel pixel p
/// leaves the lens along n = D⁻¹(K⁻¹·p) (D the Brown–Conrady distortion), and the ideal render holds that
/// direction's content at K·n — so the output at p samples K·D⁻¹(K⁻¹·p).</summary>
public static class LensPrewarp
{
    /// <summary>The map at the given resolution (default: the projector's raster; a coarser map is
    /// upsampled bilinearly by the GPU with no visible loss for real lenses).</summary>
    public static PrewarpMap Build(Intrinsics k, LensDistortion lens, int width = 0, int height = 0)
    {
        ArgumentNullException.ThrowIfNull(k);
        ArgumentNullException.ThrowIfNull(lens);
        var w = width > 0 ? width : k.Width;
        var h = height > 0 ? height : k.Height;
        if (w <= 0 || h <= 0 || k.Width <= 0 || k.Height <= 0)
            throw new ArgumentException("The raster has no size.");
        var uv = new float[w * h * 2];
        var map = new PrewarpMap(w, h, k.Width, k.Height, uv);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 2;
                if (SourceFor(map.TexelCentre(x, y), k, lens) is { } s)
                {
                    uv[i] = (float)(s.X / k.Width);
                    uv[i + 1] = (float)(s.Y / k.Height);
                }
                else
                {
                    uv[i] = PrewarpMap.NoSource;
                    uv[i + 1] = PrewarpMap.NoSource;
                }
            }
        });
        return map;
    }

    /// <summary>Where in the undistorted render the output pixel must sample: K·D⁻¹(K⁻¹·p), or null where the
    /// distortion cannot be inverted (far outside a strongly distorting lens's range).</summary>
    public static Vec2d? SourceFor(Vec2d outputPixel, Intrinsics k, LensDistortion lens)
    {
        ArgumentNullException.ThrowIfNull(k);
        ArgumentNullException.ThrowIfNull(lens);
        if (lens.IsNone)
            return outputPixel;
        var d = new Vec2d((outputPixel.X - k.Cx) / k.Fx, (outputPixel.Y - k.Cy) / k.Fy);
        if (!SolverMath.TryUndistort(d, lens, out var n))
            return null;
        // The inverse must also be the branch the lens uses: the distortion grows monotonically out to n.
        if (!Monotonic(n, lens))
            return null;
        return new Vec2d(k.Fx * n.X + k.Cx, k.Fy * n.Y + k.Cy);
    }

    /// <summary>True when the radial distortion is still increasing at n (beyond its fold the inverse is a
    /// different, unphysical branch).</summary>
    private static bool Monotonic(Vec2d n, LensDistortion lens)
    {
        var (a, b, c, d) = SolverMath.DistortJacobian(n, lens);
        return a * d - b * c > 0 && double.IsFinite(CameraModel.Distort(n, lens).X);
    }
}
