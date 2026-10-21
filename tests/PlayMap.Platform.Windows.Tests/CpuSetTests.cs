using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>Placement, pure: a hybrid CPU's critical pool on its highest efficiency class and the background on the rest; one class
/// sets nothing and says so; CPUSETS OFF sets nothing and says so; the roles take their pool's sets; ONE-CACHE confines the critical
/// pool to a dual-CCD CPU's first last-level cache and leaves a single-cache CPU's placement as AUTO's, or nothing to separate.</summary>
public sealed class CpuSetTests
{
    /// <summary>8 performance cores (class 1, ids 256–263) and 16 efficiency cores (class 0, ids 264–279), as Windows numbers them.</summary>
    private static List<CpuSetInfo> Hybrid() =>
    [
        .. Enumerable.Range(0, 8).Select(i => new CpuSetInfo((uint)(256 + i), i, 1)),
        .. Enumerable.Range(8, 16).Select(i => new CpuSetInfo((uint)(256 + i), i, 0)),
    ];

    [Fact]
    public void AHybridCpusCriticalPoolIsOnItsPerformanceCoresAndTheBackgroundOnTheRest()
    {
        var placement = CpuSetPlan.Place(Hybrid(), MachineSettings.Default);
        Assert.True(placement.Separates);
        Assert.Equal(Enumerable.Range(256, 8).Select(i => (uint)i), placement.Critical);
        Assert.Equal(Enumerable.Range(264, 16).Select(i => (uint)i), placement.Background);
        Assert.Equal(placement.Critical, placement.Of(PlatformPolicy.For(ThreadRole.Render).Pool));
        Assert.Equal(placement.Critical, placement.Of(PlatformPolicy.For(ThreadRole.Decode).Pool));
        Assert.Equal(placement.Background, placement.Of(PlatformPolicy.For(ThreadRole.Background).Pool));
        Assert.Empty(placement.Of(PlatformPolicy.For(ThreadRole.Audio).Pool));
        Assert.Equal([8, 16], CpuSetPlan.ByEfficiencyClass(Hybrid()).ToArray());
    }

    [Fact]
    public void OneEfficiencyClassSetsNothingAndSaysSo()
    {
        var one = Enumerable.Range(0, 32).Select(i => new CpuSetInfo((uint)(256 + i), i, 0)).ToList();
        var placement = CpuSetPlan.Place(one, MachineSettings.Default);
        Assert.False(placement.Separates);
        Assert.Empty(placement.Critical);
        Assert.Empty(placement.Background);
        Assert.Equal(CpuSetPlan.OneClassWords, placement.Words);
        Assert.Equal([32], CpuSetPlan.ByEfficiencyClass(one).ToArray());
    }

    [Fact]
    public void CpuSetsOffOrUnreadSetNothingAndSaySo()
    {
        var off = CpuSetPlan.Place(Hybrid(), MachineSettings.Default with { CpuSets = "off" });
        Assert.False(off.Separates);
        Assert.Equal(CpuSetPlan.OffWords, off.Words);
        var unread = CpuSetPlan.Place([], MachineSettings.Default);
        Assert.False(unread.Separates);
        Assert.Equal("the CPU sets were not read: no CPU set is chosen", unread.Words);
    }

    /// <summary>A dual-CCD CPU: one efficiency class, 2 × 8 logical processors, each CCD its own last-level cache (ids 256–263 on
    /// cache 0, 264–271 on cache 8, as Windows numbers the cache by its first processor).</summary>
    private static List<CpuSetInfo> DualCcd() =>
    [
        .. Enumerable.Range(0, 8).Select(i => new CpuSetInfo((uint)(256 + i), i, 0, 0)),
        .. Enumerable.Range(8, 8).Select(i => new CpuSetInfo((uint)(256 + i), i, 0, 8)),
    ];

    [Fact]
    public void OneCacheConfinesTheCriticalPoolToADualCcdCpusFirstCacheAndAutoLeavesItAlone()
    {
        // AUTO: one efficiency class, nothing to separate — the default leaves a dual-CCD CPU to Windows.
        var auto = CpuSetPlan.Place(DualCcd(), MachineSettings.Default);
        Assert.False(auto.Separates);
        Assert.Equal(CpuSetPlan.OneClassWords, auto.Words);

        var one = CpuSetPlan.Place(DualCcd(), MachineSettings.Default with { CpuSets = "one-cache" });
        Assert.True(one.Separates);
        Assert.Equal(Enumerable.Range(256, 8).Select(i => (uint)i), one.Critical);
        Assert.Equal(Enumerable.Range(264, 8).Select(i => (uint)i), one.Background);
        Assert.Equal("the critical threads on the 8 logical processors of the highest efficiency class's first last-level cache, the background on the other 8", one.Words);
        // The roles take the confined pool as they take AUTO's; MMCSS and the priorities are unchanged.
        Assert.Equal(PlatformPolicy.For(ThreadRole.Render), PlatformPolicy.For(ThreadRole.Render, MachineSettings.Default with { CpuSets = "one-cache" }));
    }

    [Fact]
    public void OneCacheOnASingleCacheCpuIsAutosPlacementOrNothingToSeparate()
    {
        // A hybrid CPU's cores share one cache: the performance cores are the critical pool, as under AUTO.
        var hybrid = CpuSetPlan.Place(Hybrid(), MachineSettings.Default with { CpuSets = "one-cache" });
        var auto = CpuSetPlan.Place(Hybrid(), MachineSettings.Default);
        Assert.Equal(auto.Critical.ToArray(), hybrid.Critical.ToArray());
        Assert.Equal(auto.Background.ToArray(), hybrid.Background.ToArray());
        // One class on one cache: nothing to separate, said.
        var single = Enumerable.Range(0, 16).Select(i => new CpuSetInfo((uint)(256 + i), i, 0)).ToList();
        var none = CpuSetPlan.Place(single, MachineSettings.Default with { CpuSets = "one-cache" });
        Assert.False(none.Separates);
        Assert.Equal(CpuSetPlan.OneCacheDomainWords, none.Words);
        Assert.True(PlatformPolicy.TrySet(MachineSettings.Default, "CPUSETS", "ONE-CACHE", false, out var next, out _));
        Assert.Equal("one-cache", next.CpuSets);
    }
}
