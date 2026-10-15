using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;

namespace PlayMap.Core.Tests.Fleet;

/// <summary>A machine's health: every category in every record, unknown is −1 and grey with words, the light table, and the record
/// read back as written (bounded).</summary>
public sealed class HealthRecordTests
{
    internal static HealthFacts Known() => new()
    {
        Node = "0badc0de",
        Name = "FOH-A",
        AtUnixMs = 1_760_000_000_000,
        Role = "primary",
        Epoch = 7,
        Build = "0.2.0",
        Rev = 12,
        Outputs = [new HealthOutput("op_1", "Left", true, 60, 0, 0, "gl-window"), new HealthOutput("op_2", "Right", true, 60, 0, 0, "gl-window")],
        Projectors = [new HealthProjector("pj1", "P1", Light.Green, "calibrated, 0.8 px · on", "op_1")],
        Renderer = "llvmpipe",
        RenderPath = "gl",
        CpuPct = 23,
        MemMb = 1400,
        MemFreeMb = 8000,
        DiskFreeGb = 812,
        GpuPct = 40,
        GpuTempC = 61,
        NetMbps = 12.5,
        UptimeS = 86400,
        SyncRole = "leader",
        SyncUncertaintyMs = 0,
        DevicesTotal = 1,
        DevicesGreen = 1,
        MediaItems = 42,
        LaneMs = 12,
        RenderMs = 4.1,
    };

    private static Light Of(HealthRecord r, string category) => r.Categories.Single(c => c.Name == category).Light;

    [Fact]
    public void EveryCategoryIsInEveryRecordInTheContractsOrder()
    {
        foreach (var facts in new[] { Known(), new HealthFacts { Node = "0badc0de", Name = "bare" } })
        {
            var r = HealthRecord.Of(facts);
            Assert.Equal(HealthLights.Names.ToArray(), r.Categories.Select(c => c.Name).ToArray());
            Assert.All(r.Categories, c => Assert.False(string.IsNullOrWhiteSpace(c.Words), c.Name));

            using var doc = JsonDocument.Parse(HealthJson.Write(r));
            var root = doc.RootElement;
            foreach (var field in new[] { "v", "node", "name", "at", "role", "epoch", "build", "light", "headline", "outputs", "render", "cpuPct",
                "memMb", "memFreeMb", "diskFreeGb", "gpuPct", "gpuTempC", "netMbps", "sync", "devices", "media", "watchdog", "categories" })
                Assert.True(root.TryGetProperty(field, out _), field);
            Assert.Equal(HealthLights.Names.Length, root.GetProperty("categories").GetArrayLength());
            Assert.Equal(facts.Node, root.GetProperty("node").GetString());
        }
    }

    [Fact]
    public void UnknownIsMinusOneAndGreyWithWordsNeverZero()
    {
        var r = HealthRecord.Of(new HealthFacts { Node = "0badc0de", Name = "bare" });
        using var doc = JsonDocument.Parse(HealthJson.Write(r));
        var root = doc.RootElement;
        foreach (var field in new[] { "cpuPct", "memMb", "memFreeMb", "diskFreeGb", "gpuPct", "gpuTempC", "netMbps", "uptimeS" })
            Assert.Equal(-1, root.GetProperty(field).GetDouble());
        Assert.Equal(-1, root.GetProperty("watchdog").GetProperty("laneMs").GetDouble());
        Assert.All(r.Categories, c => Assert.Equal(Light.Grey, c.Light));
        Assert.Equal(Light.Grey, r.Light);
        Assert.Equal("grey", root.GetProperty("light").GetString());
        Assert.Contains("GPU", r.Categories.Single(c => c.Name == "gpu").Words, StringComparison.Ordinal);
        Assert.Equal("bare · nothing measured yet", r.Headline);
    }

    [Fact]
    public void AKnownMachineIsGreenAndSaysWhatIsUnknown()
    {
        var r = HealthRecord.Of(Known() with { GpuPct = -1, GpuTempC = -1, GpuWhy = "no counter here" });
        Assert.Equal(Light.Green, r.Light);
        Assert.Equal(Light.Grey, Of(r, "gpu"));
        Assert.Equal("no counter here", r.Categories.Single(c => c.Name == "gpu").Words);
        Assert.Equal("FOH-A · primary · all green · 1 unknown", r.Headline);
    }

    public static TheoryData<string, Func<HealthFacts, HealthFacts>, Light> Table() => new()
    {
        { "cpu", f => f with { CpuPct = 79.9 }, Light.Green },
        { "cpu", f => f with { CpuPct = 80 }, Light.Amber },
        { "cpu", f => f with { CpuPct = 95 }, Light.Red },
        { "memory", f => f with { MemFreeMb = 1600 }, Light.Green },
        { "memory", f => f with { MemFreeMb = 1000 }, Light.Amber },
        { "memory", f => f with { MemFreeMb = 400 }, Light.Red },
        { "disk", f => f with { DiskFreeGb = 9 }, Light.Amber },
        { "disk", f => f with { DiskFreeGb = 1 }, Light.Red },
        { "gpu", f => f with { GpuTempC = 82 }, Light.Amber },
        { "gpu", f => f with { GpuPct = 99 }, Light.Red },
        { "network", f => f with { NetMbps = 950 }, Light.Amber },
        { "outputs", f => f with { Outputs = [new HealthOutput("op_1", "Left", true, 60, 3, 0, "")] }, Light.Amber },
        { "outputs", f => f with { Outputs = [new HealthOutput("op_1", "Left", true, 60, 0, 2, "")] }, Light.Amber },
        { "outputs", f => f with { Outputs = [new HealthOutput("op_1", "Left", false, 0, 0, 0, "")] }, Light.Red },
        { "outputs", f => f with { Outputs = [new HealthOutput("op_1", "Left", true, -1, 0, 0, "")] }, Light.Grey },
        { "outputs", f => f with { Outputs = [] }, Light.Grey },
        { "render", f => f with { Resets = 1 }, Light.Amber },
        { "render", f => f with { Renderer = "none" }, Light.Grey },
        { "sync", f => f with { SyncRole = "follower", SyncUncertaintyMs = 0.3 }, Light.Green },
        { "sync", f => f with { SyncRole = "follower", SyncUncertaintyMs = 3 }, Light.Amber },
        { "sync", f => f with { SyncRole = "follower", SyncUncertaintyMs = 12 }, Light.Red },
        { "sync", f => f with { SyncRole = "solo" }, Light.Grey },
        { "sync", f => f with { FreewheelS = 4 }, Light.Amber },
        { "devices", f => f with { DevicesAmber = 1, DevicesGreen = 0 }, Light.Amber },
        { "devices", f => f with { DevicesRed = 1, DevicesGreen = 0 }, Light.Red },
        { "devices", f => f with { DevicesTotal = 0, DevicesGreen = 0 }, Light.Grey },
        { "media", f => f with { MediaMissing = 2 }, Light.Amber },
        { "media", f => f with { MediaItems = 0 }, Light.Grey },
        { "watchdog", f => f with { LaneMs = 70 }, Light.Amber },
        { "watchdog", f => f with { LaneMs = 300 }, Light.Red },
        { "watchdog", f => f with { RenderMs = 45 }, Light.Amber },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void TheLightTable(string category, Func<HealthFacts, HealthFacts> change, Light expected)
    {
        var r = HealthRecord.Of(change(Known()));
        Assert.Equal(expected, Of(r, category));
        // The machine's light is the worst known category: red over amber over green; grey says nothing.
        var worst = expected == Light.Grey ? Light.Green : expected;
        Assert.Equal(worst, r.Light);
        if (expected is Light.Red or Light.Amber)
            Assert.Contains(category, r.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRecordReadsBackAsWritten()
    {
        var r = HealthRecord.Of(Known() with { Of = "9f8e7d6c", SyncRole = "follower", SyncLeader = "9f8e7d6c", SyncUncertaintyMs = 3 });
        var json = HealthJson.Write(r);
        var back = HealthJson.Parse(json);
        Assert.NotNull(back);
        Assert.Equal(r.Light, back.Light);
        Assert.Equal(r.Headline, back.Headline);
        Assert.Equal(r.Categories.ToArray(), back.Categories.ToArray());
        Assert.Equal(r.Facts.Outputs.ToArray(), back.Facts.Outputs.ToArray());
        Assert.Equal(r.Facts.Projectors.ToArray(), back.Facts.Projectors.ToArray());
        Assert.Equal("9f8e7d6c", back.Facts.Of);
        Assert.Equal("9f8e7d6c", back.Facts.SyncLeader);
        Assert.Equal(json, HealthJson.Write(back));
    }

    [Fact]
    public void ARecordThatIsNotOneReadsNull()
    {
        Assert.Null(HealthJson.Parse(null));
        Assert.Null(HealthJson.Parse(""));
        Assert.Null(HealthJson.Parse("[1,2]"));
        Assert.Null(HealthJson.Parse("{\"name\":\"no node id\"}"));
        Assert.Null(HealthJson.Parse("{\"node\":"));
        Assert.Null(HealthJson.Parse("{\"node\":\"a\",\"name\":\"" + new string('x', HealthJson.MaxBytes) + "\"}"));
        var odd = HealthJson.Parse("{\"node\":\"a\",\"light\":\"purple\",\"cpuPct\":\"lots\"}");
        Assert.NotNull(odd);
        Assert.Equal(Light.Grey, odd.Light);
        Assert.Equal(-1, odd.Facts.CpuPct);
    }
}
