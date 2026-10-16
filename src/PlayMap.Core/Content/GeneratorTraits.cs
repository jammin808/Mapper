namespace PlayMap.Core.Content;

/// <summary>How a generator's picture depends on time. <see cref="Pure"/>: a closed form of its time. <see cref="Bounded"/>: a
/// simulation whose state at a step depends only on the last <see cref="GeneratorTraits.MaxLifeSeconds"/> of steps, so two nodes
/// (or one after a seek) converge bit for bit once that horizon has run. <see cref="History"/>: it integrates frame to frame.
/// <see cref="Live"/>: it reads this node's own sound or camera, so a mirror does not draw the same.</summary>
public enum TimeClass { Pure, Bounded, History, Live }

/// <summary>What a kind is, beyond its parameters: its time class, its period (0 = none: the float precision warning applies), the
/// horizon a bounded kind converges within at its default lives, whether it reads the sound bus, whether it may flash, and whether
/// its renderer evaluates its bindings live on the render thread.</summary>
public sealed record GeneratorTraits(TimeClass Time, double PeriodSeconds, double MaxLifeSeconds, bool ReadsAudio, bool MayFlash, bool DrawsBindings)
{
    public static readonly GeneratorTraits Pure = new(TimeClass.Pure, 0, 0, false, false, false);

    /// <summary>A kind's traits (an unknown kind is pure and draws its bindings at their base).</summary>
    public static GeneratorTraits Of(string? kindId)
    {
        var id = kindId?.ToLowerInvariant() ?? "";
        return id switch
        {
            "particles.flow" or "particles.image" => new(TimeClass.Bounded, 0, ParticleFlowSettings.DefaultLifeMaxSeconds, false, true, true),
            // The CPU field restarts from its seed whenever a number changes, so a number rewritten every frame is not drawn live.
            "particles.field" => new(TimeClass.Bounded, 0, 0, false, true, false),
            "reactive.scene" => new(TimeClass.Pure, 0, 0, true, true, true),
            "fractal.mandelbulb" or "fractal.mandelbox" or "fractal.kifs" => new(TimeClass.Pure, 0, 0, true, true, true),
            "fractal.mandelbrot" or "fractal.julia" or "fractal.burningship" or "fractal.newton" or "fractal.warp" =>
                new(TimeClass.Pure, 0, 0, true, true, true),
            // The sync check counts on the leader's own grid: its period is the mark's.
            "pattern.syncmarks" => new(TimeClass.Pure, 2, 0, false, true, false),
            "pattern.motion" or "pattern.colorcycle" or "pattern.checker" => new(TimeClass.Pure, 0, 0, false, true, true),
            _ => Pure,
        };
    }

    /// <summary>The time class a generator shows: Live when it reads the sound and is told to listen (sound above 0 or a binding to a
    /// live source), else its kind's.</summary>
    public TimeClass With(bool listens) => listens ? TimeClass.Live : Time;

    public static string Word(TimeClass t) => t.ToString().ToLowerInvariant();

    /// <summary>True for the kinds that keep the leader's own clock whatever their origin: the sync marks flash on the leader's grid,
    /// which is what they are for.</summary>
    public static bool OnLeaderClock(string? kindId) => string.Equals(kindId, "pattern.syncmarks", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a number a kind reads once per change, never per frame (it fixes a field's schedule or a target's size), so
    /// a binding on it is drawn at its base: a particle field's lives, a raymarched fractal's render scale.</summary>
    public static bool ConfigureOnly(string? kindId, string param)
    {
        ArgumentNullException.ThrowIfNull(param);
        if (ParticleFlowSettings.IsKind(kindId))
            return param.Equals("lifeMin", StringComparison.OrdinalIgnoreCase) || param.Equals("lifeMax", StringComparison.OrdinalIgnoreCase);
        if (Fractal3DSettings.IsKind(kindId))
            return param.Equals("renderScale", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
