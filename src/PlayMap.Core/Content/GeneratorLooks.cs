using PlayMap.Core.Model;

namespace PlayMap.Core.Content;

/// <summary>A curated look: a name, the kind it is for, the pack it is filed in, and the parameters it sets (the rest are the
/// kind's defaults). The particle field's and the 2D fractals' looks are their preset tables, recalled through their
/// <c>preset</c> parameter.</summary>
public sealed record GeneratorLook(string Name, string Kind, string Pack, ParamSet Params);

/// <summary>Every curated look, by kind: thirty for the GPU particles, the raymarched fractals and the reactive scene, and the particle
/// presets and fractal scenes the older kinds already carry. Recalling a look writes its parameters whole (one undo step).</summary>
public static class GeneratorLooks
{
    private static ParamSet P(params (string Name, object Value)[] values)
    {
        var set = ParamSet.Empty;
        foreach (var (name, value) in values)
            set = set.With(name, value switch
            {
                double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                _ => value.ToString() ?? "",
            });
        return set;
    }

    /// <summary>The looks of the round-2 kinds, curated.</summary>
    public static readonly IReadOnlyList<GeneratorLook> Curated =
    [
        // ---- Particle flow ----
        new("Ink in water", "particles.flow", "Flow", P(("emitter", "disc"), ("size", 0.4), ("curl", 1.4), ("curlScale", 1.5), ("drag", 1.2),
            ("colors", "#0B1F3A,#1E3A8A,#3EC1F3,#E0F2FF"), ("glow", false), ("opacity", 0.6), ("sizeOverLife", "grow"), ("lifeMax", 6.0))),
        new("Embers", "particles.flow", "Fire", P(("emitter", "bottom"), ("direction", 270.0), ("spread", 30.0), ("speedMin", 0.1), ("speedMax", 0.3),
            ("gravity", -0.05), ("curl", 0.8), ("colors", "#FFD166,#FF8C42,#D62828,#3D0000"), ("colorBy", "life"), ("sizeOverLife", "shrink"))),
        new("Galaxy swirl", "particles.flow", "Cosmic", P(("emitter", "ring"), ("size", 0.8), ("vortex", 0.6), ("curl", 0.3), ("drag", 0.2),
            ("colors", "#10002B,#7B2FF7,#F72585,#FFFFFF"), ("colorBy", "speed"), ("lifeMax", 8.0), ("lifeMin", 4.0))),
        new("Snowfall", "particles.flow", "Weather", P(("emitter", "top"), ("direction", 90.0), ("spread", 20.0), ("gravity", 0.02), ("curl", 0.4),
            ("curlScale", 3.0), ("colors", "#FFFFFF,#DDE7FF"), ("glow", false), ("sizeMin", 2.0), ("sizeMax", 5.0), ("sizeOverLife", "constant"),
            ("lifeMax", 8.0), ("lifeMin", 5.0))),
        new("Fireflies", "particles.flow", "Nature", P(("count", 4096), ("emitter", "area"), ("curl", 0.5), ("curlSpeed", 0.5),
            ("sizeOverLife", "pulse"), ("colors", "#E9FF70,#B5E48C,#52B788"), ("sizeMin", 3.0), ("sizeMax", 6.0), ("sprite", "bokeh"))),
        new("Pollen drift", "particles.flow", "Nature", P(("emitter", "left"), ("direction", 0.0), ("spread", 40.0), ("wind", 0.03),
            ("curl", 0.9), ("colors", "#FFF3B0,#E09F3E,#9E2A2B"), ("glow", false), ("opacity", 0.7))),
        new("Starburst", "particles.flow", "Cosmic", P(("emitter", "point"), ("speedMin", 0.2), ("speedMax", 0.6), ("drag", 0.8), ("curl", 0.2),
            ("colors", "#FFFFFF,#3EC1F3,#7B2FF7"), ("colorBy", "speed"), ("sprite", "streak"), ("lifeMax", 3.0), ("lifeMin", 1.0))),
        new("Rain", "particles.flow", "Weather", P(("emitter", "top"), ("direction", 100.0), ("spread", 4.0), ("speedMin", 0.8), ("speedMax", 1.2),
            ("gravity", 0.5), ("curl", 0.0), ("drag", 0.0), ("colors", "#9DB4C0,#C2DFE3"), ("glow", false), ("sprite", "streak"),
            ("sizeOverLife", "constant"), ("lifeMax", 2.0), ("lifeMin", 1.0))),

        // ---- Particle picture ----
        new("Dissolve", "particles.image", "Picture", P(("flow", 0.2), ("curl", 0.6), ("drag", 0.4), ("sizeMin", 1.0), ("sizeMax", 2.0), ("glow", false))),
        new("Pixel rain", "particles.image", "Picture", P(("gravity", 0.3), ("curl", 0.0), ("flow", 0.0), ("sprite", "square"), ("glow", false),
            ("sizeOverLife", "constant"), ("lifeMax", 3.0), ("lifeMin", 1.5))),
        new("Embers off the picture", "particles.image", "Fire", P(("gravity", -0.08), ("curl", 0.9), ("colorBy", "life"),
            ("colors", "#FFD166,#FF8C42,#D62828,#3D0000"), ("weight", "edges"), ("threshold", 0.1))),
        new("Contour glow", "particles.image", "Picture", P(("flow", 1.2), ("curl", 0.1), ("weight", "edges"), ("threshold", 0.15), ("glow", true),
            ("sizeMin", 1.0), ("sizeMax", 2.5))),
        new("Sand", "particles.image", "Picture", P(("gravity", 0.15), ("drag", 1.5), ("curl", 0.2), ("glow", false), ("sizeOverLife", "constant"),
            ("sizeMin", 1.0), ("sizeMax", 1.5), ("lifeMax", 6.0))),

        // ---- Mandelbulb ----
        new("Classic bulb", "fractal.mandelbulb", "Bulbs", P(("power", 8.0))),
        new("Power four", "fractal.mandelbulb", "Bulbs", P(("power", 4.0), ("distance", 2.4), ("colors", "#10002B,#5A189A,#FF6EC7,#FFD6F5,#FFFFFF"))),
        new("Golden bulb", "fractal.mandelbulb", "Bulbs", P(("power", 6.0), ("colors", "#1A0A00,#7F3F1D,#FFB020,#FFE8B0,#FFFFFF"), ("glow", 0.8))),
        new("Deep blue", "fractal.mandelbulb", "Bulbs", P(("power", 10.0), ("fog", 0.5), ("colors", "#000814,#001D3D,#3EC1F3,#FFFFFF,#0077B6"))),

        // ---- Mandelbox ----
        new("Folded city", "fractal.mandelbox", "Boxes", P(("scale", -1.8))),
        new("Crystal cave", "fractal.mandelbox", "Boxes", P(("scale", 2.0), ("distance", 4.0), ("colors", "#02111B,#0B3954,#35E0D0,#E0FFFA,#FFFFFF"))),
        new("Inverted box", "fractal.mandelbox", "Boxes", P(("scale", -2.5), ("minRadius", 0.3), ("colors", "#1A0000,#7F1D1D,#F97316,#FDE68A,#FFFFFF"))),
        new("Lattice", "fractal.mandelbox", "Boxes", P(("scale", 1.5), ("foldLimit", 0.8), ("distance", 5.0), ("glow", 1.0))),

        // ---- KIFS ----
        new("Sierpinski", "fractal.kifs", "Folds", P(("fold", "tetra"), ("twist", 0.0))),
        new("Octahedral jewel", "fractal.kifs", "Folds", P(("fold", "octa"), ("twist", 0.3), ("colors", "#10002B,#7B2FF7,#F72585,#FFD6F5,#FFFFFF"))),
        new("Menger sponge", "fractal.kifs", "Folds", P(("fold", "menger"), ("scale", 3.0), ("twist", 0.0), ("iterations", 5), ("distance", 4.0))),
        new("Twisting spire", "fractal.kifs", "Folds", P(("fold", "tetra"), ("twist", 0.6), ("scale", 1.8), ("height", 1.2))),
        new("Coral", "fractal.kifs", "Folds", P(("fold", "octa"), ("scale", 1.6), ("offsetY", 0.6), ("colors", "#03071E,#F72585,#FF8FAB,#FFE5EC,#FFFFFF"))),

        // ---- Reactive scene ----
        new("Club tunnel", "reactive.scene", "Club", P(("scene", "tunnel"), ("speed", 1.6), ("sound", 0.8), ("colors", "#FF2D95,#7B2FF7,#00D4FF"))),
        new("Kaleido bloom", "reactive.scene", "Club", P(("scene", "kaleidoscope"), ("symmetry", 8), ("sound", 0.6), ("colors", "#FFD166,#EF476F,#118AB2"))),
        new("Pulse rings", "reactive.scene", "Club", P(("scene", "pulse"), ("sound", 1.0), ("depth", 0.6), ("colors", "#00D4FF,#FFFFFF,#7B2FF7"))),
        new("Star warp", "reactive.scene", "Space", P(("scene", "starwarp"), ("speed", 1.4), ("sound", 0.4), ("colors", "#FFFFFF,#3EC1F3,#7B2FF7"))),
    ];

    /// <summary>Every curated look of a kind: the curated table's, or — for a kind with a <c>preset</c> parameter — its preset table's,
    /// as a look that sets that parameter.</summary>
    public static IEnumerable<GeneratorLook> For(string kindId)
    {
        ArgumentNullException.ThrowIfNull(kindId);
        if (kindId.Equals("particles.field", StringComparison.OrdinalIgnoreCase))
            return ParticlePresets.All.Select(p => new GeneratorLook(p.Name, "particles.field", p.Category, ParamSet.Empty.With("preset", p.Name)));
        if (GeneratorCatalog.Find(kindId) is { Family: "fractal" } kind && kind.Param("preset") is not null)
            return FractalPresets.Scenes.Where(s => FractalSettings.KindOf(kindId) == s.Kind)
                .Select(s => new GeneratorLook(s.Name, kind.Id, s.Category, ParamSet.Empty.With("preset", s.Name)));
        return Curated.Where(l => l.Kind.Equals(kindId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A kind's curated look by name (case-insensitive), or null.</summary>
    public static GeneratorLook? Find(string kindId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return For(kindId).FirstOrDefault(l => l.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Any kind's curated look by name, or null (the first in catalogue order).</summary>
    public static GeneratorLook? FindAny(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return GeneratorCatalog.All.SelectMany(k => For(k.Id)).FirstOrDefault(l => l.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
