using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>How a projector pixel of a <see cref="ProjectorView"/> got its camera point.</summary>
public enum ProjectorCoverage : byte
{
    /// <summary>No camera pixel saw this projector pixel's light.</summary>
    None,

    /// <summary>The robust mean of the camera pixels that decoded to it.</summary>
    Measured,

    /// <summary>Inside a triangle of neighbouring decoded camera pixels (the camera is coarser than the projector here).</summary>
    Interpolated,

    /// <summary>A small hole filled from its neighbours.</summary>
    Filled,
}

/// <summary>Options for the projector-space view.</summary>
public sealed record ProjectorViewOptions
{
    public static readonly ProjectorViewOptions Default = new();

    /// <summary>Camera pixels below this confidence are not used.</summary>
    public float MinConfidence { get; init; } = 0.05f;

    /// <summary>A camera sample further than this many standard deviations from its projector pixel's mean
    /// camera point is left out of the mean (decoding noise at edges, a stray reflection).</summary>
    public double OutlierSigma { get; init; } = 2.5;

    /// <summary>The smallest outlier radius in camera pixels, so a tight cluster does not reject its own members.</summary>
    public double MinOutlierRadius { get; init; } = 1.0;

    /// <summary>The longest projector-space edge (pixels) of a camera triangle that is interpolated across; null
    /// takes four times the scan's typical camera-neighbour step. Longer edges span a depth jump.</summary>
    public double? MaxTriangleEdge { get; init; }

    /// <summary>Hole-filling passes (each grows the filled area by a pixel).</summary>
    public int HoleFillPasses { get; init; } = 2;

    /// <summary>A hole pixel is filled when at least this many of its eight neighbours are covered.</summary>
    public int HoleFillMinNeighbours { get; init; } = 4;
}

/// <summary>
/// The scan seen from the projector: for every projector pixel of the scanned region, the camera point that
/// sees it (continuous camera pixel coordinates, NaN where none) and the "smart scan" — the camera's white
/// frame resampled into projector space, the picture of what the projector lights. Masks and effects drawn
/// on it are already in output pixels; no calibration is needed for a single projector on an object.
/// </summary>
public sealed class ProjectorView
{
    private ProjectorView(int width, int height, PixelRect region)
    {
        Width = width;
        Height = height;
        Region = region;
        var n = width * height;
        CameraX = new float[n];
        CameraY = new float[n];
        Confidence = new float[n];
        Coverage = new ProjectorCoverage[n];
        SmartScan = new byte[n];
        Array.Fill(CameraX, float.NaN);
        Array.Fill(CameraY, float.NaN);
    }

    public int Width { get; }
    public int Height { get; }
    public PixelRect Region { get; }
    public float[] CameraX { get; }
    public float[] CameraY { get; }
    public float[] Confidence { get; }
    public ProjectorCoverage[] Coverage { get; }

    /// <summary>The camera's white frame in projector space, gray8, black where uncovered.</summary>
    public byte[] SmartScan { get; }

    /// <summary>The same in colour (0xRRGGBB), when the decode carried a colour white frame.</summary>
    public int[]? SmartScanColour { get; private set; }

    public int CoveredPixels { get; private set; }

    /// <summary>The covered share of the scanned region.</summary>
    public double CoveredFraction => Region.IsEmpty ? 0 : (double)CoveredPixels / ((long)Region.Width * Region.Height);

    public bool TryGetCamera(int x, int y, out Vec2d camera)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || Coverage[y * Width + x] == ProjectorCoverage.None)
        {
            camera = default;
            return false;
        }
        camera = new Vec2d(CameraX[y * Width + x], CameraY[y * Width + x]);
        return true;
    }

    /// <summary>Builds the projector-space view of a decode that coded both axes.</summary>
    public static ProjectorView Build(CorrespondenceMap map, ProjectorViewOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Axes != ScanAxes.Both)
            throw new ArgumentException("A projector-space view needs both axes decoded.", nameof(map));
        options ??= ProjectorViewOptions.Default;
        var view = new ProjectorView(map.ProjectorWidth, map.ProjectorHeight, map.Region);
        view.Splat(map, options);
        view.Interpolate(map, options);
        view.FillHoles(options);
        view.Resample(map);
        var covered = 0;
        foreach (var c in view.Coverage)
        {
            if (c != ProjectorCoverage.None)
                covered++;
        }
        view.CoveredPixels = covered;
        return view;
    }

    /// <summary>Each decoded camera pixel adds its centre to the four projector pixels around its projector
    /// point, bilinearly weighted (and by its confidence), so a projector pixel's mean camera point is the
    /// camera point of the pixel's centre to first order. Three passes: mean, spread, mean of the samples
    /// within the spread.</summary>
    private void Splat(CorrespondenceMap map, ProjectorViewOptions options)
    {
        var n = Width * Height;
        var sumW = new double[n];
        var sumBilinear = new double[n];
        var sumX = new double[n];
        var sumY = new double[n];
        var spread = new double[n];
        var meanX = new double[n];
        var meanY = new double[n];
        Span<int> targets = stackalloc int[4];
        Span<double> weights = stackalloc double[4];

        // Pass 1: the plain weighted mean.
        ForEachSample(map, options, targets, weights, (t, w, cx, cy, _) =>
        {
            sumW[t] += w;
            sumX[t] += w * cx;
            sumY[t] += w * cy;
        });
        for (var t = 0; t < n; t++)
        {
            if (sumW[t] > 0)
            {
                meanX[t] = sumX[t] / sumW[t];
                meanY[t] = sumY[t] / sumW[t];
            }
        }

        // Pass 2: the weighted spread about it.
        ForEachSample(map, options, targets, weights, (t, w, cx, cy, _) =>
        {
            var dx = cx - meanX[t];
            var dy = cy - meanY[t];
            spread[t] += w * (dx * dx + dy * dy);
        });
        for (var t = 0; t < n; t++)
        {
            if (sumW[t] > 0)
            {
                var radius = Math.Max(options.MinOutlierRadius, options.OutlierSigma * Math.Sqrt(spread[t] / sumW[t]));
                spread[t] = radius * radius;
            }
        }

        // Pass 3: the mean of the samples within the spread.
        Array.Clear(sumW);
        Array.Clear(sumX);
        Array.Clear(sumY);
        ForEachSample(map, options, targets, weights, (t, w, cx, cy, bilinear) =>
        {
            var dx = cx - meanX[t];
            var dy = cy - meanY[t];
            if (dx * dx + dy * dy > spread[t])
                return;
            sumW[t] += w;
            sumBilinear[t] += bilinear;
            sumX[t] += w * cx;
            sumY[t] += w * cy;
        });
        for (var t = 0; t < n; t++)
        {
            // A pixel needs a quarter of a sample's worth of support, or its mean leans on one side's samples.
            if (sumBilinear[t] < 0.25 || sumW[t] <= 0)
                continue;
            CameraX[t] = (float)(sumX[t] / sumW[t]);
            CameraY[t] = (float)(sumY[t] / sumW[t]);
            Confidence[t] = (float)Math.Min(1, sumW[t] / sumBilinear[t]);
            Coverage[t] = ProjectorCoverage.Measured;
        }
    }

    private delegate void SampleAction(int target, double weight, double cameraX, double cameraY, double bilinear);

    private void ForEachSample(CorrespondenceMap map, ProjectorViewOptions options, Span<int> targets, Span<double> weights, SampleAction action)
    {
        var w = map.Width;
        for (var k = 0; k < map.Flags.Length; k++)
        {
            if ((map.Flags[k] & DecodeStatus.Valid) == 0 || map.Confidence[k] < options.MinConfidence)
                continue;
            var fx = map.ProjectorX[k] - 0.5;
            var fy = map.ProjectorY[k] - 0.5;
            var x0 = (int)Math.Floor(fx);
            var y0 = (int)Math.Floor(fy);
            var tx = fx - x0;
            var ty = fy - y0;
            var count = 0;
            AddTarget(x0, y0, (1 - tx) * (1 - ty), targets, weights, ref count);
            AddTarget(x0 + 1, y0, tx * (1 - ty), targets, weights, ref count);
            AddTarget(x0, y0 + 1, (1 - tx) * ty, targets, weights, ref count);
            AddTarget(x0 + 1, y0 + 1, tx * ty, targets, weights, ref count);
            var cx = k % w + 0.5;
            var cy = k / w + 0.5;
            var confidence = map.Confidence[k];
            for (var i = 0; i < count; i++)
                action(targets[i], weights[i] * confidence, cx, cy, weights[i]);
        }
    }

    private void AddTarget(int x, int y, double weight, Span<int> targets, Span<double> weights, ref int count)
    {
        if (weight <= 1e-9 || !Region.Contains(x, y))
            return;
        targets[count] = y * Width + x;
        weights[count] = weight;
        count++;
    }

    /// <summary>Projector pixels the splat missed (the camera is coarser than the projector) take their camera
    /// point from the triangle of neighbouring decoded camera pixels that covers them, barycentrically;
    /// triangles stretched across a depth jump are skipped.</summary>
    private void Interpolate(CorrespondenceMap map, ProjectorViewOptions options)
    {
        var w = map.Width;
        var h = map.Height;
        var maxEdge = options.MaxTriangleEdge ?? Math.Max(3.0, 4 * TypicalStep(map, options));
        var maxEdge2 = maxEdge * maxEdge;
        for (var y = 0; y + 1 < h; y++)
        {
            for (var x = 0; x + 1 < w; x++)
            {
                var k00 = y * w + x;
                var k10 = k00 + 1;
                var k01 = k00 + w;
                var k11 = k01 + 1;
                if (Usable(map, k00, options) && Usable(map, k10, options) && Usable(map, k01, options))
                    Rasterise(map, k00, k10, k01, maxEdge2);
                if (Usable(map, k10, options) && Usable(map, k11, options) && Usable(map, k01, options))
                    Rasterise(map, k10, k11, k01, maxEdge2);
            }
        }
    }

    private static bool Usable(CorrespondenceMap map, int k, ProjectorViewOptions options) =>
        (map.Flags[k] & DecodeStatus.Valid) != 0 && map.Confidence[k] >= options.MinConfidence;

    /// <summary>The median projector-space distance between horizontally neighbouring decoded camera pixels.</summary>
    private static double TypicalStep(CorrespondenceMap map, ProjectorViewOptions options)
    {
        var steps = new List<float>();
        var w = map.Width;
        for (var y = 0; y < map.Height; y += 2)
        {
            for (var x = 0; x + 1 < w; x += 2)
            {
                var k = y * w + x;
                if (!Usable(map, k, options) || !Usable(map, k + 1, options))
                    continue;
                var dx = map.ProjectorX[k + 1] - map.ProjectorX[k];
                var dy = map.ProjectorY[k + 1] - map.ProjectorY[k];
                steps.Add(MathF.Sqrt(dx * dx + dy * dy));
            }
        }
        if (steps.Count == 0)
            return 1;
        steps.Sort();
        return steps[steps.Count / 2];
    }

    private void Rasterise(CorrespondenceMap map, int ka, int kb, int kc, double maxEdge2)
    {
        var w = map.Width;
        var a = new Vec2d(map.ProjectorX[ka], map.ProjectorY[ka]);
        var b = new Vec2d(map.ProjectorX[kb], map.ProjectorY[kb]);
        var c = new Vec2d(map.ProjectorX[kc], map.ProjectorY[kc]);
        if ((b - a).LengthSquared > maxEdge2 || (c - b).LengthSquared > maxEdge2 || (a - c).LengthSquared > maxEdge2)
            return;
        var area = Vec2d.Cross(b - a, c - a);
        if (Math.Abs(area) < 1e-9)
            return;
        var x0 = Math.Max(Region.X, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) - 0.5));
        var x1 = Math.Min(Region.Right - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X)) - 0.5));
        var y0 = Math.Max(Region.Y, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - 0.5));
        var y1 = Math.Min(Region.Bottom - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - 0.5));
        var confidence = Math.Min(map.Confidence[ka], Math.Min(map.Confidence[kb], map.Confidence[kc])) * 0.9f;
        for (var py = y0; py <= y1; py++)
        {
            for (var px = x0; px <= x1; px++)
            {
                var t = py * Width + px;
                if (Coverage[t] != ProjectorCoverage.None)
                    continue;
                var p = new Vec2d(px + 0.5, py + 0.5);
                var wa = Vec2d.Cross(b - p, c - p) / area;
                var wb = Vec2d.Cross(c - p, a - p) / area;
                var wc = 1 - wa - wb;
                const double eps = -1e-6;
                if (wa < eps || wb < eps || wc < eps)
                    continue;
                CameraX[t] = (float)(wa * (ka % w + 0.5) + wb * (kb % w + 0.5) + wc * (kc % w + 0.5));
                CameraY[t] = (float)(wa * (ka / w + 0.5) + wb * (kb / w + 0.5) + wc * (kc / w + 0.5));
                Confidence[t] = confidence;
                Coverage[t] = ProjectorCoverage.Interpolated;
            }
        }
    }

    /// <summary>Uncovered pixels with enough covered neighbours take their mean, pass by pass.</summary>
    private void FillHoles(ProjectorViewOptions options)
    {
        for (var pass = 0; pass < options.HoleFillPasses; pass++)
        {
            var before = (ProjectorCoverage[])Coverage.Clone();
            var filled = 0;
            for (var y = Region.Y; y < Region.Bottom; y++)
            {
                for (var x = Region.X; x < Region.Right; x++)
                {
                    var t = y * Width + x;
                    if (before[t] != ProjectorCoverage.None)
                        continue;
                    int count = 0;
                    double sx = 0, sy = 0, sc = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var xx = x + dx;
                            var yy = y + dy;
                            if ((dx == 0 && dy == 0) || !Region.Contains(xx, yy))
                                continue;
                            var u = yy * Width + xx;
                            if (before[u] == ProjectorCoverage.None)
                                continue;
                            count++;
                            sx += CameraX[u];
                            sy += CameraY[u];
                            sc += Confidence[u];
                        }
                    }
                    if (count < options.HoleFillMinNeighbours)
                        continue;
                    CameraX[t] = (float)(sx / count);
                    CameraY[t] = (float)(sy / count);
                    Confidence[t] = (float)(0.5 * sc / count);
                    Coverage[t] = ProjectorCoverage.Filled;
                    filled++;
                }
            }
            if (filled == 0)
                break;
        }
    }

    /// <summary>Samples the camera's white frame (or, without one, the decode's contrast) at every covered
    /// projector pixel's camera point, bilinearly.</summary>
    private void Resample(CorrespondenceMap map)
    {
        var colour = map.WhiteColour;
        int[]? colourOut = colour is null ? null : new int[Width * Height];
        for (var t = 0; t < Coverage.Length; t++)
        {
            if (Coverage[t] == ProjectorCoverage.None)
                continue;
            var fx = CameraX[t] - 0.5;
            var fy = CameraY[t] - 0.5;
            SmartScan[t] = map.White is { } white
                ? (byte)Math.Clamp(Math.Round(Bilinear(map.Width, map.Height, fx, fy, white)), 0, 255)
                : (byte)Math.Clamp(Math.Round(Bilinear(map.Width, map.Height, fx, fy, map.Contrast)), 0, 255);
            if (colour is not null && colourOut is not null)
            {
                var r = Bilinear(map.Width, map.Height, fx, fy, colour, 16);
                var g = Bilinear(map.Width, map.Height, fx, fy, colour, 8);
                var b = Bilinear(map.Width, map.Height, fx, fy, colour, 0);
                colourOut[t] = (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
            }
        }
        SmartScanColour = colourOut;
    }

    private static int Channel(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);

    private static double Bilinear(int w, int h, double fx, double fy, byte[] image)
    {
        var s = BilinearSite.At(w, h, fx, fy);
        return s.Blend(image[s.K00], image[s.K10], image[s.K01], image[s.K11]);
    }

    private static double Bilinear(int w, int h, double fx, double fy, int[] image, int shift)
    {
        var s = BilinearSite.At(w, h, fx, fy);
        return s.Blend((image[s.K00] >> shift) & 0xFF, (image[s.K10] >> shift) & 0xFF, (image[s.K01] >> shift) & 0xFF, (image[s.K11] >> shift) & 0xFF);
    }

    private static double Bilinear(int w, int h, double fx, double fy, float[] image)
    {
        var s = BilinearSite.At(w, h, fx, fy);
        return s.Blend(image[s.K00], image[s.K10], image[s.K01], image[s.K11]);
    }

    /// <summary>The four samples around a continuous position (sample centres at integers, edges clamped) and
    /// the weights between them.</summary>
    private readonly record struct BilinearSite(int K00, int K10, int K01, int K11, double Tx, double Ty)
    {
        public static BilinearSite At(int w, int h, double fx, double fy)
        {
            fx = Math.Clamp(fx, 0, w - 1);
            fy = Math.Clamp(fy, 0, h - 1);
            var x0 = Math.Min((int)fx, w - 1);
            var y0 = Math.Min((int)fy, h - 1);
            var x1 = Math.Min(x0 + 1, w - 1);
            var y1 = Math.Min(y0 + 1, h - 1);
            return new BilinearSite(y0 * w + x0, y0 * w + x1, y1 * w + x0, y1 * w + x1, fx - x0, fy - y0);
        }

        public double Blend(double v00, double v10, double v01, double v11) =>
            (v00 * (1 - Tx) + v10 * Tx) * (1 - Ty) + (v01 * (1 - Tx) + v11 * Tx) * Ty;
    }
}
