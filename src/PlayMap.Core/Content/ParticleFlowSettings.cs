using PlayMap.Core.Model;

namespace PlayMap.Core.Content;

/// <summary>Where a GPU particle field's births come from.</summary>
public enum FlowEmitter { Area, Point, Line, Ring, Disc, Top, Bottom, Left, Right, Edges, Picture }

/// <summary>What happens to a particle at the picture's edge.</summary>
public enum FlowEdges { Pass, Bounce, Wrap, Kill }

/// <summary>How a particle's size runs over its life.</summary>
public enum FlowSizeOverLife { Constant, Grow, Shrink, Pulse }

/// <summary>What a particle's colour is chosen by.</summary>
public enum FlowColorBy { Life, Speed, Random }

/// <summary>A GPU particle field's settings that fix its shape (the slots, the schedule and the emitter): a change re-anchors the
/// field. The forces and the look are numbers the renderer reads every frame (and a binding may move). The field lives in picture
/// heights: y from 0 at the top to 1 at the bottom, x from 0 to the picture's aspect.</summary>
public sealed record ParticleFlowSettings(int Count, uint Seed, FlowEmitter Emitter, double LifeMinSeconds, double LifeMaxSeconds,
    FlowEdges Edges, FlowSizeOverLife SizeOverLife, FlowColorBy ColorBy, ParticleShape Shape, bool Glow, string Colors, string Background)
{
    public const string KindId = "particles.flow";

    public const int DefaultCount = 262_144;
    public const int MaxCount = 4_194_304;

    /// <summary>The longest life the catalogue's default allows: the horizon a field converges within.</summary>
    public const double DefaultLifeMaxSeconds = 4;

    /// <summary>The count a software rasteriser draws (llvmpipe): a 64 k field steps in milliseconds there.</summary>
    public const int SoftwareCount = 65_536;

    public const int PaletteColors = 5;

    /// <summary>The shortest life in steps.</summary>
    public int LifeMinSteps => Math.Max(1, (int)Math.Round(LifeMinSeconds * ParticleSchedule.StepsPerSecond));

    /// <summary>The longest life in steps: the field converges within this many steps of any start.</summary>
    public int LifeMaxSteps => Math.Max(LifeMinSteps, (int)Math.Round(LifeMaxSeconds * ParticleSchedule.StepsPerSecond));

    /// <summary>The state textures' side: the smallest square that holds every slot.</summary>
    public static int Side(int count) => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, count))));

    public ColorRgb[] Palette()
    {
        var all = ColorRgb.ParseList(Colors, ColorRgb.White);
        return all.Length <= PaletteColors ? all : all[..PaletteColors];
    }

    /// <summary>The settings a parameter set gives, the count capped at <paramref name="maxCount"/>.</summary>
    public static ParticleFlowSettings Resolve(ParamSet set, int maxCount = MaxCount)
    {
        ArgumentNullException.ThrowIfNull(set);
        var kind = GeneratorCatalog.Find(KindId)!;
        var lifeMin = ParamValues.Number(set, kind, "lifeMin");
        var lifeMax = Math.Max(lifeMin, ParamValues.Number(set, kind, "lifeMax"));
        return new ParticleFlowSettings(
            Math.Clamp(ParamValues.Integer(set, kind, "count"), 1, Math.Max(1, maxCount)),
            unchecked((uint)ParamValues.Integer(set, kind, "seed")),
            Enum.Parse<FlowEmitter>(ParamValues.Choice(set, kind, "emitter"), ignoreCase: true),
            lifeMin,
            lifeMax,
            Enum.Parse<FlowEdges>(ParamValues.Choice(set, kind, "edges"), ignoreCase: true),
            Enum.Parse<FlowSizeOverLife>(ParamValues.Choice(set, kind, "sizeOverLife"), ignoreCase: true),
            Enum.Parse<FlowColorBy>(ParamValues.Choice(set, kind, "colorBy"), ignoreCase: true),
            Enum.Parse<ParticleShape>(ParamValues.Choice(set, kind, "sprite"), ignoreCase: true),
            ParamValues.Flag(set, kind, "glow"),
            ParamValues.Text(set, kind, "colors"),
            ParamValues.Text(set, kind, "background"));
    }
}
