namespace PlayMap.Core.Content;

/// <summary>One curated particle scene: its pack, its name and the values it sets (everything but the
/// background and the seed, which are the operator's). Spin is in turns a second.</summary>
public sealed record ParticlePreset(
    string Category,
    string Name,
    ParticleEmitter Emitter,
    ParticleShape Shape,
    int Count,
    double SizeMin,
    double SizeMax,
    double SpeedMin,
    double SpeedMax,
    double DirectionDeg,
    double SpreadDeg,
    double GravityY,
    double WindX,
    double Wobble,
    double SpinTurns,
    bool Glow,
    string Colors);

/// <summary>Patterns' 34 particle scenes in eight packs — the classic seven first, then awards, modern,
/// nature, moods, star cloths, night skies and feel-good — so a scene named on one desk reads the same here.</summary>
public static class ParticlePresets
{
    private const ParticleEmitter Top = ParticleEmitter.TopEdge;
    private const ParticleEmitter Bottom = ParticleEmitter.BottomEdge;
    private const ParticleEmitter Center = ParticleEmitter.Center;
    private const ParticleEmitter Area = ParticleEmitter.FullArea;
    private const ParticleShape Circle = ParticleShape.Circle;
    private const ParticleShape Square = ParticleShape.Square;
    private const ParticleShape Star = ParticleShape.Star;
    private const ParticleShape Streak = ParticleShape.Streak;
    private const ParticleShape Bokeh = ParticleShape.Bokeh;

    public static readonly IReadOnlyList<ParticlePreset> All =
    [
        // Classic — the seven the studio has always had.
        new("Classic", "Snow", Top, Circle, 900, 1.5, 5, 30, 95, 90, 18, 8, 12, 0.7, 0, false, "#FFFFFF,#E8F2FF"),
        new("Classic", "Confetti", Top, Square, 900, 3, 8, 90, 240, 90, 50, 60, 25, 0.8, 1.4, false, "#FF4D6D,#FFD166,#06D6A0,#4CC9F0,#B388FF,#FFFFFF"),
        new("Classic", "Starfield", Center, Circle, 1600, 1, 2.6, 40, 160, 0, 360, 0, 0, 0, 0, true, "#FFFFFF,#BBDDFF,#FFEECC"),
        new("Classic", "Rain", Top, Streak, 1400, 5, 11, 900, 1400, 83, 4, 240, -40, 0, 0, false, "#9FBFDF,#7FA8CC"),
        new("Classic", "Bokeh", Area, Bokeh, 90, 20, 90, 4, 22, 0, 360, -4, 6, 0.5, 0, true, "#FFB020,#F03EAE,#3EC1F3,#FFE28A"),
        new("Classic", "Embers", Bottom, Circle, 500, 1.5, 4.5, 50, 140, -90, 40, -55, 18, 0.9, 0, true, "#FF6B35,#FFA630,#FFD97D,#FF3E1D"),
        new("Classic", "Fireflies", Area, Bokeh, 160, 2, 7, 6, 30, 0, 360, 0, 0, 1, 0, true, "#D7FF6E,#A8FF3E,#FFFFAA"),

        // Awards — gold, sparkle, champagne.
        new("Awards", "Gold dust", Area, Bokeh, 700, 1.5, 4, 6, 24, -90, 360, -3, 4, 0.6, 0, true, "#FFD700,#FFE9A8,#FFB300,#FFF6D5"),
        new("Awards", "Champagne", Bottom, Circle, 600, 1.5, 5, 60, 160, -90, 25, -40, 6, 0.5, 0, true, "#FFF3C4,#FFE082,#FFFFFF"),
        new("Awards", "Red-carpet sparkle", Area, Star, 220, 3, 9, 4, 18, 0, 360, 0, 3, 0.4, 0.6, true, "#FFD700,#FF4D6D,#FFFFFF,#FFB300"),
        new("Awards", "Ticker tape", Top, Streak, 500, 6, 14, 80, 200, 90, 30, 40, 30, 0.9, 1.0, false, "#FFD700,#FFFFFF,#C0C0C0"),

        // Modern — clean, cool, technical.
        new("Modern", "Data stream", Bottom, Streak, 900, 4, 10, 300, 700, -90, 3, -80, 0, 0, 0, true, "#3EC1F3,#7FE7FF,#FFFFFF"),
        new("Modern", "Pixel dust", Area, Square, 1200, 1, 3, 8, 40, 0, 360, 0, 6, 0.2, 0, true, "#FFFFFF,#9FB6FF,#5C7CFF"),
        new("Modern", "Neon rain", Top, Streak, 700, 5, 12, 500, 900, 90, 6, 120, 0, 0, 0, true, "#F03EAE,#3EC1F3,#B388FF"),
        new("Modern", "Grid motes", Area, Square, 260, 4, 8, 10, 30, 0, 360, 0, 0, 0.3, 0.2, false, "#3EC1F3,#FFFFFF"),

        // Nature — seasons and gardens.
        new("Nature", "Autumn leaves", Top, Square, 380, 6, 14, 40, 110, 90, 40, 20, 35, 1, 0.8, false, "#D2691E,#FF8C00,#B22222,#DAA520,#8B4513"),
        new("Nature", "Cherry blossom", Top, Circle, 600, 3, 8, 30, 90, 90, 35, 6, 22, 0.9, 0, false, "#FFC0CB,#FFB7C5,#FFFFFF,#FF9EB5"),
        new("Nature", "Dandelion seeds", Area, Bokeh, 140, 6, 16, 15, 50, 0, 60, -4, 30, 1, 0, false, "#FFFFFF,#F4F4F4"),
        new("Nature", "Pollen", Area, Bokeh, 400, 2, 5, 6, 26, 0, 360, -2, 8, 0.8, 0, true, "#FFE066,#FFF2A8,#FFD23F"),

        // Moods — slow, soft, large.
        new("Moods", "Calm", Area, Bokeh, 60, 30, 110, 3, 12, 0, 360, 0, 2, 0.4, 0, true, "#3E6FF3,#6BA4FF,#A8D0FF"),
        new("Moods", "Warm glow", Area, Bokeh, 80, 24, 90, 4, 16, -90, 360, -3, 3, 0.5, 0, true, "#FFB020,#FF7A3D,#FFD98A"),
        new("Moods", "Dusk", Area, Bokeh, 110, 16, 70, 5, 20, 0, 360, 0, 5, 0.5, 0, true, "#7B4DFF,#FF6F91,#FFB86B"),
        new("Moods", "Deep blue", Area, Circle, 500, 1, 4, 5, 25, 0, 360, -3, 0, 0.7, 0, true, "#1E5AA8,#3EC1F3,#9FE1FF"),

        // Starcloth — a field that stays put and shimmers, like the drape behind a band.
        new("Starcloth", "Starcloth", Area, Circle, 1400, 0.8, 2.2, 0, 2, 0, 360, 0, 0, 0.15, 0, true, "#FFFFFF,#E8F0FF,#FFF4DA"),
        new("Starcloth", "Starcloth blue", Area, Circle, 1200, 0.8, 2.2, 0, 2, 0, 360, 0, 0, 0.15, 0, true, "#BFD8FF,#7FB4FF,#FFFFFF"),
        new("Starcloth", "Starcloth dense", Area, Circle, 3000, 0.6, 1.8, 0, 2, 0, 360, 0, 0, 0.12, 0, true, "#FFFFFF,#EEF3FF"),

        // Night sky — slow stars and the odd streak.
        new("Night sky", "Night sky", Area, Circle, 900, 0.8, 2.6, 1, 6, 0, 360, 0, 2, 0.2, 0, true, "#FFFFFF,#CFE0FF,#FFE9C4"),
        new("Night sky", "Shooting stars", Top, Streak, 24, 3, 6, 900, 1600, 25, 10, 0, 0, 0, 0, true, "#FFFFFF,#DDEEFF"),
        new("Night sky", "Milky drift", Area, Bokeh, 300, 2, 7, 2, 8, 0, 360, 0, 3, 0.3, 0, true, "#E6EEFF,#FFFFFF,#CBD7FF"),

        // Feel-good — parties, bubbles, sparklers.
        new("Feel-good", "Party confetti", Top, Star, 800, 3, 9, 100, 260, 90, 60, 70, 20, 0.9, 1.8, false, "#FF4D6D,#FFD166,#06D6A0,#4CC9F0,#B388FF,#FF8C42"),
        new("Feel-good", "Bubbles", Bottom, Bokeh, 260, 6, 22, 40, 120, -90, 30, -25, 10, 1, 0, false, "#BFEFFF,#E0F7FF,#FFFFFF"),
        new("Feel-good", "Fireworks", Center, Star, 900, 1.5, 4, 120, 320, 0, 360, 0, 0, 0, 2, true, "#FF4D6D,#FFD166,#4CC9F0,#B388FF,#FFFFFF"),
        new("Feel-good", "Sparkler", Center, Circle, 700, 1, 3, 160, 360, 0, 360, 0, 0, 0, 0, true, "#FFFFFF,#FFE27A,#FFB020,#FF6B35"),
        new("Feel-good", "Sunshine", Area, Bokeh, 120, 10, 40, 6, 20, -90, 360, -2, 4, 0.5, 0, true, "#FFF176,#FFD54F,#FFFFFF"),
    ];

    /// <summary>Every scene name, classic first.</summary>
    public static readonly IReadOnlyList<string> Names = [.. All.Select(p => p.Name)];

    /// <summary>The pack names in the order the page shows them.</summary>
    public static readonly IReadOnlyList<string> Categories = [.. All.Select(p => p.Category).Distinct()];

    /// <summary>A scene by name (case-insensitive), or null.</summary>
    public static ParticlePreset? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        foreach (var p in All)
            if (p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }
}
