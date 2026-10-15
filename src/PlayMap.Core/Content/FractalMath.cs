using PlayMap.Core.Model;

namespace PlayMap.Core.Content;

/// <summary>The fractal families (the catalogue's fractal.* kinds).</summary>
public enum FractalKind
{
    Mandelbrot,
    Julia,
    BurningShip,
    Newton,
    DomainWarp,
}

/// <summary>The fractal families point by point, pure and deterministic — the CPU twin of the shaders, which
/// say the same thing in GLSL line for line (a test holds the two together).</summary>
public static class FractalMath
{
    /// <summary>The escape-time families return 0..1 with smooth colouring (1 = never escaped: inside the
    /// set). Newton returns the root it converged to in thirds — 0, ⅓, ⅔ — plus how slowly, as a fraction of
    /// that third. Domain warp returns a noise value in 0..1 that drifts with time.</summary>
    public static double Sample(FractalKind kind, double x, double y, double cr, double ci, int maxIter, double time, double warp = 0) =>
        kind switch
        {
            FractalKind.Julia => Escape(x, y, cr, ci, maxIter, ship: false),
            FractalKind.BurningShip => Escape(0, 0, x, y, maxIter, ship: true),
            FractalKind.Newton => Newton(x, y, maxIter),
            FractalKind.DomainWarp => Warp(x, y, time, warp),
            _ => Escape(0, 0, x, y, maxIter, ship: false),
        };

    private static double Escape(double zx, double zy, double cx, double cy, int maxIter, bool ship)
    {
        for (var i = 0; i < maxIter; i++)
        {
            if (ship)
            {
                zx = Math.Abs(zx);
                zy = Math.Abs(zy);
            }
            var nx = zx * zx - zy * zy + cx;
            var ny = 2 * zx * zy + cy;
            zx = nx;
            zy = ny;
            var m = zx * zx + zy * zy;
            if (m > 16)
            {
                var n = i + 1 - Math.Log2(Math.Log2(m) * 0.5);
                return Math.Clamp(n / maxIter, 0, 0.999999);
            }
        }
        return 1;
    }

    private static double Newton(double zx, double zy, int maxIter)
    {
        var n = 0;
        for (var i = 0; i < maxIter; i++)
        {
            var z2x = zx * zx - zy * zy;
            var z2y = 2 * zx * zy;
            var z3x = z2x * zx - z2y * zy;
            var z3y = z2x * zy + z2y * zx;
            var fx = z3x - 1;
            var fy = z3y;
            if (fx * fx + fy * fy < 1e-12)
                break;
            var dx = 3 * z2x;
            var dy = 3 * z2y;
            var dd = dx * dx + dy * dy + 1e-12;
            var qx = (fx * dx + fy * dy) / dd;
            var qy = (fy * dx - fx * dy) / dd;
            zx -= qx;
            zy -= qy;
            n = i + 1;
        }
        var ang = Math.Atan2(zy, zx);
        var root = (int)Math.Floor(((ang / (2 * Math.PI) + 1 + 1.0 / 6) % 1.0) * 3);
        var speed = Math.Clamp(n / (double)maxIter, 0, 0.999);
        return root / 3.0 + speed / 3.0;
    }

    // ---- domain warp: value noise, three folds ----
    //
    // In single precision on purpose, the shader's own. The lattice hash multiplies by 123.34 and 456.21 and
    // takes the fraction, then folds the result into itself: a difference in the last place of the constant
    // grows a hundredfold through the fold. Computed in doubles the CPU twin would draw a different cloud
    // from the graphics card's; in floats, with the shader's constants and order of operations, they agree.

    /// <summary><paramref name="warp"/> (0–1) folds the second warp deeper — a sting's morph.</summary>
    public static double Warp(double x, double y, double time, double warp = 0)
    {
        var px = (float)x * 1.5f;
        var py = (float)y * 1.5f;
        var t = (float)time;
        var fold = 3f + 3f * (float)warp;
        var q = FbmF(px + t * 0.11f, py + t * 0.07f);
        var r = FbmF(px + fold * q + 1.7f - t * 0.05f, py + fold * q + 9.2f - t * 0.05f);
        return Math.Clamp(FbmF(px + 3f * r, py + 3f * r), 0f, 1f);
    }

    public static double Fbm(double x, double y) => FbmF((float)x, (float)y);

    public static double Noise(double x, double y) => NoiseF((float)x, (float)y);

    /// <summary>A lattice hash in 0..1, the same arithmetic the shader uses.</summary>
    public static double Hash(double x, double y) => HashF((float)x, (float)y);

    private static float FbmF(float x, float y)
    {
        var v = 0f;
        var amp = 0.5f;
        for (var i = 0; i < 4; i++)
        {
            v += amp * NoiseF(x, y);
            var nx = x * 2.03f + 17.1f;
            var ny = y * 2.03f + 9.3f;
            x = nx;
            y = ny;
            amp *= 0.5f;
        }
        return v;
    }

    private static float NoiseF(float x, float y)
    {
        var ix = MathF.Floor(x);
        var iy = MathF.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        var ux = fx * fx * (3f - 2f * fx);
        var uy = fy * fy * (3f - 2f * fy);
        var a = HashF(ix, iy);
        var b = HashF(ix + 1f, iy);
        var c = HashF(ix, iy + 1f);
        var d = HashF(ix + 1f, iy + 1f);
        return Lerp(Lerp(a, b, ux), Lerp(c, d, ux), uy);
    }

    private static float HashF(float x, float y)
    {
        var px = Fract(x * 123.34f);
        var py = Fract(y * 456.21f);
        var dot = px * (px + 45.32f) + py * (py + 45.32f);
        px += dot;
        py += dot;
        return Fract(px * py);
    }

    private static float Fract(float v) => v - MathF.Floor(v);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>What a fractal generator draws: its family, the view and the palette (2–5 colours).</summary>
public sealed record FractalSettings(
    FractalKind Kind,
    double Zoom,
    double CenterX,
    double CenterY,
    int Iterations,
    double Speed,
    double JuliaRe,
    double JuliaIm,
    string Colors)
{
    /// <summary>The colours a fractal cycles through: the shader has five slots and the CPU twin reads the
    /// same five.</summary>
    public const int PaletteColors = 5;

    /// <summary>The palette, capped at <see cref="PaletteColors"/>, white when it is empty.</summary>
    public ColorRgb[] Palette()
    {
        var all = ColorRgb.ParseList(Colors, ColorRgb.White);
        return all.Length <= PaletteColors ? all : all[..PaletteColors];
    }

    /// <summary>The family a catalogue kind id names ("fractal.julia" → Julia).</summary>
    public static FractalKind KindOf(string kindId) => kindId.ToLowerInvariant() switch
    {
        "fractal.julia" => FractalKind.Julia,
        "fractal.burningship" => FractalKind.BurningShip,
        "fractal.newton" => FractalKind.Newton,
        "fractal.warp" => FractalKind.DomainWarp,
        _ => FractalKind.Mandelbrot,
    };

    /// <summary>A generator's settings: a parameter the operator set wins; the rest come from the named
    /// preset when there is one (its view, depth, motion and palette — the family stays the kind's), then
    /// the catalogue's defaults.</summary>
    public static FractalSettings Resolve(string kindId, ParamSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var kind = GeneratorCatalog.Find(kindId) ?? GeneratorCatalog.Find("fractal.mandelbrot")!;
        var preset = FractalPresets.Find(set.Get("preset"));
        double Num(string name, double? fromPreset) =>
            ParamValues.TryNumber(set, kind, name, out var d) ? d : fromPreset ?? ParamValues.DefaultNumber(kind, name);
        var colors = set.Get("colors") ?? preset?.Colors ?? kind.Value(ParamSet.Empty, "colors");
        return new FractalSettings(
            KindOf(kind.Id),
            Num("zoom", preset?.Zoom),
            Num("centerX", preset?.CenterX),
            Num("centerY", preset?.CenterY),
            (int)Math.Round(Num("iterations", preset?.Iterations)),
            Num("speed", preset?.Speed),
            Num("juliaRe", preset?.JuliaRe),
            Num("juliaIm", preset?.JuliaIm),
            colors);
    }
}

/// <summary>The view of the plane one frame draws: the centre, the plane units per picture height, the
/// animated Julia constant, the palette drift and the brightness, with a sting's surge folded in. Pure, so
/// every node computes the same view from the same leader time.</summary>
public readonly record struct FractalView(double CenterX, double CenterY, double Span, double JuliaRe, double JuliaIm,
                                          int Iterations, double PaletteOffset, double Brightness, double Time)
{
    /// <summary>Plane units across the whole picture height at zoom 1.</summary>
    public const double BaseSpan = 2.4;

    /// <summary>The shaders' loop bound: past this the GPU cost is not worth the detail.</summary>
    public const int ShaderIterationCap = 256;

    public static FractalView Of(FractalSettings o, double time, int iterationCap = 1024, EffectSurge surge = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        return Of(o, FractalNumbers.Of(o), time, iterationCap, surge);
    }

    /// <summary>The view with the numbers a binding may move taken from <paramref name="n"/> (the rest from the settings).</summary>
    public static FractalView Of(FractalSettings o, in FractalNumbers n, double time, int iterationCap = 1024, EffectSurge surge = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        var speed = n.Speed;
        var pulse = 1 + 0.08 * Math.Sin(time * speed * 0.6) + surge.Zoom * 0.6;
        var span = BaseSpan / (n.Zoom * Math.Max(0.5, pulse));
        var morph = surge.Morph;
        var cr = n.JuliaRe + 0.06 * Math.Sin(time * speed) + morph * 0.22 * Math.Sin(time * 1.7 + 0.5);
        var ci = n.JuliaIm + 0.06 * Math.Cos(time * speed * 0.77) + morph * 0.22 * Math.Cos(time * 1.3);
        var offset = time * speed * 0.05 + surge.Speed * 0.15 + surge.Hue;
        var bright = 1 + surge.Glow * 0.8;
        var iterations = Math.Max(1, Math.Min(o.Iterations, iterationCap));
        return new FractalView(n.CenterX, n.CenterY, span, cr, ci, iterations, offset, bright, time)
        {
            Angle = surge.Rotate * Math.PI / 2,
            Warp = morph,
        };
    }

    /// <summary>The plane turned about the centre, radians — a sting's rotate channel.</summary>
    public double Angle { get; init; }

    /// <summary>0–1: how much deeper the domain warp folds — a sting's morph channel.</summary>
    public double Warp { get; init; }

    /// <summary>Plane units per pixel on a picture of this height.</summary>
    public double UnitsPerPixel(int height) => Span / Math.Max(1, height);

    /// <summary>The plane point under a pixel position (pixel centres at +0.5), turned by the angle.</summary>
    public (double X, double Y) ToPlane(double px, double py, int w, int h)
    {
        var upp = UnitsPerPixel(h);
        var dx = px - w / 2.0;
        var dy = py - h / 2.0;
        if (Angle != 0)
        {
            var c = Math.Cos(Angle);
            var s = Math.Sin(Angle);
            (dx, dy) = (dx * c - dy * s, dx * s + dy * c);
        }
        return (CenterX + dx * upp, CenterY + dy * upp);
    }
}

/// <summary>The numbers of a 2D fractal a binding may move, read every frame: its settings' values unless a binding moves them.</summary>
public readonly record struct FractalNumbers(double Zoom, double CenterX, double CenterY, double Speed, double JuliaRe, double JuliaIm)
{
    public static FractalNumbers Of(FractalSettings o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new(o.Zoom, o.CenterX, o.CenterY, o.Speed, o.JuliaRe, o.JuliaIm);
    }
}

/// <summary>The palette lookups the shaders do, on the CPU.</summary>
public static class FractalColor
{
    /// <summary>A cyclic ramp through the palette; <paramref name="u"/> wraps.</summary>
    public static ColorRgb Cycle(IReadOnlyList<ColorRgb> palette, double u, double brightness)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (palette.Count == 0)
            return ColorRgb.White;
        var f = (u - Math.Floor(u)) * palette.Count;
        var i = (int)Math.Floor(f);
        var k = f - i;
        var a = palette[i % palette.Count];
        var b = palette[(i + 1) % palette.Count];
        return ColorRgb.Lerp(a, b, (float)k).Scaled((float)brightness).Clamped();
    }

    /// <summary>A sample of <see cref="FractalMath.Sample"/> as a colour, the way the family's shader paints it.</summary>
    public static ColorRgb Map(FractalKind kind, double v, IReadOnlyList<ColorRgb> palette, double offset, double brightness)
    {
        ArgumentNullException.ThrowIfNull(palette);
        switch (kind)
        {
            case FractalKind.Newton:
            {
                if (palette.Count == 0)
                    return ColorRgb.White;
                var root = Math.Clamp((int)Math.Floor(v * 3), 0, 2);
                var speed = v * 3 - root;
                var shade = 1 - speed * 0.8;
                var rotate = (int)Math.Floor(offset * palette.Count);
                var index = ((root + rotate) % palette.Count + palette.Count) % palette.Count;
                return palette[index].Scaled((float)(shade * brightness)).Clamped();
            }
            case FractalKind.DomainWarp:
                return Cycle(palette, v * 1.5 + offset, brightness);
            default:
                return v >= 1 ? ColorRgb.Black : Cycle(palette, v * 3 + offset, brightness);
        }
    }
}
