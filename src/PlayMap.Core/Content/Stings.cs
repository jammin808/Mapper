namespace PlayMap.Core.Content;

/// <summary>One sting a node fires: its preset name (Patterns' pulse names), pulse or score, the channels it drives and its
/// length when none is given.</summary>
public sealed record StingPreset(PulsePreset Preset, string Name, bool Scored, IReadOnlyList<string> Channels, int DefaultMs);

/// <summary>The sting presets <c>STING</c> fires and <c>STING LIST</c> lists: every <see cref="PulsePreset"/>, with the channels its
/// envelope drives. A sting runs 100–5000 ms.</summary>
public static class Stings
{
    public const int MinMs = 100;
    public const int MaxMs = 5000;

    public static readonly IReadOnlyList<StingPreset> All = [.. Enum.GetValues<PulsePreset>().Select(Make)];

    public static StingPreset? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        foreach (var s in All)
            if (s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    private static StingPreset Make(PulsePreset preset)
    {
        var w = EffectEnvelope.Weights(preset);
        var channels = new List<string>();
        void Add(string name, float v)
        {
            if (Math.Abs(v) > 0)
                channels.Add(name);
        }
        Add("burst", w.Burst);
        Add("speed", w.Speed);
        Add("glow", w.Glow);
        Add("zoom", w.Zoom);
        Add("flash", w.Flash);
        Add("hue", w.Hue);
        Add("lift", w.Lift);
        Add("swirl", w.Swirl);
        Add("gust", w.Gust);
        Add("scale", w.Scale);
        Add("ripple", w.Ripple);
        Add("shake", w.Shake);
        Add("slow", w.Slow);
        Add("rotate", w.Rotate);
        Add("morph", w.Morph);
        var scored = EffectEnvelope.IsScored(preset);
        return new StingPreset(preset, preset.ToString(), scored, channels, scored ? 2000 : 900);
    }
}
