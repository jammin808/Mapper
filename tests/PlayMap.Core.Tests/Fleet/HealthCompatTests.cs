using System.Text.Json;
using PlayMap.Core.Fleet;

namespace PlayMap.Core.Tests.Fleet;

/// <summary>HEALTH's round-3 fields: each output's shown and observed frames (−1 unknown) and the GPU record (the device epoch, the
/// phase, since when). A round-2 line, which has none of them, reads them as unknown; a line that has them reads them back as written;
/// a value that is not a whole count of 0 or more, or a phase word that is not one, reads unknown.</summary>
public sealed class HealthCompatTests
{
    /// <summary>A line as round 2's writer wrote it: every key it had, none of round 3's.</summary>
    private const string RoundTwoLine =
        """
        {"v":1,"node":"0badc0de","name":"FOH-A","at":1760000000000,"role":"primary","of":"","epoch":7,"mirror":"","build":"0.2.0+17.abc1234",
         "light":"green","headline":"FOH-A: all well","outputs":[{"id":"op_1","name":"Left","light":"green","open":true,"fps":60,"dropped":0,
         "missed60s":0,"path":"gl-window"},{"id":"op_2","name":"Right","light":"green","open":true,"fps":60,"dropped":0,"missed60s":0,
         "path":"gl-window"}],"projectors":[],"render":{"renderer":"llvmpipe","path":"gl","resets":0},"cpuPct":23,"memMb":1400,
         "memFreeMb":8000,"diskFreeGb":812,"gpuPct":40,"gpuTempC":61,"netMbps":12.5,"uptimeS":86400,"sync":{"role":"leader","leader":"",
         "group":"","uncertaintyMs":0,"freewheelS":0},"devices":{"total":0,"green":0,"amber":0,"red":0},"media":{"items":42,"missing":0},
         "watchdog":{"laneMs":12,"renderMs":4.1},"rev":12,"categories":[]}
        """;

    [Fact]
    public void ARoundTwoLineReadsTheNewFieldsAsUnknown()
    {
        var r = HealthJson.Parse(RoundTwoLine);

        Assert.NotNull(r);
        Assert.Equal(2, r.Facts.Outputs.Length);
        Assert.All(r.Facts.Outputs, o => Assert.Equal((-1L, -1L), (o.ShownFrames, o.ObservedFrames)));
        Assert.Equal(HealthGpu.Unknown, r.Facts.Gpu);
        Assert.Equal((-1L, GpuPhase.Unknown, -1L), (r.Facts.Gpu.Epoch, r.Facts.Gpu.Phase, r.Facts.Gpu.SinceUnixMs));
        // What round 2 sent is kept: the rest of the record reads as it did.
        Assert.Equal((7L, "0.2.0+17.abc1234", 60.0), (r.Facts.Epoch, r.Facts.Build, r.Facts.Outputs[0].Fps));

        // Written again, the record says unknown in the new fields' own words.
        using var doc = JsonDocument.Parse(HealthJson.Write(r));
        var root = doc.RootElement;
        foreach (var o in root.GetProperty("outputs").EnumerateArray())
            Assert.Equal((-1L, -1L), (o.GetProperty("shownFrames").GetInt64(), o.GetProperty("observedFrames").GetInt64()));
        var gpu = root.GetProperty("gpu");
        Assert.Equal((-1L, "unknown", -1L), (gpu.GetProperty("epoch").GetInt64(), gpu.GetProperty("phase").GetString(), gpu.GetProperty("since").GetInt64()));
    }

    [Fact]
    public void ANewLineReadsItsCountsAndItsGpuRecordBackAsWritten()
    {
        var facts = HealthRecordTests.Known() with
        {
            Outputs = [new HealthOutput("op_1", "Left", true, 60, 0, 0, "gl-window") { ShownFrames = 216_000, ObservedFrames = 215_990 }],
            Gpu = new HealthGpu(3, GpuPhase.Rebuilding, 1_760_000_000_500),
        };
        var json = HealthJson.Write(HealthRecord.Of(facts));

        var back = HealthJson.Parse(json)!;

        Assert.Equal((216_000L, 215_990L), (back.Facts.Outputs[0].ShownFrames, back.Facts.Outputs[0].ObservedFrames));
        Assert.Equal(new HealthGpu(3, GpuPhase.Rebuilding, 1_760_000_000_500), back.Facts.Gpu);
        Assert.Equal(json, HealthJson.Write(back));
        Assert.Contains("\"gpu\":{\"epoch\":3,\"phase\":\"rebuilding\",\"since\":1760000000500}", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GpuPhase.Unknown, "unknown")]
    [InlineData(GpuPhase.Starting, "starting")]
    [InlineData(GpuPhase.Running, "running")]
    [InlineData(GpuPhase.Rebuilding, "rebuilding")]
    public void EveryPhaseIsOneWordThatReadsBack(GpuPhase phase, string word)
    {
        Assert.Equal(word, HealthGpu.Word(phase));
        Assert.Equal(phase, HealthGpu.Parse(word));
    }

    [Theory]
    [InlineData("\"lots\"")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("true")]
    public void ACountThatIsNotAWholeCountReadsUnknown(string value)
    {
        var line = RoundTwoLine.Replace("\"missed60s\":0,\"path\":\"gl-window\"}", $"\"missed60s\":0,\"path\":\"gl-window\",\"shownFrames\":{value},\"observedFrames\":{value}}}",
            StringComparison.Ordinal);
        Assert.NotEqual(RoundTwoLine, line);
        var gpu = line.Replace("\"rev\":12", $"\"gpu\":{{\"epoch\":{value},\"phase\":\"melting\",\"since\":{value}}},\"rev\":12", StringComparison.Ordinal);

        var r = HealthJson.Parse(gpu)!;

        Assert.All(r.Facts.Outputs, o => Assert.Equal((-1L, -1L), (o.ShownFrames, o.ObservedFrames)));
        Assert.Equal(HealthGpu.Unknown, r.Facts.Gpu);
        Assert.Equal(GpuPhase.Unknown, HealthGpu.Parse("RUNNING"));
        Assert.Equal(HealthGpu.Unknown, HealthJson.Parse(RoundTwoLine.Replace("\"rev\":12", "\"gpu\":\"running\",\"rev\":12", StringComparison.Ordinal))!.Facts.Gpu);
    }

    [Fact]
    public void ANegativeCountInTheFactsIsWrittenAsUnknown()
    {
        var facts = HealthRecordTests.Known() with
        {
            Outputs = [new HealthOutput("op_1", "Left", true, 60, 0, 0, "gl-window") { ShownFrames = -7 }],
            Gpu = new HealthGpu(-3, GpuPhase.Running, -9),
        };

        var back = HealthJson.Parse(HealthJson.Write(HealthRecord.Of(facts)))!;

        Assert.Equal(-1, back.Facts.Outputs[0].ShownFrames);
        Assert.Equal(new HealthGpu(-1, GpuPhase.Running, -1), back.Facts.Gpu);
    }
}
