using System.Globalization;
using System.Text;

namespace PlayMap.Core.Modulation;

/// <summary>What a source name reads.</summary>
public enum ModSourceKind
{
    Level, Bass, Mid, High, Peak, Flux, Centroid, Onset, Band, Fft,
    Beat, Bar, Phrase, BeatPulse, BarPulse, Bpm,
    Lfo,
}

/// <summary>An LFO's shape: one cycle as x goes 0→1.</summary>
public enum LfoShape { Sine, Triangle, Saw, Ramp, Square, Pulse, Random }

/// <summary>A parsed source name. Audio: "audio:level|bass|low|mid|high|peak|flux|centroid|onset", "audio:band/&lt;0-15&gt;",
/// "audio:fft/&lt;0-63&gt;", each with "/n" for its auto-gained twin where it has one. The beat clock: "beat" (phase in the beat),
/// "bar", "phrase", "beat:pulse[/&lt;ms&gt;]", "bar:pulse[/&lt;ms&gt;]", "bpm". LFOs: "lfo:&lt;shape&gt;/&lt;period&gt;[b][/&lt;phase&gt;]"
/// with the period in seconds, or in beats with "b". Any source may end in "~&lt;attack ms&gt;[/&lt;release ms&gt;]" (smoothing).
/// <see cref="Canonical"/> is the name written the one way it is kept.</summary>
public sealed record ModSource(ModSourceKind Kind, int Index, bool Normalised, LfoShape Shape, double Period, bool PeriodInBeats,
    double Phase, double PulseMs, double AttackMs, double ReleaseMs)
{
    public const int Bands = 16;
    public const int FftBins = 64;

    /// <summary>A pulse's decay when the name gives none.</summary>
    public const double DefaultPulseMs = 100;

    /// <summary>An onset envelope's decay.</summary>
    public const double OnsetDecayMs = 120;

    public bool IsAudio => Kind <= ModSourceKind.Fft;

    public bool IsSmoothed => AttackMs > 0 || ReleaseMs > 0;

    /// <summary>Pure (a function of show or beat time) or live (this node's sound).</summary>
    public SourceDeterminism Determinism => IsAudio ? SourceDeterminism.Live : SourceDeterminism.Pure;

    /// <summary>The name without its smoothing.</summary>
    public string BaseName => Kind switch
    {
        ModSourceKind.Band => $"audio:band/{Num(Index)}{(Normalised ? "/n" : "")}",
        ModSourceKind.Fft => $"audio:fft/{Num(Index)}",
        ModSourceKind.Beat => "beat",
        ModSourceKind.Bar => "bar",
        ModSourceKind.Phrase => "phrase",
        ModSourceKind.BeatPulse => PulseMs == DefaultPulseMs ? "beat:pulse" : $"beat:pulse/{Num(PulseMs)}",
        ModSourceKind.BarPulse => PulseMs == DefaultPulseMs ? "bar:pulse" : $"bar:pulse/{Num(PulseMs)}",
        ModSourceKind.Bpm => "bpm",
        ModSourceKind.Lfo => $"lfo:{Shape.ToString().ToLowerInvariant()}/{Num(Period)}{(PeriodInBeats ? "b" : "")}{(Phase == 0 ? "" : "/" + Num(Phase))}",
        _ => $"audio:{Kind.ToString().ToLowerInvariant()}{(Normalised ? "/n" : "")}",
    };

    /// <summary>The name as kept: its base and its smoothing.</summary>
    public string Canonical => !IsSmoothed ? BaseName
        : ReleaseMs == AttackMs ? $"{BaseName}~{Num(AttackMs)}" : $"{BaseName}~{Num(AttackMs)}/{Num(ReleaseMs)}";

    /// <summary>The same source with this smoothing.</summary>
    public ModSource WithSmoothing(double attackMs, double releaseMs) => this with { AttackMs = attackMs, ReleaseMs = releaseMs };

    /// <summary>Reads a source name; the reason when it is not one.</summary>
    public static bool TryParse(string? name, out ModSource source, out string why)
    {
        source = None;
        why = "";
        var text = (name ?? "").Trim();
        if (text.Length == 0)
        {
            why = "no source given — MOD SOURCES lists them";
            return false;
        }
        double attack = 0, release = 0;
        var tilde = text.IndexOf('~', StringComparison.Ordinal);
        if (tilde >= 0)
        {
            if (!TrySmoothing(text[(tilde + 1)..], out attack, out release))
            {
                why = $"'{text[tilde..]}' is not a smoothing: ~<attack ms>[/<release ms>], 0 to 10000";
                return false;
            }
            text = text[..tilde];
        }
        var lower = text.ToLowerInvariant();
        var ok = lower.StartsWith("audio:", StringComparison.Ordinal) ? TryAudio(lower[6..], out source)
            : lower.StartsWith("lfo:", StringComparison.Ordinal) ? TryLfo(lower[4..], out source)
            : TryBeat(lower, out source);
        if (!ok)
        {
            why = $"'{text}' is not a source this node has — MOD SOURCES lists them (audio:…, beat, bar, phrase, beat:pulse, bpm, lfo:…)";
            source = None;
            return false;
        }
        source = source.WithSmoothing(attack, release);
        return true;
    }

    private static readonly ModSource None = new(ModSourceKind.Level, 0, false, LfoShape.Sine, 0, false, 0, 0, 0, 0);

    private static bool TrySmoothing(string text, out double attack, out double release)
    {
        release = 0;
        var parts = text.Split('/');
        if (!Ms(parts[0], out attack) || parts.Length > 2)
            return false;
        if (parts.Length == 1)
        {
            release = attack;
            return true;
        }
        return Ms(parts[1], out release);
    }

    private static bool Ms(string s, out double ms) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out ms) && double.IsFinite(ms) && ms is >= 0 and <= 10000;

    private static bool TryAudio(string rest, out ModSource source)
    {
        source = None;
        var parts = rest.Split('/');
        var normalised = parts.Length > 1 && parts[^1] == "n";
        var count = normalised ? parts.Length - 1 : parts.Length;
        ModSourceKind kind;
        var index = 0;
        switch (parts[0])
        {
            case "level": kind = ModSourceKind.Level; break;
            case "bass" or "low": kind = ModSourceKind.Bass; break;
            case "mid": kind = ModSourceKind.Mid; break;
            case "high": kind = ModSourceKind.High; break;
            case "peak": kind = ModSourceKind.Peak; break;
            case "flux": kind = ModSourceKind.Flux; break;
            case "centroid": kind = ModSourceKind.Centroid; break;
            case "onset": kind = ModSourceKind.Onset; break;
            case "band" or "fft":
                kind = parts[0] == "band" ? ModSourceKind.Band : ModSourceKind.Fft;
                if (count != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out index)
                    || index >= (kind == ModSourceKind.Band ? Bands : FftBins))
                    return false;
                break;
            default: return false;
        }
        var takesIndex = kind is ModSourceKind.Band or ModSourceKind.Fft;
        if (!takesIndex && count != 1)
            return false;
        // Only Patterns' four numbers and the bands have auto-gained twins.
        if (normalised && kind is not (ModSourceKind.Level or ModSourceKind.Bass or ModSourceKind.Mid or ModSourceKind.High or ModSourceKind.Band))
            return false;
        source = None with { Kind = kind, Index = index, Normalised = normalised };
        return true;
    }

    private static bool TryBeat(string text, out ModSource source)
    {
        source = None;
        var parts = text.Split('/');
        var pulseMs = DefaultPulseMs;
        if (parts.Length == 2 && !Ms(parts[1], out pulseMs))
            return false;
        if (parts.Length > 2 || (parts.Length == 2 && pulseMs <= 0))
            return false;
        ModSourceKind? kind = parts[0] switch
        {
            "beat" when parts.Length == 1 => ModSourceKind.Beat,
            "bar" when parts.Length == 1 => ModSourceKind.Bar,
            "phrase" when parts.Length == 1 => ModSourceKind.Phrase,
            "bpm" when parts.Length == 1 => ModSourceKind.Bpm,
            "beat:pulse" => ModSourceKind.BeatPulse,
            "bar:pulse" => ModSourceKind.BarPulse,
            _ => null,
        };
        if (kind is not { } k)
            return false;
        source = None with { Kind = k, PulseMs = k is ModSourceKind.BeatPulse or ModSourceKind.BarPulse ? pulseMs : 0 };
        return true;
    }

    private static bool TryLfo(string rest, out ModSource source)
    {
        source = None;
        var parts = rest.Split('/');
        if (parts.Length is < 2 or > 3 || !Enum.TryParse<LfoShape>(parts[0], ignoreCase: true, out var shape)
            || int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return false;
        var periodText = parts[1];
        var beats = periodText.EndsWith('b');
        if (beats)
            periodText = periodText[..^1];
        if (!double.TryParse(periodText, NumberStyles.Float, CultureInfo.InvariantCulture, out var period) || !double.IsFinite(period)
            || period < 0.01 || period > 3600)
            return false;
        var phase = 0.0;
        if (parts.Length == 3 && (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out phase)
            || !double.IsFinite(phase) || phase < 0 || phase > 1))
            return false;
        source = None with { Kind = ModSourceKind.Lfo, Shape = shape, Period = period, PeriodInBeats = beats, Phase = phase % 1 };
        return true;
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A stable 64-bit hash of a name (FNV-1a over its UTF-8): the same on every machine and every run, so a random LFO
    /// draws the same values everywhere.</summary>
    public static ulong StableHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
