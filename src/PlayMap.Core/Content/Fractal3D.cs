using System.Numerics;
using PlayMap.Core.Model;

namespace PlayMap.Core.Content;

/// <summary>The raymarched fractal families: each a distance estimator (DE) one marcher shares.</summary>
public enum Fractal3DKind { Mandelbulb, Mandelbox, Kifs }

/// <summary>The fold a KIFS fractal reflects by each iteration.</summary>
public enum KifsFold { Tetra, Octa, Menger }

/// <summary>The numbers a DE reads, all floats as the shader has them.</summary>
public readonly record struct Fractal3DParams(int Iterations, float Power, float Scale, float FoldLimit, float MinRadius,
    float FixedRadius, KifsFold Fold, Vector3 Offset, float Twist);

/// <summary>A raymarched fractal's settings: its family, the DE's numbers, the camera's orbit, the march, the look and the render
/// scale. Resolved once per parameter change; the renderer reads the bindable numbers every frame.</summary>
public sealed record Fractal3DSettings(Fractal3DKind Kind, Fractal3DParams De, string Colors, string Background)
{
    public const int PaletteColors = 5;

    public static bool IsKind(string? kindId) => kindId is not null && KindOf(kindId) is not null;

    public static Fractal3DKind? KindOf(string kindId) => kindId.ToLowerInvariant() switch
    {
        "fractal.mandelbulb" => Fractal3DKind.Mandelbulb,
        "fractal.mandelbox" => Fractal3DKind.Mandelbox,
        "fractal.kifs" => Fractal3DKind.Kifs,
        _ => null,
    };

    public ColorRgb[] Palette()
    {
        var all = ColorRgb.ParseList(Colors, ColorRgb.White);
        return all.Length <= PaletteColors ? all : all[..PaletteColors];
    }

    public static Fractal3DSettings Resolve(string kindId, ParamSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var kind = GeneratorCatalog.Find(kindId) ?? throw new ArgumentException($"no generator kind {kindId}", nameof(kindId));
        var family = KindOf(kind.Id) ?? throw new ArgumentException($"{kindId} is not a raymarched fractal", nameof(kindId));
        float N(string name) => ParamValues.Spec(kind, name) is null ? 0f : (float)ParamValues.Number(set, kind, name);
        var de = new Fractal3DParams(
            ParamValues.Integer(set, kind, "iterations"),
            N("power"),
            N("scale"),
            N("foldLimit"),
            N("minRadius"),
            N("fixedRadius"),
            family == Fractal3DKind.Kifs ? Enum.Parse<KifsFold>(ParamValues.Choice(set, kind, "fold"), ignoreCase: true) : KifsFold.Tetra,
            new Vector3(N("offsetX"), N("offsetY"), N("offsetZ")),
            N("twist"));
        return new Fractal3DSettings(family, de, ParamValues.Text(set, kind, "colors"), ParamValues.Text(set, kind, "background"));
    }
}

/// <summary>The camera a raymarched frame is seen from: an orbit round the origin at a distance and height, turning with the
/// generator's time, looking at the centre with a vertical field of view. Pure: the same time gives the same camera.</summary>
public readonly record struct Fractal3DCamera(Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Forward, float TanHalfFov)
{
    public static Fractal3DCamera Orbit(double seconds, double speed, double distance, double height, double fovDegrees)
    {
        var a = ContentTime.Wrap(seconds * speed * 0.2, Math.Tau);
        var eye = new Vector3((float)(Math.Sin(a) * distance), (float)height, (float)(Math.Cos(a) * distance));
        var forward = Vector3.Normalize(-eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        return new Fractal3DCamera(eye, right, up, forward, (float)Math.Tan(fovDegrees * Math.PI / 360));
    }

    /// <summary>The ray through a pixel centre (pixels from the top-left, y down) of a w×h picture.</summary>
    public Vector3 Ray(float px, float py, int w, int h)
    {
        var x = (2f * px - w) / h * TanHalfFov;
        var y = (h - 2f * py) / h * TanHalfFov;
        return Vector3.Normalize(Forward + Right * x + Up * y);
    }
}

/// <summary>The distance estimators and the marcher in single precision, written once here and mirrored line for line in the
/// <c>raymarch</c> program, so the CPU twin and the GPU agree on where a ray meets the surface.</summary>
public static class Fractal3D
{
    public const float Far = 20f;

    /// <summary>The distance from <paramref name="p"/> to the fractal's surface (an estimate never larger than the truth, so a
    /// ray may step by it), and the orbit trap (the smallest |w|² the iteration came to: the colour).</summary>
    public static float Distance(Fractal3DKind kind, Vector3 p, in Fractal3DParams q, out float trap) => kind switch
    {
        Fractal3DKind.Mandelbox => Box(p, q, out trap),
        Fractal3DKind.Kifs => Kifs(p, q, out trap),
        _ => Bulb(p, q, out trap),
    };

    public static float Distance(Fractal3DKind kind, Vector3 p, in Fractal3DParams q) => Distance(kind, p, q, out _);

    private static float Bulb(Vector3 p, in Fractal3DParams q, out float trap)
    {
        var w = p;
        var dr = 1f;
        var r = w.Length();
        trap = 1e10f;
        for (var i = 0; i < 16; i++)
        {
            if (i >= q.Iterations || r > 2f || r < 1e-6f)
                break;
            trap = MathF.Min(trap, r * r);
            var theta = MathF.Acos(Math.Clamp(w.Y / r, -1f, 1f)) * q.Power;
            var phi = MathF.Atan2(w.X, w.Z) * q.Power;
            dr = MathF.Pow(r, q.Power - 1f) * q.Power * dr + 1f;
            var zr = MathF.Pow(r, q.Power);
            w = zr * new Vector3(MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Cos(phi)) + p;
            r = w.Length();
        }
        return 0.5f * MathF.Log(MathF.Max(r, 1e-6f)) * r / dr;
    }

    private static float Box(Vector3 p, in Fractal3DParams q, out float trap)
    {
        var w = p;
        var dr = 1f;
        var minR2 = q.MinRadius * q.MinRadius;
        var fixedR2 = q.FixedRadius * q.FixedRadius;
        trap = 1e10f;
        for (var i = 0; i < 20; i++)
        {
            if (i >= q.Iterations)
                break;
            w = Vector3.Clamp(w, new Vector3(-q.FoldLimit), new Vector3(q.FoldLimit)) * 2f - w;
            var r2 = Vector3.Dot(w, w);
            if (r2 < minR2)
            {
                var k = fixedR2 / minR2;
                w *= k;
                dr *= k;
            }
            else if (r2 < fixedR2)
            {
                var k = fixedR2 / r2;
                w *= k;
                dr *= k;
            }
            w = q.Scale * w + p;
            dr = dr * MathF.Abs(q.Scale) + 1f;
            trap = MathF.Min(trap, Vector3.Dot(w, w));
        }
        return w.Length() / MathF.Abs(dr);
    }

    private static float Kifs(Vector3 p, in Fractal3DParams q, out float trap)
    {
        var w = p;
        var c = MathF.Cos(q.Twist);
        var s = MathF.Sin(q.Twist);
        var n = 0;
        trap = 1e10f;
        for (var i = 0; i < 20; i++)
        {
            if (i >= q.Iterations)
                break;
            w = new Vector3(c * w.X + s * w.Z, w.Y, -s * w.X + c * w.Z);
            if (q.Fold == KifsFold.Tetra)
            {
                if (w.X + w.Y < 0f)
                    w = new Vector3(-w.Y, -w.X, w.Z);
                if (w.X + w.Z < 0f)
                    w = new Vector3(-w.Z, w.Y, -w.X);
                if (w.Y + w.Z < 0f)
                    w = new Vector3(w.X, -w.Z, -w.Y);
            }
            else
            {
                w = Vector3.Abs(w);
                if (w.X < w.Y)
                    w = new Vector3(w.Y, w.X, w.Z);
                if (w.X < w.Z)
                    w = new Vector3(w.Z, w.Y, w.X);
                if (w.Y < w.Z)
                    w = new Vector3(w.X, w.Z, w.Y);
            }
            w = w * q.Scale - q.Offset * (q.Scale - 1f);
            if (q.Fold == KifsFold.Menger && w.Z < -0.5f * q.Offset.Z * (q.Scale - 1f))
                w = new Vector3(w.X, w.Y, w.Z + q.Offset.Z * (q.Scale - 1f));
            trap = MathF.Min(trap, Vector3.Dot(w, w));
            n++;
        }
        return (w.Length() - 1f) * MathF.Pow(q.Scale, -n);
    }

    /// <summary>Sphere tracing along a ray: the distance to the surface, or −1 when the ray leaves past <see cref="Far"/> or runs out
    /// of steps. A hit is a step closer than <paramref name="detail"/> × the distance travelled (the pixel's own cone).</summary>
    public static float March(Fractal3DKind kind, in Fractal3DParams q, Vector3 eye, Vector3 dir, int steps, float detail)
    {
        var t = 0f;
        for (var i = 0; i < 512; i++)
        {
            if (i >= steps)
                break;
            var d = Distance(kind, eye + dir * t, q);
            if (d < detail * MathF.Max(t, 0.05f))
                return t;
            t += d;
            if (t > Far)
                break;
        }
        return -1f;
    }

    /// <summary>The cone a pixel subtends per unit of distance, times the detail in pixels.</summary>
    public static float Cone(float tanHalfFov, int height, float detailPx) => 2f * tanHalfFov / Math.Max(1, height) * Math.Max(0.1f, detailPx);
}
