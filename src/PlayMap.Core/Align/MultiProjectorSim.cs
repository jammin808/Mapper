using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Align;

public enum SimScreenShape
{
    /// <summary>The plane z = 0 (x right, y up, metres), with an optional smooth bulge toward the room.</summary>
    Flat,

    /// <summary>A concave vertical cylinder through the origin, its axis at z = radius.</summary>
    Cylinder,
}

/// <summary>The simulated screen. A flat screen may bulge by <see cref="BulgeM"/> at its centre, falling off as a cosine to nothing at
/// ±half its <see cref="BulgeWidthM"/> × <see cref="BulgeHeightM"/>: a screen that is not quite flat.</summary>
public sealed record SimScreen(SimScreenShape Shape, double BulgeM, double BulgeWidthM, double BulgeHeightM, double RadiusM)
{
    public static readonly SimScreen Flat = new(SimScreenShape.Flat, 0, 4, 2.25, 0);

    public static SimScreen Bulged(double bulgeM, double widthM = 4.2, double heightM = 2.6) => new(SimScreenShape.Flat, bulgeM, widthM, heightM, 0);

    public static SimScreen Curved(double radiusM) => new(SimScreenShape.Cylinder, 0, 0, 0, radiusM);
}

/// <summary>A simulated projector: optics, pose and light. A signal level v emits Black + (White − Black)·(v/255)^Gamma, the white
/// falling by <see cref="CornerFalloff"/> at the raster's corners (quadratically with the distance from its centre).</summary>
public sealed record SimProjector(Intrinsics Intrinsics, Pose Pose)
{
    public double Gamma { get; init; } = 2.2;

    /// <summary>Full white at the raster's centre, in the camera's linear units at exposure 1.</summary>
    public double White { get; init; } = 0.55;

    public double Black { get; init; } = 0.002;

    public double CornerFalloff { get; init; }

    /// <summary>The camera-linear RGB of each full primary (columns red, green, blue) per unit of <see cref="White"/>; null is grey
    /// light (the identity), the same in every channel as the grey picture.</summary>
    public Mat3d? Response { get; init; }

    /// <summary>A projector's colour response at a correlated colour temperature (4 000–25 000 K, the CIE daylight locus) with sRGB's
    /// primaries, its white's luminance one; <paramref name="redShift"/> moves the red primary's x (a lamp's own red).</summary>
    public static Mat3d ResponseAt(double kelvin, double redShift = 0)
    {
        var t = Math.Clamp(kelvin, 4000, 25000);
        var xw = t <= 7000
            ? -4.6070e9 / (t * t * t) + 2.9678e6 / (t * t) + 0.09911e3 / t + 0.244063
            : -2.0064e9 / (t * t * t) + 1.9018e6 / (t * t) + 0.24748e3 / t + 0.237040;
        var yw = -3.000 * xw * xw + 2.870 * xw - 0.275;
        static Vec3d Xyz(double x, double y) => new(x / y, 1, (1 - x - y) / y);
        var p = Mat3d.FromColumns(Xyz(0.64 + redShift, 0.33), Xyz(0.30, 0.60), Xyz(0.15, 0.06));
        var scale = p.Inverse() * Xyz(xw, yw);
        var primaries = Mat3d.FromColumns(Xyz(0.64 + redShift, 0.33) * scale.X, Xyz(0.30, 0.60) * scale.Y, Xyz(0.15, 0.06) * scale.Z);
        return Mat3d.FromColumns(ColourMatch.FromXyz(primaries.Column(0)), ColourMatch.FromXyz(primaries.Column(1)), ColourMatch.FromXyz(primaries.Column(2)));
    }
}

/// <summary>A simulated camera: optics with the full Brown lens (k1, k2, k3 and both tangential terms), pose, the room's light, noise
/// and an encoding gamma (light L is recorded as 255·L^(1/EncodeGamma)).</summary>
public sealed record SimCamera(Intrinsics Intrinsics, LensDistortion Lens, Pose Pose)
{
    public double NoiseSigma { get; init; } = 0.8;

    public double EncodeGamma { get; init; } = 2.2;

    public double Ambient { get; init; } = 0.004;

    public double Exposure { get; init; } = 1;

    public ulong Seed { get; init; } = 1;

    /// <summary>The camera sets its own exposure for each picture (a webcam left on automatic): the picture's mean light is brought to a
    /// fixed share of full scale, whatever the projectors show.</summary>
    public bool AutoExposure { get; init; }
}

/// <summary>How a ready-made layout is built: the projectors' rasters and light, their overlap and throw, how far each is turned off
/// square, the camera's raster, lens and position, the screen.</summary>
public sealed record SimLayout
{
    public int ProjectorWidth { get; init; } = 320;
    public int ProjectorHeight { get; init; } = 180;
    public int CameraWidth { get; init; } = 800;
    public int CameraHeight { get; init; } = 450;
    public double CameraFovDegrees { get; init; } = 62;

    /// <summary>The share of a tile its neighbour also covers.</summary>
    public double Overlap { get; init; } = 0.2;

    public double ThrowRatio { get; init; } = 1.5;

    /// <summary>The width the tiles cover together (metres).</summary>
    public double CoveredWidthM { get; init; } = 3.6;

    /// <summary>How far each projector is turned off square (degrees, about each of its axes, drawn per projector).</summary>
    public double JitterDegrees { get; init; } = 1.5;

    /// <summary>A webcam's barrel with the terms a two-coefficient fit leaves out.</summary>
    public LensDistortion CameraLens { get; init; } = new(-0.08, 0.02, 0.0004, -0.0003, -0.003);

    public Vec3d CameraOffset { get; init; } = new(0.3, -0.2, 0);

    public SimScreen Screen { get; init; } = SimScreen.Flat;

    /// <summary>Each projector's gamma, in order (the last repeats).</summary>
    public IReadOnlyList<double> Gammas { get; init; } = [2.2];

    /// <summary>Each projector's white, in order (the last repeats).</summary>
    public IReadOnlyList<double> Whites { get; init; } = [0.55];

    public double Black { get; init; } = 0.002;

    public double CornerFalloff { get; init; }

    public double NoiseSigma { get; init; } = 0.8;

    public ulong Seed { get; init; } = 1;

    /// <summary>Per projector raster (in order; the last repeats); empty takes <see cref="ProjectorWidth"/> × <see cref="ProjectorHeight"/>.</summary>
    public IReadOnlyList<(int Width, int Height)> Rasters { get; init; } = [];

    /// <summary>Each projector's colour temperature (K), in order (the last repeats); empty is grey light.</summary>
    public IReadOnlyList<double> Kelvins { get; init; } = [];

    /// <summary>Each projector's light level, a factor on its white (the last repeats).</summary>
    public IReadOnlyList<double> Levels { get; init; } = [1];

    /// <summary>The camera sets its own exposure per picture.</summary>
    public bool AutoExposure { get; init; }

    /// <summary>Projectors knocked after they were set up: by index (0-based), how many of its pixels its picture moves across.</summary>
    public IReadOnlyDictionary<int, double> Shifts { get; init; } = new Dictionary<int, double>();

    internal (int Width, int Height) RasterOf(int j) =>
        Rasters.Count == 0 ? (ProjectorWidth, ProjectorHeight) : Rasters[Math.Min(j, Rasters.Count - 1)];

    internal static double Pick(IReadOnlyList<double> values, int j, double fallback) =>
        values.Count == 0 ? fallback : values[Math.Min(j, values.Count - 1)];
}

/// <summary>
/// The simulated world an alignment runs in: several projectors lighting one screen that one camera sees. Each capture composes, in
/// linear light, what every projector shows (bilinear between its pixels, through its gamma and its white and black, with its corner
/// fall-off) plus the room's light, then records it through the camera (exposure, encoding gamma, noise drawn from a seeded
/// <see cref="Pcg32"/> per capture and row, so a run is the same every time). The truth — where every camera pixel's screen point lands
/// in each projector, where a projector pixel lands on the screen — is kept for the tests. The camera's lens and a screen that is
/// not quite flat are richer than the alignment's own model, on purpose.
/// </summary>
public sealed class MultiProjectorSim
{
    private readonly int _n;
    private readonly float[][] _qx;
    private readonly float[][] _qy;
    private readonly float[][] _fall;
    private readonly float[][] _luts;
    private long _captures;

    public MultiProjectorSim(SimScreen screen, SimCamera camera, IReadOnlyList<SimProjector> projectors)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(projectors);
        if (projectors.Count == 0)
            throw new ArgumentException("A simulation needs a projector.", nameof(projectors));
        Screen = screen;
        Camera = camera;
        Projectors = [.. projectors];
        CameraWidth = camera.Intrinsics.Width;
        CameraHeight = camera.Intrinsics.Height;
        _n = CameraWidth * CameraHeight;
        _qx = new float[projectors.Count][];
        _qy = new float[projectors.Count][];
        _fall = new float[projectors.Count][];
        _luts = new float[projectors.Count][];
        for (var j = 0; j < projectors.Count; j++)
        {
            _qx[j] = new float[_n];
            _qy[j] = new float[_n];
            _fall[j] = new float[_n];
            Array.Fill(_qx[j], float.NaN);
            Array.Fill(_qy[j], float.NaN);
            var lut = new float[256];
            for (var v = 0; v < 256; v++)
                lut[v] = (float)Math.Pow(v / 255.0, projectors[j].Gamma);
            _luts[j] = lut;
        }
        Parallel.For(0, CameraHeight, Compute.Options, y =>
        {
            for (var x = 0; x < CameraWidth; x++)
            {
                if (CameraToScreen(new Vec2d(x + 0.5, y + 0.5)) is not { } world)
                    continue;
                var k = y * CameraWidth + x;
                for (var j = 0; j < Projectors.Count; j++)
                {
                    if (ScreenToProjector(j, world) is not { } q)
                        continue;
                    _qx[j][k] = (float)q.X;
                    _qy[j][k] = (float)q.Y;
                    _fall[j][k] = (float)Falloff(j, q);
                }
            }
        });
    }

    public SimScreen Screen { get; }

    public SimCamera Camera { get; }

    public IReadOnlyList<SimProjector> Projectors { get; }

    public int CameraWidth { get; }

    public int CameraHeight { get; }

    /// <summary>How many pictures the camera took.</summary>
    public long Captures => Interlocked.Read(ref _captures);

    // ---- layouts ------------------------------------------------------------------------------------------------------------

    /// <summary>Tiles: <paramref name="columns"/> × <paramref name="rows"/> projectors side by side with the layout's overlap, each a little
    /// off square, under one camera a little off the screen's axis.</summary>
    public static MultiProjectorSim Tiles(int columns, int rows, SimLayout? layout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        var o = layout ?? new SimLayout();
        var (w0, h0) = o.RasterOf(0);
        var tileW = o.CoveredWidthM / (columns - (columns - 1) * o.Overlap);
        var tileH = tileW * h0 / w0;
        var coveredH = rows * tileH - (rows - 1) * o.Overlap * tileH;
        var rng = new Pcg32(o.Seed, 0x5eed);
        var projectors = new List<SimProjector>();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var j = projectors.Count;
                var centre = new Vec3d(-o.CoveredWidthM / 2 + tileW / 2 + c * tileW * (1 - o.Overlap), coveredH / 2 - tileH / 2 - r * tileH * (1 - o.Overlap), 0);
                projectors.Add(Aimed(o, j, centre, tileW, ref rng, 1.0, Vec3d.Zero));
            }
        }
        return new MultiProjectorSim(o.Screen, CameraFor(o, o.CoveredWidthM), projectors);
    }

    /// <summary>A stack: <paramref name="members"/> projectors on one picture, each after the first placed off the first as a stack is
    /// when its members are set up by hand — its picture 0.8 px along and 0.4 px down, a 0.3 % longer throw, 0.5° of roll.</summary>
    public static MultiProjectorSim Stack(int members, SimLayout? layout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(members, 1);
        var o = layout ?? new SimLayout();
        var rng = new Pcg32(o.Seed, 0x5eed);
        var first = Aimed(o, 0, Vec3d.Zero, o.CoveredWidthM, ref rng, 1.0, Vec3d.Zero);
        var projectors = new List<SimProjector> { first };
        for (var j = 1; j < members; j++)
        {
            var (w, h) = o.RasterOf(j);
            var k0 = Intrinsics.FromThrowRatio(w, h, o.ThrowRatio);
            var zoom = 1 + 0.003 * j;
            var k = k0 with { Fx = k0.Fx * zoom, Fy = k0.Fy * zoom };
            // Turning the lens by angle a moves the picture by about f·a pixels: 0.8 px across and 0.4 px down per member.
            var turn = new Vec3d(-0.4 * j / k.Fy, (0.8 * j + o.Shifts.GetValueOrDefault(j)) / k.Fx, 0.5 * j * Math.PI / 180);
            var r = first.Pose.CameraToWorld * Mat3d.FromRotationVector(turn);
            projectors.Add(first with
            {
                Intrinsics = k,
                Pose = new Pose(first.Pose.Position, r.NearestRotation().ToRotationVector()),
                Gamma = SimLayout.Pick(o.Gammas, j, 2.2),
                White = SimLayout.Pick(o.Whites, j, 0.55),
            });
        }
        return new MultiProjectorSim(o.Screen, CameraFor(o, o.CoveredWidthM), projectors);
    }

    /// <summary>A layout named the way a simulated camera's source names it: "[flat-screen|bulged-screen|curved-screen]:tile&lt;c&gt;x&lt;r&gt;[:o&lt;pct&gt;]"
    /// or "…:stack&lt;n&gt;", with ":k&lt;kelvin&gt;,…" (each projector's colour temperature), ":lvl&lt;level&gt;,…" (its light, 0–1) and
    /// ":autoexposure" (a camera that sets its own exposure) and ":shift&lt;n&gt;=&lt;px&gt;" (projector n, from 1, knocked so its picture moves
    /// that many of its pixels across); ":drift&lt;n&gt;=&lt;px&gt;" moves it that much only in the simulator built <paramref name="drifted"/>
    /// (the picture after the scan: a projector that drifted while the run went on); anything else is a row of <paramref name="rasters"/>.Count tiles on a flat screen. The rasters are the
    /// projectors' own, in order; the camera's raster is given.</summary>
    public static MultiProjectorSim FromSource(string source, IReadOnlyList<(int Width, int Height)> rasters, int cameraWidth, int cameraHeight,
        bool drifted = false)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        var n = Math.Max(1, rasters.Count);
        var parts = (source ?? "").Trim().ToLowerInvariant().Split(':', StringSplitOptions.RemoveEmptyEntries);
        var layout = new SimLayout { Rasters = [.. rasters], CameraWidth = cameraWidth, CameraHeight = cameraHeight };
        if (parts.Length > 0)
        {
            layout = parts[0] switch
            {
                "curved-screen" => layout with { Screen = SimScreen.Curved(3) },
                "bulged-screen" => layout with { Screen = SimScreen.Bulged(0.004) },
                _ => layout,
            };
        }
        var columns = n;
        var rows = 1;
        var stack = 0;
        foreach (var p in parts.Skip(1))
        {
            if (p.StartsWith("tile", StringComparison.Ordinal) && p[4..].Split('x') is [var cs, var rs]
                && int.TryParse(cs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && int.TryParse(rs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
                && c >= 1 && r >= 1 && c * r == n)
                (columns, rows) = (c, r);
            else if (p.StartsWith("stack", StringComparison.Ordinal))
                stack = n;
            else if (p.StartsWith('o') && int.TryParse(p[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct) && pct is >= 5 and <= 60)
                layout = layout with { Overlap = pct / 100.0 };
            else if (p.StartsWith('k') && Numbers(p[1..]) is { Count: > 0 } kelvins && kelvins.All(t => t is >= 4000 and <= 25000))
                layout = layout with { Kelvins = kelvins };
            else if (p.StartsWith("lvl", StringComparison.Ordinal) && Numbers(p[3..]) is { Count: > 0 } levels && levels.All(l => l is > 0 and <= 1))
                layout = layout with { Levels = levels };
            else if (p == "autoexposure")
                layout = layout with { AutoExposure = true };
            else if (p.StartsWith("shift", StringComparison.Ordinal) && p[5..].Split('=') is [var who, var px]
                && int.TryParse(who, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n1) && n1 >= 1
                && double.TryParse(px, NumberStyles.Float, CultureInfo.InvariantCulture, out var by) && Math.Abs(by) <= 200)
                layout = layout with { Shifts = Add(layout.Shifts, n1 - 1, by) };
            else if (p.StartsWith("drift", StringComparison.Ordinal) && p[5..].Split('=') is [var whom, var dpx]
                && int.TryParse(whom, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n2) && n2 >= 1
                && double.TryParse(dpx, NumberStyles.Float, CultureInfo.InvariantCulture, out var drift) && Math.Abs(drift) <= 200)
            {
                if (drifted)
                    layout = layout with { Shifts = Add(layout.Shifts, n2 - 1, drift) };
            }
        }
        return stack > 0 ? Stack(stack, layout) : Tiles(columns, rows, layout);
    }

    private static Dictionary<int, double> Add(IReadOnlyDictionary<int, double> shifts, int index, double px)
    {
        var d = new Dictionary<int, double>(shifts);
        d[index] = d.GetValueOrDefault(index) + px;
        return d;
    }

    /// <summary>True when a source names a projector that drifts after the scan (":drift&lt;n&gt;=&lt;px&gt;").</summary>
    public static bool Drifts(string? source) => (source ?? "").Contains(":drift", StringComparison.OrdinalIgnoreCase);

    private static List<double>? Numbers(string list)
    {
        var values = new List<double>();
        foreach (var word in list.Split(','))
        {
            if (!double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return null;
            values.Add(v);
        }
        return values;
    }

    private static SimProjector Aimed(SimLayout o, int j, Vec3d centre, double tileW, ref Pcg32 rng, double zoom, Vec3d extraTurn)
    {
        var (w, h) = o.RasterOf(j);
        var k0 = Intrinsics.FromThrowRatio(w, h, o.ThrowRatio);
        var k = k0 with { Fx = k0.Fx * zoom, Fy = k0.Fy * zoom };
        var distance = o.ThrowRatio * tileW;
        var jitter = o.JitterDegrees * Math.PI / 180;
        var eye = centre + new Vec3d(rng.Range(-0.05, 0.05), rng.Range(-0.05, 0.05), distance);
        var look = Pose.LookAt(eye, centre, Vec3d.UnitY);
        var turn = new Vec3d(rng.Range(-jitter, jitter), rng.Range(-jitter, jitter), rng.Range(-jitter, jitter)) + extraTurn
            + new Vec3d(0, o.Shifts.GetValueOrDefault(j) / k.Fx, 0);
        var r = look.CameraToWorld * Mat3d.FromRotationVector(turn);
        return new SimProjector(k, new Pose(eye, r.NearestRotation().ToRotationVector()))
        {
            Gamma = SimLayout.Pick(o.Gammas, j, 2.2),
            White = SimLayout.Pick(o.Whites, j, 0.55) * SimLayout.Pick(o.Levels, j, 1),
            Black = o.Black,
            CornerFalloff = o.CornerFalloff,
            Response = o.Kelvins.Count == 0 ? null : SimProjector.ResponseAt(SimLayout.Pick(o.Kelvins, j, 6500), j % 2 == 1 ? 0.01 : 0),
        };
    }

    private static SimCamera CameraFor(SimLayout o, double coveredWidth)
    {
        var k = Intrinsics.FromFieldOfView(o.CameraWidth, o.CameraHeight, o.CameraFovDegrees);
        var distance = coveredWidth / 2 / (0.8 * Math.Tan(o.CameraFovDegrees * Math.PI / 360));
        var eye = new Vec3d(o.CameraOffset.X, o.CameraOffset.Y, distance + o.CameraOffset.Z);
        return new SimCamera(k, o.CameraLens, Pose.LookAt(eye, Vec3d.Zero, Vec3d.UnitY)) { NoiseSigma = o.NoiseSigma, Seed = o.Seed, AutoExposure = o.AutoExposure };
    }

    // ---- capture ------------------------------------------------------------------------------------------------------------

    /// <summary>The camera's picture (gray8, row-major) while each projector shows its frame (gray8 at its raster; null shows black).</summary>
    public byte[] Capture(IReadOnlyList<byte[]?> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (shown.Count != Projectors.Count)
            throw new ArgumentException("One frame (or null) per projector.", nameof(shown));
        for (var j = 0; j < shown.Count; j++)
        {
            var k = Projectors[j].Intrinsics;
            if (shown[j] is { } f && f.Length != k.Width * k.Height)
                throw new ArgumentException($"Projector {j}'s frame is not its raster.", nameof(shown));
        }
        var capture = (ulong)Interlocked.Increment(ref _captures);
        var picture = new byte[_n];
        var cam = Camera;
        var inv = 1 / cam.EncodeGamma;
        var exposure = cam.AutoExposure ? AutoExposure(j => shown[j] is { } f ? (f, 1) : (null, 1)) : cam.Exposure;
        Parallel.For(0, CameraHeight, Compute.Options, y =>
        {
            var rng = new Pcg32(cam.Seed * 0x9E3779B97F4A7C15UL + capture, (ulong)y * 2 + 1);
            for (var x = 0; x < CameraWidth; x++)
            {
                var k = y * CameraWidth + x;
                var light = cam.Ambient;
                for (var j = 0; j < Projectors.Count; j++)
                {
                    var qx = _qx[j][k];
                    if (float.IsNaN(qx))
                        continue;
                    var p = Projectors[j];
                    var s = shown[j] is { } frame ? Sample(frame, p.Intrinsics.Width, p.Intrinsics.Height, _luts[j], qx, _qy[j][k]) : 0;
                    light += p.Black + (p.White - p.Black) * _fall[j][k] * s;
                }
                var e = Math.Clamp(light * exposure, 0, 1);
                var v = 255 * Math.Pow(e, inv) + cam.NoiseSigma * rng.NextGaussian();
                picture[k] = (byte)Math.Clamp(Math.Round(v), 0, 255);
            }
        });
        return picture;
    }

    /// <summary>The camera's colour picture (interleaved RGB) while each projector shows its frame (interleaved RGB at its raster; null
    /// shows black): each channel the room's light plus every projector's black and its primaries' light through its response.</summary>
    public byte[] CaptureRgb(IReadOnlyList<byte[]?> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (shown.Count != Projectors.Count)
            throw new ArgumentException("One frame (or null) per projector.", nameof(shown));
        for (var j = 0; j < shown.Count; j++)
        {
            var k = Projectors[j].Intrinsics;
            if (shown[j] is { } f && f.Length != k.Width * k.Height * 3)
                throw new ArgumentException($"Projector {j}'s frame is not its raster in RGB.", nameof(shown));
        }
        var capture = (ulong)Interlocked.Increment(ref _captures);
        var picture = new byte[_n * 3];
        var cam = Camera;
        var inv = 1 / cam.EncodeGamma;
        var exposure = cam.AutoExposure ? AutoExposure(j => shown[j] is { } f ? (f, 3) : (null, 3)) : cam.Exposure;
        var responses = Projectors.Select(p => p.Response ?? Mat3d.Identity).ToArray();
        Parallel.For(0, CameraHeight, Compute.Options, y =>
        {
            var rng = new Pcg32(cam.Seed * 0x9E3779B97F4A7C15UL + capture, (ulong)y * 2 + 1);
            for (var x = 0; x < CameraWidth; x++)
            {
                var k = y * CameraWidth + x;
                var light = new Vec3d(cam.Ambient, cam.Ambient, cam.Ambient);
                for (var j = 0; j < Projectors.Count; j++)
                {
                    var qx = _qx[j][k];
                    if (float.IsNaN(qx))
                        continue;
                    var p = Projectors[j];
                    var drive = shown[j] is { } frame
                        ? new Vec3d(SampleRgb(frame, p.Intrinsics.Width, p.Intrinsics.Height, _luts[j], qx, _qy[j][k], 0),
                            SampleRgb(frame, p.Intrinsics.Width, p.Intrinsics.Height, _luts[j], qx, _qy[j][k], 1),
                            SampleRgb(frame, p.Intrinsics.Width, p.Intrinsics.Height, _luts[j], qx, _qy[j][k], 2))
                        : Vec3d.Zero;
                    var lit = responses[j] * drive * ((p.White - p.Black) * _fall[j][k]);
                    light += lit + new Vec3d(p.Black, p.Black, p.Black);
                }
                for (var c = 0; c < 3; c++)
                {
                    var e = Math.Clamp((c == 0 ? light.X : c == 1 ? light.Y : light.Z) * exposure, 0, 1);
                    var v = 255 * Math.Pow(e, inv) + cam.NoiseSigma * rng.NextGaussian();
                    picture[k * 3 + c] = (byte)Math.Clamp(Math.Round(v), 0, 255);
                }
            }
        });
        return picture;
    }

    /// <summary>The exposure a camera on automatic picks for a picture: its mean grey light (rows summed in order) brought toward 0.25,
    /// within a webcam's reach of 0.6–1.6 times its set exposure.</summary>
    private double AutoExposure(Func<int, (byte[]? Frame, int Channels)> frameOf)
    {
        var rows = new double[CameraHeight];
        Parallel.For(0, CameraHeight, Compute.Options, y =>
        {
            double sum = 0;
            for (var x = 0; x < CameraWidth; x++)
            {
                var k = y * CameraWidth + x;
                var light = Camera.Ambient;
                for (var j = 0; j < Projectors.Count; j++)
                {
                    var qx = _qx[j][k];
                    if (float.IsNaN(qx))
                        continue;
                    var p = Projectors[j];
                    var (frame, channels) = frameOf(j);
                    var s = frame is null ? 0 : channels == 1
                        ? Sample(frame, p.Intrinsics.Width, p.Intrinsics.Height, _luts[j], qx, _qy[j][k])
                        : SampleRgb(frame, p.Intrinsics.Width, p.Intrinsics.Height, _luts[j], qx, _qy[j][k], 1);
                    light += p.Black + (p.White - p.Black) * _fall[j][k] * s;
                }
                sum += light;
            }
            rows[y] = sum;
        });
        var mean = rows.Sum() / _n;
        return Camera.Exposure * Math.Clamp(0.25 / Math.Max(mean, 1e-4), 0.6, 1.6);
    }

    /// <summary>One channel of an interleaved RGB frame's light at a continuous projector point, bilinear.</summary>
    private static double SampleRgb(byte[] frame, int w, int h, float[] lut, float qx, float qy, int channel)
    {
        var fx = qx - 0.5;
        var fy = qy - 0.5;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var xa = Math.Clamp(x0, 0, w - 1);
        var xb = Math.Clamp(x0 + 1, 0, w - 1);
        var ya = Math.Clamp(y0, 0, h - 1);
        var yb = Math.Clamp(y0 + 1, 0, h - 1);
        var top = lut[frame[(ya * w + xa) * 3 + channel]] * (1 - tx) + lut[frame[(ya * w + xb) * 3 + channel]] * tx;
        var bottom = lut[frame[(yb * w + xa) * 3 + channel]] * (1 - tx) + lut[frame[(yb * w + xb) * 3 + channel]] * tx;
        return top * (1 - ty) + bottom * ty;
    }

    /// <summary>The light a frame emits at a continuous projector point: its four nearest pixels' light, bilinear.</summary>
    private static double Sample(byte[] frame, int w, int h, float[] lut, float qx, float qy)
    {
        var fx = qx - 0.5;
        var fy = qy - 0.5;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var xa = Math.Clamp(x0, 0, w - 1);
        var xb = Math.Clamp(x0 + 1, 0, w - 1);
        var ya = Math.Clamp(y0, 0, h - 1);
        var yb = Math.Clamp(y0 + 1, 0, h - 1);
        var top = lut[frame[ya * w + xa]] * (1 - tx) + lut[frame[ya * w + xb]] * tx;
        var bottom = lut[frame[yb * w + xa]] * (1 - tx) + lut[frame[yb * w + xb]] * tx;
        return top * (1 - ty) + bottom * ty;
    }

    private double Falloff(int j, Vec2d q)
    {
        var p = Projectors[j];
        if (p.CornerFalloff <= 0)
            return 1;
        var k = p.Intrinsics;
        var dx = (q.X - k.Width / 2.0) / (k.Width / 2.0);
        var dy = (q.Y - k.Height / 2.0) / (k.Height / 2.0);
        return 1 - p.CornerFalloff * (dx * dx + dy * dy) / 2;
    }

    // ---- truth --------------------------------------------------------------------------------------------------------------

    /// <summary>Projector <paramref name="j"/>'s white light at its raster point (its corner fall-off applied), less its black.</summary>
    public double WhiteAt(int j, Vec2d q) => (Projectors[j].White - Projectors[j].Black) * Falloff(j, q);

    /// <summary>Where camera pixel <paramref name="cameraPx"/> sees the screen, or null where it sees past it.</summary>
    public Vec3d? CameraToScreen(Vec2d cameraPx) =>
        Intersect(CameraModel.PixelRay(cameraPx, Camera.Intrinsics, Camera.Lens, Camera.Pose));

    /// <summary>The camera pixel a screen point is seen at, or null.</summary>
    public Vec2d? ScreenToCamera(Vec3d world) =>
        CameraModel.Project(world, Camera.Intrinsics, Camera.Lens, Camera.Pose) is { } c && CameraModel.InRaster(c, Camera.Intrinsics) ? c : null;

    /// <summary>Where projector <paramref name="j"/>'s raster point lands on the screen, or null.</summary>
    public Vec3d? ProjectorToScreen(int j, Vec2d q)
    {
        var p = Projectors[j];
        return Intersect(CameraModel.PixelRay(q, p.Intrinsics, LensDistortion.None, p.Pose));
    }

    /// <summary>The raster point of projector <paramref name="j"/> that lights a screen point, or null outside its raster.</summary>
    public Vec2d? ScreenToProjector(int j, Vec3d world)
    {
        var p = Projectors[j];
        return CameraModel.Project(world, p.Intrinsics, LensDistortion.None, p.Pose) is { } q && CameraModel.InRaster(q, p.Intrinsics) ? q : null;
    }

    /// <summary>Where a screen point falls in projector <paramref name="j"/>'s raster plane, inside the raster or not (null behind it).</summary>
    public Vec2d? ScreenToRaster(int j, Vec3d world)
    {
        var p = Projectors[j];
        return CameraModel.Project(world, p.Intrinsics, LensDistortion.None, p.Pose);
    }

    /// <summary>How far a screen point is from projector <paramref name="j"/>'s raster edge, in its pixels (inside or out).</summary>
    public double DistanceToRasterEdge(int j, Vec3d world)
    {
        if (ScreenToRaster(j, world) is not { } q)
            return double.PositiveInfinity;
        var k = Projectors[j].Intrinsics;
        var dx = Math.Min(Math.Abs(q.X), Math.Abs(k.Width - q.X));
        var dy = Math.Min(Math.Abs(q.Y), Math.Abs(k.Height - q.Y));
        var insideX = q.X >= 0 && q.X <= k.Width;
        var insideY = q.Y >= 0 && q.Y <= k.Height;
        return insideX && insideY ? Math.Min(dx, dy) : insideX ? dy : insideY ? dx : Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The projector point camera pixel (x, y)'s screen point lands at in projector <paramref name="j"/>, or null.</summary>
    public Vec2d? CameraToProjector(int j, int x, int y)
    {
        var k = y * CameraWidth + x;
        return float.IsNaN(_qx[j][k]) ? null : new Vec2d(_qx[j][k], _qy[j][k]);
    }

    /// <summary>A screen point in the screen's own metres: across (arc length on a curved screen) and down.</summary>
    public Vec2d OnScreen(Vec3d world) => Screen.Shape == SimScreenShape.Cylinder
        ? new Vec2d(Screen.RadiusM * Math.Atan2(world.X, Screen.RadiusM - world.Z), -world.Y)
        : new Vec2d(world.X, -world.Y);

    /// <summary>How many projector pixels of projector <paramref name="j"/> one metre of screen is, at its raster point.</summary>
    public double PixelsPerMetre(int j, Vec2d q)
    {
        if (ProjectorToScreen(j, q) is not { } a || ProjectorToScreen(j, q + new Vec2d(1, 0)) is not { } b)
            return double.NaN;
        return 1 / Vec3d.Distance(a, b);
    }

    private Vec3d? Intersect(Ray ray)
    {
        var o = ray.Origin;
        var d = ray.Direction;
        if (Screen.Shape == SimScreenShape.Cylinder)
        {
            // (o.x + t d.x)² + (o.z + t d.z − R)² = R²; the camera stands inside, so the forward root is the far wall.
            var r = Screen.RadiusM;
            var oz = o.Z - r;
            var a = d.X * d.X + d.Z * d.Z;
            var b = 2 * (o.X * d.X + oz * d.Z);
            var c = o.X * o.X + oz * oz - r * r;
            var disc = b * b - 4 * a * c;
            if (a < 1e-12 || disc < 0)
                return null;
            var t = (-b + Math.Sqrt(disc)) / (2 * a);
            return t > 0 ? o + d * t : null;
        }
        if (Math.Abs(d.Z) < 1e-12)
            return null;
        var tPlane = -o.Z / d.Z;
        if (tPlane <= 0)
            return null;
        if (Screen.BulgeM == 0)
            return o + d * tPlane;
        // z(x, y) = bulge · cos(πx/W) · cos(πy/H) inside the bulge, 0 outside: Newton on t from the plane's hit.
        var tt = tPlane;
        for (var i = 0; i < 8; i++)
        {
            var p = o + d * tt;
            var f = p.Z - Bulge(p.X, p.Y);
            const double h = 1e-6;
            var p2 = o + d * (tt + h);
            var df = (p2.Z - Bulge(p2.X, p2.Y) - f) / h;
            if (Math.Abs(df) < 1e-12)
                break;
            tt -= f / df;
        }
        return o + d * tt;
    }

    private double Bulge(double x, double y)
    {
        var s = Screen;
        if (Math.Abs(x) >= s.BulgeWidthM / 2 || Math.Abs(y) >= s.BulgeHeightM / 2)
            return 0;
        return s.BulgeM * Math.Cos(Math.PI * x / s.BulgeWidthM) * Math.Cos(Math.PI * y / s.BulgeHeightM);
    }
}
