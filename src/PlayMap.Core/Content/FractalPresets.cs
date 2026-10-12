namespace PlayMap.Core.Content;

/// <summary>One fractal scene: its family (the page's row), its name and the view, depth, motion and palette
/// it sets. Patterns' scenes, so a look named on one desk reads the same on the other.</summary>
public sealed record FractalScene(
    string Category,
    string Name,
    FractalKind Kind,
    double Zoom,
    double CenterX,
    double CenterY,
    int Iterations,
    double Speed,
    double JuliaRe,
    double JuliaIm,
    string Colors);

/// <summary>The fractal scenes, filed by family.</summary>
public static class FractalPresets
{
    public static readonly IReadOnlyList<FractalScene> Scenes =
    [
        // Mandelbrot: the map of every Julia set, and the places on its coast people know by name.
        new("Mandelbrot", "Mandelbrot classic", FractalKind.Mandelbrot, 1, -0.6, 0, 96, 0.5, -0.72, 0.27, "#0B0C2A,#1E3A8A,#3EC1F3,#FFFFFF,#FFB020"),
        new("Mandelbrot", "Seahorse valley", FractalKind.Mandelbrot, 60, -0.745, 0.1, 200, 0.3, -0.72, 0.27, "#050510,#3A0CA3,#F72585,#FFD166,#FFFFFF"),
        new("Mandelbrot", "Elephant valley", FractalKind.Mandelbrot, 45, 0.275, 0.007, 200, 0.3, -0.72, 0.27, "#0A1F1A,#0F766E,#2EE68A,#FDE68A,#FFFFFF"),
        new("Mandelbrot", "Spiral arm", FractalKind.Mandelbrot, 300, -0.7453, 0.1127, 300, 0.25, -0.72, 0.27, "#10002B,#5A189A,#FF6EC7,#FFD6F5,#FFFFFF"),
        new("Mandelbrot", "Mini-brot", FractalKind.Mandelbrot, 60, -1.7548, 0, 200, 0.3, -0.72, 0.27, "#0B0C2A,#1E3A8A,#6E9BFF,#DDE7FF,#FFB020"),
        new("Mandelbrot", "Triple spiral valley", FractalKind.Mandelbrot, 25, -0.088, 0.654, 220, 0.3, -0.72, 0.27, "#1A0000,#7F1D1D,#F97316,#FDE68A,#FFFFFF"),

        // Julia: one set at a time, chosen by c — the constants people draw first.
        new("Julia", "Julia swirl", FractalKind.Julia, 1.1, 0, 0, 120, 0.5, -0.72, 0.27, "#10002B,#5A189A,#C77DFF,#FFFFFF,#F72585"),
        new("Julia", "Julia dragon", FractalKind.Julia, 1.2, 0, 0, 160, 0.4, -0.8, 0.156, "#03071E,#0077B6,#48CAE4,#CAF0F8,#FFB703"),
        new("Julia", "Douady's rabbit", FractalKind.Julia, 1.1, 0, 0, 160, 0.4, -0.123, 0.745, "#0B0C2A,#3A0CA3,#B18CFF,#FFFFFF,#FFC24D"),
        new("Julia", "Dendrite", FractalKind.Julia, 1.1, 0, 0, 160, 0.3, 0, 1, "#02111B,#0B3954,#35E0D0,#E0FFFA,#FFFFFF"),
        new("Julia", "San Marco", FractalKind.Julia, 1.1, 0, 0, 160, 0.4, -0.75, 0, "#1A0A00,#7F3F1D,#FFB020,#FFE8B0,#FFFFFF"),
        new("Julia", "Siegel disk", FractalKind.Julia, 1.1, 0, 0, 200, 0.3, -0.391, -0.587, "#050510,#2B1055,#F03EAE,#FFD1EC,#FFFFFF"),
        new("Julia", "Galaxy spiral", FractalKind.Julia, 1.1, 0, 0, 200, 0.4, 0.285, 0.01, "#000814,#001D3D,#3EC1F3,#FFFFFF,#FFC24D"),

        // Burning ship: the same iteration with the signs folded — hulls, masts and a fleet down the real axis.
        new("Burning ship", "Burning ship", FractalKind.BurningShip, 1, -0.5, -0.5, 96, 0.3, -0.72, 0.27, "#000000,#7F1D1D,#F97316,#FDE68A,#FFFFFF"),
        new("Burning ship", "The armada", FractalKind.BurningShip, 12, -1.7, -0.03, 200, 0.25, -0.72, 0.27, "#02111B,#0B3954,#FF9E58,#FFE0C2,#FFFFFF"),
        new("Burning ship", "Ship's mast", FractalKind.BurningShip, 60, -1.7548, -0.0281, 240, 0.2, -0.72, 0.27, "#0B0C2A,#5A189A,#FF5C7A,#FFD6DE,#FFFFFF"),

        // Newton: the plane coloured by which root of z³ − 1 the method finds, and how long it took.
        new("Newton", "Newton triad", FractalKind.Newton, 1, 0, 0, 40, 0.4, -0.72, 0.27, "#3EC1F3,#F03EAE,#FFB020"),
        new("Newton", "Newton coast", FractalKind.Newton, 5, -0.5, 0, 60, 0.3, -0.72, 0.27, "#0B0C2A,#3EC1F3,#FFFFFF"),
        new("Newton", "Newton lace", FractalKind.Newton, 0.5, 0, 0, 48, 0.4, -0.72, 0.27, "#1A0033,#F03EAE,#7CF5C8"),

        // Domain warp: flowing noise folded into itself — a palette and a pace make the scene.
        new("Domain warp", "Domain warp lava", FractalKind.DomainWarp, 1, 0, 0, 32, 0.8, -0.72, 0.27, "#1A0000,#7F1D1D,#F97316,#FDE68A,#FFFFFF"),
        new("Domain warp", "Domain warp ocean", FractalKind.DomainWarp, 1, 0, 0, 32, 0.6, -0.72, 0.27, "#02111B,#0B3954,#087E8B,#BFD7EA,#FFFFFF"),
        new("Domain warp", "Domain warp smoke", FractalKind.DomainWarp, 0.7, 0, 0, 32, 0.4, -0.72, 0.27, "#050505,#2B2B2B,#6B6B6B,#B5B5B5,#F2F2F2"),
        new("Domain warp", "Domain warp aurora", FractalKind.DomainWarp, 1.4, 0, 0, 32, 0.5, -0.72, 0.27, "#020B12,#0B3D2E,#2EE68A,#7CF5C8,#B18CFF"),
        new("Domain warp", "Domain warp neon", FractalKind.DomainWarp, 1, 0, 0, 32, 0.9, -0.72, 0.27, "#0B0C2A,#3EC1F3,#F03EAE,#FFB020,#FFFFFF"),
    ];

    /// <summary>Every scene name, in the page's order.</summary>
    public static readonly IReadOnlyList<string> Names = [.. Scenes.Select(s => s.Name)];

    /// <summary>The families in the order the page shows them.</summary>
    public static readonly IReadOnlyList<string> Categories = [.. Scenes.Select(s => s.Category).Distinct()];

    /// <summary>A scene by name (case-insensitive), or null.</summary>
    public static FractalScene? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        foreach (var s in Scenes)
            if (s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }
}
