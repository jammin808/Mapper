using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>Placement, pure: a hybrid CPU's critical pool on its highest efficiency class and the background on the rest; one class
/// sets nothing and says so; CPUSETS OFF sets nothing and says so; the roles take their pool's sets.</summary>
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
}
