using PlayMap.Core.Model;

namespace PlayMap.Core.Content;

/// <summary>The reactive scenes (reactive.scene's "scene" choice).</summary>
public enum ReactiveScene
{
    Plasma,
    Tunnel,
    Kaleidoscope,
    Pulse,
    Vortex,
    StarWarp,
}

/// <summary>What a reactive scene draws: the scene, its pace and shape, and its palette.</summary>
public sealed record ReactiveSettings(ReactiveScene Scene, double Speed, double Depth, int Symmetry, double Rotation, double Brightness, string Colors)
{
    /// <summary>The colours a scene cycles through: the shader has five slots and the CPU twin reads the same five.</summary>
    public const int PaletteColors = 5;

    public ColorRgb[] Palette()
    {
        var all = ColorRgb.ParseList(Colors, ColorRgb.White);
        return all.Length <= PaletteColors ? all : all[..PaletteColors];
    }

    public static ReactiveScene SceneOf(string choice) => choice.ToLowerInvariant() switch
    {
        "tunnel" => ReactiveScene.Tunnel,
        "kaleidoscope" => ReactiveScene.Kaleidoscope,
        "pulse" => ReactiveScene.Pulse,
        "vortex" => ReactiveScene.Vortex,
        "starwarp" => ReactiveScene.StarWarp,
        _ => ReactiveScene.Plasma,
    };

    public static ReactiveSettings Resolve(ParamSet set)
    {
        var kind = GeneratorCatalog.Find("reactive.scene")!;
        return new ReactiveSettings(
            SceneOf(ParamValues.Choice(set, kind, "scene")),
            ParamValues.Number(set, kind, "speed"),
            ParamValues.Number(set, kind, "depth"),
            ParamValues.Integer(set, kind, "symmetry"),
            ParamValues.Number(set, kind, "rotation"),
            ParamValues.Number(set, kind, "brightness"),
            ParamValues.Text(set, kind, "colors"));
    }
}

/// <summary>Everything a reactive scene needs for one frame, read once and handed to both paths: the GPU gets
/// it as uniforms and the CPU twin as this struct, so the two draw the same picture from the same numbers.
/// The scene's own clock runs on leader seconds × speed, so a scene with no sound still moves.</summary>
public readonly record struct ReactiveView(
    double Time,
    double Depth,
    int Symmetry,
    double Angle,
    float Level,
    float Low,
    float Amount,
    float Brightness)
{
    public static ReactiveView Of(ReactiveSettings o, double seconds, EffectSurge surge = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        return Of(o, ReactiveNumbers.Of(o), seconds, surge, ReactiveSound.None);
    }

    /// <summary>The view with the numbers a binding may move taken from <paramref name="n"/>, and the sound: the scene's level and
    /// bass at the frame's instant and how far it may move it (<see cref="ReactiveSound.Amount"/>, the generator's <c>sound</c>).
    /// The sound moves the brightness inside <see cref="FlashGuard.MaxAudioBrightnessSwing"/>, never more; with amount 0 the view is
    /// the one the scene drew before it listened.</summary>
    public static ReactiveView Of(ReactiveSettings o, in ReactiveNumbers n, double seconds, EffectSurge surge, in ReactiveSound sound)
    {
        ArgumentNullException.ThrowIfNull(o);
        var speed = n.Speed * (1 + surge.Speed * 0.9) * (1 - Math.Clamp(surge.Slow, 0, 0.9));
        return new ReactiveView(
            Time: seconds * speed,
            Depth: Math.Clamp(n.Depth + surge.Zoom * 0.25, 0, 3),
            Symmetry: o.Symmetry,
            Angle: seconds * n.Rotation * Math.Tau * 0.1 + surge.Rotate * Math.Tau,
            Level: sound.Level,
            Low: sound.Low,
            Amount: sound.Amount,
            // A sting's glow lifts the scene inside a bound that cannot become a flash; its own flash channel
            // is the thing that may, and that goes through the guard. The sound swings it inside the guard's audio bound.
            Brightness: FlashGuard.Brightness((float)n.Brightness * (1 + surge.Glow * 0.5f), sound.Low, sound.Amount));
    }
}

/// <summary>The numbers of a reactive scene a binding may move, read every frame.</summary>
public readonly record struct ReactiveNumbers(double Speed, double Depth, double Rotation, double Brightness)
{
    public static ReactiveNumbers Of(ReactiveSettings o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new(o.Speed, o.Depth, o.Rotation, o.Brightness);
    }
}

/// <summary>What a scene hears at one instant: the sound's level and bass (0–1, from the node's modulation bus; 0.5 = neutral when
/// the node hears nothing) and how far it may move the scene (0–1).</summary>
public readonly record struct ReactiveSound(float Level, float Low, float Amount)
{
    public static readonly ReactiveSound None = new(0, 0, 0);

    /// <summary>The bus sources a scene listens to.</summary>
    public const string LevelSource = "audio:level";
    public const string BassSource = "audio:bass";
}

/// <summary>The scenes' arithmetic, written once here for the CPU and mirrored line for line in the shaders.
/// Every scene is one closed-form sample per pixel — no loops, no feedback, no state. Coordinates: the
/// pixel's place with the centre at 0 and the picture height at 1, so a wide picture sees more of the scene.</summary>
public static class ReactiveField
{
    /// <summary>The scene's value at a point, 0–1, before the palette.</summary>
    public static double Sample(ReactiveScene scene, double px, double py, in ReactiveView v)
    {
        var cos = Math.Cos(v.Angle);
        var sin = Math.Sin(v.Angle);
        var x = px * cos - py * sin;
        var y = px * sin + py * cos;
        var t = v.Time;
        var r = Math.Sqrt(x * x + y * y);
        var a = Math.Atan2(y, x);

        switch (scene)
        {
            case ReactiveScene.Tunnel:
            {
                var depth = 0.15 + v.Depth * 0.35;
                var spokes = Math.Sin(a * v.Symmetry + t * 0.4) * 0.06 * v.Depth;
                return Fract(depth / Math.Max(r, 0.02) - t * 0.35 + spokes);
            }
            case ReactiveScene.Kaleidoscope:
            {
                var wedge = Math.Tau / Math.Max(2, v.Symmetry);
                var folded = Math.Abs(Fract(a / wedge + 0.5) - 0.5) * wedge;
                return Plasma(Math.Cos(folded) * r * 5, Math.Sin(folded) * r * 5, t, v.Depth);
            }
            case ReactiveScene.Pulse:
            {
                var rings = 4 + v.Low * v.Amount * 5;
                var edge = Math.Sin((r * rings - t * 0.5) * Math.Tau) * 0.5 + 0.5;
                return Math.Clamp(Math.Pow(edge, 1.5 + v.Depth * 2), 0, 1);
            }
            case ReactiveScene.Vortex:
            {
                var twist = a + r * (2 + v.Depth * 8) + t * 0.25;
                return Fract(twist / Math.Tau * v.Symmetry * 0.5 + Math.Sin(r * 6 - t * 0.4) * 0.15);
            }
            case ReactiveScene.StarWarp:
            {
                var spokes = Math.Max(2, v.Symmetry) * 6;
                var lane = Fract(a / Math.Tau * spokes);
                var seed = Math.Floor(a / Math.Tau * spokes);
                var along = Fract(Math.Log(Math.Max(r, 0.015)) * 0.7 + t * 0.5 + Hash(seed));
                var streak = along * along * along;
                var across = 1 - Math.Min(1, Math.Abs(lane - 0.5) * 6);
                return Math.Clamp(streak * Math.Max(0, across) * (0.55 + r * 1.2), 0, 1);
            }
            default:
                return Plasma(x * 6, y * 6, t, v.Depth);
        }
    }

    private static double Plasma(double x, double y, double t, double depth)
    {
        var warp = 0.5 + depth;
        var s = Math.Sin(x + t)
                + Math.Sin(y * 0.9 - t * 0.7)
                + Math.Sin((x + y) * 0.6 * warp + t * 0.4)
                + Math.Sin(Math.Sqrt(x * x + y * y) * 1.3 * warp - t * 1.1);
        return Fract(s * 0.125 + 0.5);
    }

    private static double Hash(double n) => Fract(Math.Sin(n * 12.9898) * 43758.5453);

    /// <summary>The fractional part, always 0–1.</summary>
    public static double Fract(double v)
    {
        var f = v - Math.Floor(v);
        return f < 0 ? f + 1 : f;
    }

    /// <summary>The palette at a point of the cycle: the colours in order, wrapping, mixed smoothly
    /// (smoothstep), then taken up or down by the brightness.</summary>
    public static ColorRgb Map(double v, IReadOnlyList<ColorRgb> colors, float brightness)
    {
        ArgumentNullException.ThrowIfNull(colors);
        var n = colors.Count;
        if (n == 0)
            return ColorRgb.Black;
        if (n == 1)
            return colors[0].Scaled(brightness).Clamped();
        var pos = Fract(v) * n;
        var i = (int)Math.Floor(pos) % n;
        var j = (i + 1) % n;
        var k = pos - Math.Floor(pos);
        k = k * k * (3 - 2 * k);
        return ColorRgb.Lerp(colors[i], colors[j], (float)k).Scaled(brightness).Clamped();
    }
}
