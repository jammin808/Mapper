using System.Collections.Immutable;
using System.Globalization;

namespace PlayMap.Core.Machine;

/// <summary>One CPU set as Windows lists it (<c>GetSystemCpuSetInformation</c>): its id, the logical processor it is, its efficiency
/// class (higher is more performant; every set of a CPU without hybrid cores has class 0) and its last-level cache's index (the L3
/// domain: a dual-CCD CPU has two).</summary>
public readonly record struct CpuSetInfo(uint Id, int LogicalProcessor, int EfficiencyClass, int LastLevelCache = 0);

/// <summary>Where the critical and background threads are placed: the critical pool's CPU set ids, the background pool's, and the
/// words. Both empty on a CPU with one efficiency class, said: there is nothing to separate.</summary>
public sealed record CpuPlacement(ImmutableArray<uint> Critical, ImmutableArray<uint> Background, string Words)
{
    public static CpuPlacement None(string words) => new([], [], words);

    /// <summary>Whether any pool is chosen.</summary>
    public bool Separates => !Critical.IsEmpty;

    /// <summary>A pool's CPU set ids (empty for <see cref="CpuPool.None"/>).</summary>
    public ImmutableArray<uint> Of(CpuPool pool) => pool switch
    {
        CpuPool.Critical => Critical,
        CpuPool.Background => Background,
        _ => [],
    };
}

/// <summary>The placement rule, pure: the critical pool (render, present, decode, capture) on the highest efficiency class, the
/// background pools on every other class, by CPU sets (soft: Windows may still run a thread elsewhere, never a hard affinity). A CPU
/// with one efficiency class has nothing to separate, and nothing is set. Under ONE-CACHE (off by default until the lab measures it,
/// L10) the critical pool is confined further, to the highest class's first last-level cache domain (a dual-CCD CPU's first CCD), and
/// the background takes every other set.</summary>
public static class CpuSetPlan
{
    public const string OneClassWords = "this CPU has one efficiency class: there is nothing to separate, so no CPU set is chosen";

    public const string OneCacheDomainWords =
        "this CPU's highest efficiency class shares one last-level cache with every other set: there is nothing to separate, so no CPU set is chosen";

    public const string OffWords = "CPU sets are OFF on this node: no CPU set is chosen";

    public static CpuPlacement Place(IReadOnlyList<CpuSetInfo> sets, MachineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.CpuSets == "off")
            return CpuPlacement.None(OffWords);
        if (sets.Count == 0)
            return CpuPlacement.None("the CPU sets were not read: no CPU set is chosen");
        var top = sets.Max(s => s.EfficiencyClass);
        if (settings.CpuSets == "one-cache")
        {
            var cache = sets.Where(s => s.EfficiencyClass == top).Min(s => s.LastLevelCache);
            ImmutableArray<uint> confined = [.. sets.Where(s => s.EfficiencyClass == top && s.LastLevelCache == cache).Select(s => s.Id).Order()];
            ImmutableArray<uint> rest = [.. sets.Where(s => s.EfficiencyClass != top || s.LastLevelCache != cache).Select(s => s.Id).Order()];
            if (rest.IsEmpty)
                return CpuPlacement.None(OneCacheDomainWords);
            return new CpuPlacement(confined, rest, string.Create(CultureInfo.InvariantCulture,
                $"the critical threads on the {confined.Length} logical processors of the highest efficiency class's first last-level cache, the background on the other {rest.Length}"));
        }
        if (sets.All(s => s.EfficiencyClass == top))
            return CpuPlacement.None(OneClassWords);
        ImmutableArray<uint> critical = [.. sets.Where(s => s.EfficiencyClass == top).Select(s => s.Id).Order()];
        ImmutableArray<uint> background = [.. sets.Where(s => s.EfficiencyClass != top).Select(s => s.Id).Order()];
        return new CpuPlacement(critical, background, string.Create(CultureInfo.InvariantCulture,
            $"the critical threads on the {critical.Length} logical processors of the highest efficiency class, the background on the other {background.Length}"));
    }

    /// <summary>How many logical processors each efficiency class holds, the highest class first.</summary>
    public static ImmutableArray<int> ByEfficiencyClass(IReadOnlyList<CpuSetInfo> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        return [.. sets.GroupBy(s => s.EfficiencyClass).OrderByDescending(g => g.Key).Select(g => g.Count())];
    }
}
