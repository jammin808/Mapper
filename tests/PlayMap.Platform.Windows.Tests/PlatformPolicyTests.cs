using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The claims' policy, pure on both jobs: the role → claims table, MMCSS and CPU sets switched off, the defaults with their
/// causes (on only where the cause is documented), and how MACHINE SET changes a setting — refused at its value, refused for a word or
/// value it does not take, REALTIME refused without the privilege, and every other change kept.</summary>
public sealed class PlatformPolicyTests
{
    [Fact]
    public void EachRoleClaimsWhatTheTableSays()
    {
        var table = Enum.GetValues<ThreadRole>().ToDictionary(r => r, PlatformPolicy.For);
        Assert.Equal((ThreadLevel.AboveNormal, "Games", CpuPool.Critical, false), Shape(table[ThreadRole.Render]));
        Assert.Equal((ThreadLevel.AboveNormal, "Games", CpuPool.Critical, false), Shape(table[ThreadRole.Present]));
        Assert.Equal((ThreadLevel.AboveNormal, "Capture", CpuPool.Critical, false), Shape(table[ThreadRole.Capture]));
        Assert.Equal((ThreadLevel.AboveNormal, (string?)null, CpuPool.Critical, false), Shape(table[ThreadRole.Decode]));
        Assert.Equal((ThreadLevel.AboveNormal, "Pro Audio", CpuPool.None, false), Shape(table[ThreadRole.Audio]));
        Assert.Equal((ThreadLevel.AboveNormal, "Pro Audio", CpuPool.None, false), Shape(table[ThreadRole.Timecode]));
        Assert.Equal((ThreadLevel.AboveNormal, (string?)null, CpuPool.None, false), Shape(table[ThreadRole.Control]));
        Assert.Equal((ThreadLevel.AboveNormal, (string?)null, CpuPool.None, false), Shape(table[ThreadRole.Led]));
        Assert.Equal((ThreadLevel.Normal, (string?)null, CpuPool.None, false), Shape(table[ThreadRole.Network]));
        Assert.Equal((ThreadLevel.BelowNormal, (string?)null, CpuPool.Background, true), Shape(table[ThreadRole.Background]));
        // No role is ever claimed above the highest priority, and none joins realtime.
        Assert.All(table.Values, c => Assert.True(c.Priority <= ThreadLevel.Highest));

        var off = MachineSettings.Default with { Mmcss = "off", CpuSets = "off" };
        var render = PlatformPolicy.For(ThreadRole.Render, off);
        Assert.Equal((ThreadLevel.AboveNormal, (string?)null, CpuPool.None, false), Shape(render));
        Assert.Equal(PlatformPolicy.For(ThreadRole.Audio), PlatformPolicy.For(ThreadRole.Audio, MachineSettings.Default));

        static (ThreadLevel, string?, CpuPool, bool) Shape(RoleClaims c) => (c.Priority, c.MmcssTask, c.Pool, c.EcoQos);
    }

    [Fact]
    public void TheDefaultsAreOnOnlyWhereTheirCauseIsDocumented()
    {
        Assert.Equal(["QOS", "TIMER", "MMCSS", "PROCESS", "CPUSETS", "GPU-PRIORITY", "GC", "AWAKE"], PlatformPolicy.Defaults.Select(d => d.Word));
        var settings = MachineSettings.Default;
        foreach (var d in PlatformPolicy.Defaults)
        {
            // The default is the first value and the one a fresh node's settings hold.
            Assert.Equal(d.Values[0], d.Default);
            Assert.Equal(d.Default, PlatformPolicy.ValueOf(settings, d.Word));
            Assert.False(string.IsNullOrWhiteSpace(d.Cause));
            Assert.Contains(d.Applies, new[] { "now", "next start", "next arm" });
        }
        // The GPU scheduling class and the GC's show mode have no documented cause: off until the lab measures them.
        var gpu = PlatformPolicy.Find("gpu-priority")!;
        Assert.Equal("normal", gpu.Default);
        Assert.StartsWith("none documented", gpu.Cause, StringComparison.Ordinal);
        var gc = PlatformPolicy.Find("GC")!;
        Assert.Equal(("default", "next arm"), (gc.Default, gc.Applies));
        Assert.StartsWith("none documented", gc.Cause, StringComparison.Ordinal);
        Assert.All(PlatformPolicy.Defaults.Where(d => d.Word is not ("GPU-PRIORITY" or "GC")), d => Assert.StartsWith("documented", d.Cause, StringComparison.Ordinal));
        Assert.Null(settings.Locked);
    }

    [Fact]
    public void ASettingAtItsValueIsRefusedWithWordsAndNothingChanges()
    {
        var settings = MachineSettings.Default;
        Assert.False(PlatformPolicy.TrySet(settings, "QOS", "HIGH", true, out var next, out var words));
        Assert.Equal("QoS is already high", words);
        Assert.Same(settings, next);
        Assert.False(PlatformPolicy.TrySet(settings, "gpu-priority", "normal", true, out _, out words));
        Assert.Equal("The GPU priority is already normal", words);
        Assert.False(PlatformPolicy.TrySet(settings, "AWAKE", "ON", true, out _, out words));
        Assert.Equal("Keep-awake is already on", words);
    }

    [Fact]
    public void RealtimeWithoutThePrivilegeIsRefusedNamingIt()
    {
        Assert.False(PlatformPolicy.TrySet(MachineSettings.Default, "GPU-PRIORITY", "REALTIME", false, out var next, out var words));
        Assert.Equal(PlatformPolicy.RealtimeWithoutPrivilegeWords, words);
        Assert.Contains("SeIncreaseBasePriorityPrivilege", words, StringComparison.Ordinal);
        Assert.Contains("installer", words, StringComparison.Ordinal);
        Assert.Equal("normal", next.GpuPriority);

        Assert.True(PlatformPolicy.TrySet(MachineSettings.Default, "GPU-PRIORITY", "REALTIME", true, out next, out words));
        Assert.Equal(("realtime", ""), (next.GpuPriority, words));
    }

    [Fact]
    public void AWordOrValueMachineSetDoesNotTakeIsRefusedWithItsSyntax()
    {
        Assert.False(PlatformPolicy.TrySet(MachineSettings.Default, "TURBO", "ON", true, out _, out var words));
        Assert.Equal(PlatformPolicy.Syntax, words);
        Assert.Equal("MACHINE SET QOS HIGH|AUTO | TIMER HIGH|DEFAULT | MMCSS ON|OFF | PROCESS HIGH|NORMAL | CPUSETS AUTO|OFF|ONE-CACHE | GPU-PRIORITY NORMAL|HIGH|REALTIME | GC DEFAULT|SHOW | AWAKE ON|OFF",
            PlatformPolicy.Syntax);
        Assert.False(PlatformPolicy.TrySet(MachineSettings.Default, "QOS", "LOW", true, out _, out words));
        Assert.Equal("MACHINE SET QOS HIGH|AUTO", words);
    }

    [Fact]
    public void EveryOtherChangeIsKeptWordByWord()
    {
        var s = MachineSettings.Default;
        foreach (var (word, value) in new[] { ("QOS", "AUTO"), ("TIMER", "DEFAULT"), ("MMCSS", "OFF"), ("PROCESS", "NORMAL"), ("CPUSETS", "OFF"), ("GPU-PRIORITY", "HIGH"), ("GC", "SHOW"), ("AWAKE", "OFF") })
        {
            Assert.True(PlatformPolicy.TrySet(s, word, value, false, out var next, out var words), words);
            Assert.Equal(value.ToLowerInvariant(), PlatformPolicy.ValueOf(next, word));
            s = next;
        }
        Assert.Equal(MachineSettings.Default with { Qos = "auto", Timer = "default", Mmcss = "off", Process = "normal", CpuSets = "off", GpuPriority = "high", Gc = "show", Awake = "off" }, s);
    }
}
