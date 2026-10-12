using System.Collections.Frozen;
using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Content;

public enum ParamType
{
    Number,
    Integer,
    Bool,
    Choice,
    /// <summary>"#RRGGBB".</summary>
    Color,
    /// <summary>Comma-separated "#RRGGBB" list.</summary>
    Colors,
    Text,
}

/// <summary>One parameter of a generator kind: its type, range, default and the words the inspector and
/// HELP show. Values travel as invariant strings in a <see cref="ParamSet"/>.</summary>
public sealed record ParamSpec(string Name, ParamType Type, string Default, double Min, double Max, IReadOnlyList<string> Choices, string Words)
{
    public static ParamSpec Num(string name, double def, double min, double max, string words) =>
        new(name, ParamType.Number, def.ToString("R", CultureInfo.InvariantCulture), min, max, [], words);

    public static ParamSpec Int(string name, int def, int min, int max, string words) =>
        new(name, ParamType.Integer, def.ToString(CultureInfo.InvariantCulture), min, max, [], words);

    public static ParamSpec Flag(string name, bool def, string words) =>
        new(name, ParamType.Bool, def ? "true" : "false", 0, 1, [], words);

    public static ParamSpec Pick(string name, string def, string words, params string[] choices) =>
        new(name, ParamType.Choice, def, 0, 0, choices, words);

    public static ParamSpec Col(string name, string def, string words) =>
        new(name, ParamType.Color, def, 0, 0, [], words);

    public static ParamSpec Cols(string name, string def, string words) =>
        new(name, ParamType.Colors, def, 0, 0, [], words);

    public static ParamSpec Txt(string name, string def, string words) =>
        new(name, ParamType.Text, def, 0, 0, [], words);

    /// <summary>Validates and normalises a value; the reason when it is refused.</summary>
    public bool TryNormalise(string? value, out string normalised, out string why)
    {
        normalised = Default;
        why = "";
        if (value is null)
        {
            why = $"{Name} needs a value";
            return false;
        }
        var v = value.Trim();
        switch (Type)
        {
            case ParamType.Number:
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                {
                    why = $"{Name} is a number";
                    return false;
                }
                normalised = Math.Clamp(d, Min, Max).ToString("R", CultureInfo.InvariantCulture);
                return true;
            case ParamType.Integer:
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var di) || !double.IsFinite(di))
                {
                    why = $"{Name} is a whole number";
                    return false;
                }
                normalised = ((long)Math.Clamp(Math.Round(di), Min, Max)).ToString(CultureInfo.InvariantCulture);
                return true;
            case ParamType.Bool:
                if (v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1")
                    normalised = "true";
                else if (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0")
                    normalised = "false";
                else
                {
                    why = $"{Name} is ON or OFF";
                    return false;
                }
                return true;
            case ParamType.Choice:
                var match = Choices.FirstOrDefault(c => c.Equals(v, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    why = $"{Name} is one of {string.Join(", ", Choices)}";
                    return false;
                }
                normalised = match;
                return true;
            case ParamType.Color:
                if (!ColorText.TryParse(v, out var rgb))
                {
                    why = $"{Name} is a colour, #RRGGBB";
                    return false;
                }
                normalised = ColorText.Format(rgb);
                return true;
            case ParamType.Colors:
                var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var list = new List<string>();
                foreach (var p in parts)
                {
                    if (!ColorText.TryParse(p, out var c))
                    {
                        why = $"{Name} is a list of colours, #RRGGBB,#RRGGBB";
                        return false;
                    }
                    list.Add(ColorText.Format(c));
                }
                if (list.Count == 0)
                {
                    why = $"{Name} needs at least one colour";
                    return false;
                }
                normalised = string.Join(',', list);
                return true;
            default:
                normalised = v.Length > 256 ? v[..256] : v;
                return true;
        }
    }
}

/// <summary>A generator kind: a family ("pattern", "fractal", "particles", "reactive", "effect") and a name,
/// its parameters, and whether it moves (a still pattern renders once per change; a moving one every frame).</summary>
public sealed record GeneratorKind(string Id, string Family, string Name, string Words, bool Animated, IReadOnlyList<ParamSpec> Params)
{
    public ParamSpec? Param(string name) => Params.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A parameter's value with the default filled in.</summary>
    public string Value(ParamSet set, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set.Get(name) ?? Param(name)?.Default ?? "";
    }

    public ParamSet Defaults() => Params.Aggregate(ParamSet.Empty, (s, p) => s.With(p.Name, p.Default));
}

/// <summary>Every generator PlayMap draws. Patterns' test patterns, fractals, particles and reactive scenes
/// (the same names and ranges, so a look reads the same in both apps) plus the mapping kinds PlayMap adds:
/// projector identification, convergence, a UV checker and surface-aware effects that use the stage's 3D.</summary>
public static class GeneratorCatalog
{
    private static readonly string[] Curves = ["linear", "cosine", "scurve", "gamma22"];

    public static readonly IReadOnlyList<GeneratorKind> All =
    [
        // ---- Test patterns (Patterns' set) ----
        new("pattern.testcard", "pattern", "Test card", "The rig card: rulers, one-to-one fields, gamma match, levels, identity", false,
        [
            ParamSpec.Pick("variant", "rig", "Rig (layout), Pixel (one-to-one at 2×) or Levels (staircase, clip, gamma)", "rig", "pixel", "levels"),
            ParamSpec.Flag("identity", true, "Show the output's name and resolution"),
            ParamSpec.Flag("mark", true, "Show the PlayMap mark"),
            ParamSpec.Txt("label", "", "A line of your own on the card"),
        ]),
        new("pattern.grid", "pattern", "Grid", "Pixel-exact lines symmetric about the centre", false,
        [
            ParamSpec.Int("cell", 64, 2, 4096, "Cell size in pixels"),
            ParamSpec.Int("subdivisions", 4, 0, 16, "Minor lines per cell"),
            ParamSpec.Int("line", 1, 1, 8, "Line width in pixels"),
            ParamSpec.Flag("centerCross", true, "The centre cross"),
            ParamSpec.Flag("border", true, "The raster's border"),
            ParamSpec.Flag("diagonals", false, "Corner-to-corner diagonals"),
            ParamSpec.Col("color", "#FFFFFF", "Line colour"),
            ParamSpec.Col("background", "#000000", "Background"),
        ]),
        new("pattern.checker", "pattern", "Checkerboard", "Squares; animated it swaps to read LED refresh and latency", true,
        [
            ParamSpec.Int("cell", 64, 1, 4096, "Square size in pixels"),
            ParamSpec.Flag("animate", false, "Swap the colours on an interval"),
            ParamSpec.Num("interval", 0.5, 0.05, 60, "Seconds between swaps"),
        ]),
        new("pattern.bars", "pattern", "Colour bars", "SMPTE RP 219, EBU and full-height bars with PLUGE", false,
        [
            ParamSpec.Pick("variant", "smpte", "Which bars", "smpte", "ebu100", "bars75", "bars100"),
            ParamSpec.Flag("fullRange", true, "Stretch narrow-range 16–235 to 0–255"),
        ]),
        new("pattern.ramp", "pattern", "Ramp", "Grey and RGB gradients, or stepped wedges with their code values", false,
        [
            ParamSpec.Pick("variant", "grayH", "Which ramp", "grayH", "grayV", "rgb", "steps"),
            ParamSpec.Int("steps", 16, 2, 256, "Wedges in the stepped ramp"),
            ParamSpec.Flag("markers", true, "Ticks every 10 %"),
        ]),
        new("pattern.focus", "pattern", "Focus", "Siemens star, line pairs and type samples for focus and resolution", false,
        [
            ParamSpec.Flag("star", true, "The Siemens star"),
            ParamSpec.Flag("linePairs", true, "1–4 px line pairs in each corner"),
            ParamSpec.Flag("text", true, "Type samples"),
        ]),
        new("pattern.geometry", "pattern", "Geometry", "Circles, safe areas and aspect markers for roundness and framing", false,
        [
            ParamSpec.Flag("circles", true, "Centre and corner circles"),
            ParamSpec.Flag("safeAreas", true, "Action 93 % and title 90 %"),
            ParamSpec.Flag("aspect", false, "4:3, 1:1 and 2.39 markers"),
            ParamSpec.Flag("crosshair", true, "Centre crosshair"),
            ParamSpec.Flag("diagonals", false, "Diagonals"),
        ]),
        new("pattern.flat", "pattern", "Flat field", "One colour at a level, for uniformity and black level", false,
        [
            ParamSpec.Col("color", "#FFFFFF", "Colour"),
            ParamSpec.Num("level", 100, 0, 100, "Level in percent"),
            ParamSpec.Flag("border", false, "A 1-px border"),
        ]),
        new("pattern.ledwall", "pattern", "LED wall", "Tiles with numbering and borders for LED processors", false,
        [
            ParamSpec.Int("tileWidth", 128, 8, 1024, "Tile width in pixels"),
            ParamSpec.Int("tileHeight", 128, 8, 1024, "Tile height in pixels"),
            ParamSpec.Pick("numbering", "rowcol", "Tile numbering", "rowcol", "linear", "serpentine"),
            ParamSpec.Flag("borders", true, "Tile borders"),
            ParamSpec.Flag("diagonals", false, "Tile diagonals"),
        ]),
        new("pattern.videowall", "pattern", "Video wall", "Displays and bezels for monitor walls", false,
        [
            ParamSpec.Int("columns", 3, 1, 64, "Displays across"),
            ParamSpec.Int("rows", 3, 1, 64, "Displays down"),
            ParamSpec.Int("bezel", 0, 0, 512, "Bezel compensation in pixels"),
            ParamSpec.Flag("numbers", true, "Display numbers"),
        ]),
        new("pattern.blend", "pattern", "Projection blend", "Hue-coded projector frames, overlaps and opposing ramps for edge blends", false,
        [
            ParamSpec.Int("projectors", 2, 2, 12, "Projectors across"),
            ParamSpec.Int("rows", 1, 1, 6, "Rows"),
            ParamSpec.Int("overlap", 240, 0, 4096, "Overlap in pixels"),
            ParamSpec.Pick("curve", "cosine", "Blend curve", Curves),
            ParamSpec.Flag("hue", true, "Hue-code each projector"),
            ParamSpec.Flag("grayCheck", false, "Flat 50 % grey to expose a doubled join"),
        ]),
        new("pattern.motion", "pattern", "Motion", "Moving bar, bouncing box, frame flash, zone plate, scrolling grid", true,
        [
            ParamSpec.Pick("variant", "bar", "Which motion test", "bar", "box", "flash", "zoneplate", "scroll"),
            ParamSpec.Num("speed", 480, 1, 10000, "Pixels a second"),
            ParamSpec.Int("pxPerFrame", 0, 0, 64, "Pixels a frame instead (judder test; 0 = off)"),
            ParamSpec.Flag("vertical", false, "Move vertically"),
            ParamSpec.Flag("fps", true, "Show the frame rate and counter"),
        ]),
        new("pattern.colorcycle", "pattern", "Colour cycle", "The full field through a list of colours", true,
        [
            ParamSpec.Num("interval", 2, 0.1, 60, "Seconds per colour"),
            ParamSpec.Flag("fade", true, "Fade between colours"),
            ParamSpec.Cols("colors", "#FF0000,#00FF00,#0000FF,#FFFFFF", "The colours"),
        ]),
        new("pattern.syncmarks", "pattern", "Sync marks", "A white flash every 2 s on the network clock's grid: film two walls to read their offset", true,
        [
            ParamSpec.Num("period", 2, 0.5, 10, "Seconds between flashes"),
            ParamSpec.Int("flashMs", 50, 17, 500, "Flash length in milliseconds"),
        ]),

        // ---- Mapping patterns (PlayMap's own) ----
        new("pattern.identify", "pattern", "Identify", "The projector's number, name and colour, big — which lamp is which", false,
        [
            ParamSpec.Flag("grid", true, "A coarse grid behind"),
            ParamSpec.Flag("frame", true, "The raster's frame in the projector's colour"),
        ]),
        new("pattern.convergence", "pattern", "Convergence", "Red, green and blue crosshatch to read panel convergence and lens colour fringing", false,
        [
            ParamSpec.Int("spacing", 96, 8, 1024, "Line spacing in pixels"),
            ParamSpec.Pick("channels", "rgb", "Which channels", "rgb", "red", "green", "blue"),
        ]),
        new("pattern.uvchecker", "pattern", "UV checker", "Labelled squares (A1, B2…) that show how a surface's UVs land", false,
        [
            ParamSpec.Int("divisions", 8, 2, 64, "Squares across"),
            ParamSpec.Flag("labels", true, "Cell labels"),
        ]),

        // ---- Fractals (Patterns' families and presets) ----
        new("fractal.mandelbrot", "fractal", "Mandelbrot", "The Mandelbrot set, smooth-coloured, slowly breathing", true, FractalParams(-0.6, 0)),
        new("fractal.julia", "fractal", "Julia", "A Julia set whose constant drifts", true, FractalParams(0, 0)),
        new("fractal.burningship", "fractal", "Burning ship", "The burning ship fractal", true, FractalParams(-0.5, -0.5)),
        new("fractal.newton", "fractal", "Newton", "Newton's method on z³ − 1, coloured by root", true, FractalParams(0, 0)),
        new("fractal.warp", "fractal", "Domain warp", "Folded noise: lava, ocean, smoke, aurora", true, FractalParams(0, 0)),

        // ---- Particles (Patterns' simulation and presets) ----
        new("particles.field", "particles", "Particles", "A deterministic particle field: snow, confetti, starfield, embers…", true,
        [
            ParamSpec.Txt("preset", "Snow", "A preset name (its values fill the rest)"),
            ParamSpec.Int("count", 800, 1, 20000, "Particles"),
            ParamSpec.Pick("emitter", "top", "Where they come from", "top", "bottom", "center", "area"),
            ParamSpec.Pick("shape", "circle", "Sprite shape", "circle", "square", "star", "streak", "bokeh"),
            ParamSpec.Num("sizeMin", 2, 0.5, 200, "Smallest size in pixels"),
            ParamSpec.Num("sizeMax", 6, 0.5, 400, "Largest size in pixels"),
            ParamSpec.Num("speedMin", 30, 0, 4000, "Slowest speed, pixels a second"),
            ParamSpec.Num("speedMax", 90, 0, 4000, "Fastest speed"),
            ParamSpec.Num("direction", 90, -360, 360, "Direction in degrees (90 = down)"),
            ParamSpec.Num("spread", 20, 0, 360, "Spread in degrees"),
            ParamSpec.Num("gravity", 0, -2000, 2000, "Gravity, pixels a second²"),
            ParamSpec.Num("wind", 0, -2000, 2000, "Wind, pixels a second²"),
            ParamSpec.Num("wobble", 0.3, 0, 5, "Side-to-side wobble"),
            ParamSpec.Num("rotation", 0, -1440, 1440, "Spin, degrees a second"),
            ParamSpec.Flag("glow", false, "Add light (additive blend)"),
            ParamSpec.Cols("colors", "#FFFFFF", "Particle colours"),
            ParamSpec.Col("background", "#000000", "Background"),
            ParamSpec.Int("seed", 20260829, 0, int.MaxValue, "Seed: the same seed draws the same field on every node"),
        ]),

        // ---- Reactive scenes (Patterns' set) ----
        new("reactive.scene", "reactive", "Reactive scene", "Plasma, tunnel, kaleidoscope, pulse, vortex, star warp", true,
        [
            ParamSpec.Pick("scene", "plasma", "Which scene", "plasma", "tunnel", "kaleidoscope", "pulse", "vortex", "starwarp"),
            ParamSpec.Num("speed", 1, 0, 3, "Speed"),
            ParamSpec.Num("depth", 1, 0, 3, "Depth"),
            ParamSpec.Int("symmetry", 6, 2, 16, "Kaleidoscope symmetry"),
            ParamSpec.Num("rotation", 0.2, -3, 3, "Rotation"),
            ParamSpec.Num("brightness", 1, 0.2, 1.6, "Brightness"),
            ParamSpec.Cols("colors", "#FF2D95,#7B2FF7,#00D4FF", "Palette"),
        ]),

        // ---- Surface-aware effects: they read the stage's 3D (position, normal, edges), so they land on the
        //      real object the way Lightform's did, on any mesh, scanned or imported ----
        new("effect.sweep", "effect", "Sweep", "A band of light that sweeps through the world along a direction", true,
        [
            ParamSpec.Num("speed", 0.5, 0.01, 20, "Metres a second"),
            ParamSpec.Num("width", 0.2, 0.01, 10, "Band width in metres"),
            ParamSpec.Pick("axis", "x", "Sweep direction", "x", "y", "z", "radial"),
            ParamSpec.Col("color", "#FFFFFF", "Band colour"),
            ParamSpec.Col("background", "#000000", "Background"),
        ]),
        new("effect.outline", "effect", "Outline", "Glowing edges where faces fold and where surfaces end", true,
        [
            ParamSpec.Num("width", 0.02, 0.001, 0.5, "Edge width (UV units)"),
            ParamSpec.Num("pulse", 0.5, 0, 5, "Pulses a second (0 = steady)"),
            ParamSpec.Col("color", "#00D4FF", "Edge colour"),
        ]),
        new("effect.relight", "effect", "Relight", "A virtual light that moves round the object, shading it by its real normals", true,
        [
            ParamSpec.Num("speed", 0.2, 0, 5, "Turns a minute"),
            ParamSpec.Num("height", 0.6, -1, 1, "Light elevation"),
            ParamSpec.Num("ambient", 0.1, 0, 1, "Ambient light"),
            ParamSpec.Col("color", "#FFE6B0", "Light colour"),
        ]),
    ];

    private static readonly FrozenDictionary<string, GeneratorKind> ById =
        All.ToFrozenDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase);

    public static GeneratorKind? Find(string? id) => id is not null && ById.TryGetValue(id, out var k) ? k : null;

    /// <summary>Short names a wire line may use: "testcard" for "pattern.testcard", "julia" for "fractal.julia".</summary>
    public static GeneratorKind? FindLoose(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Find(name) ?? All.FirstOrDefault(k => k.Id.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ParamSpec> FractalParams(double cx, double cy) =>
    [
        ParamSpec.Txt("preset", "", "A preset name (its values fill the rest)"),
        ParamSpec.Num("zoom", 1, 0.2, 1e6, "Zoom"),
        ParamSpec.Num("centerX", cx, -4, 4, "Centre, real part"),
        ParamSpec.Num("centerY", cy, -4, 4, "Centre, imaginary part"),
        ParamSpec.Int("iterations", 160, 8, 1024, "Iterations"),
        ParamSpec.Num("speed", 1, 0, 5, "Animation speed"),
        ParamSpec.Num("juliaRe", -0.8, -2, 2, "Julia constant, real part"),
        ParamSpec.Num("juliaIm", 0.156, -2, 2, "Julia constant, imaginary part"),
        ParamSpec.Cols("colors", "#000764,#206BCB,#EDFFFF,#FFAA00,#000200", "Palette (2–5 colours)"),
    ];
}

/// <summary>"#RRGGBB" text ↔ packed 0xRRGGBB.</summary>
public static class ColorText
{
    public static bool TryParse(string? text, out int rgb)
    {
        rgb = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        if (t.StartsWith('#'))
            t = t[1..];
        if (t.Length == 3)
            t = string.Concat(t.Select(c => new string(c, 2)));
        return t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    public static string Format(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    public static (float R, float G, float B) ToFloats(int rgb) =>
        (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
}
