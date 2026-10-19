using System.Runtime;
using System.Text.Json;
using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Engine.Machine;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>The GC's show mode through the arm hook: under DEFAULT arming changes nothing and says so; under SHOW the collector is asked
/// for SustainedLowLatency as the show arms, read back and published, and put back as it was at disarm; MACHINE SET GC applies from the
/// next arm; the pause histogram counts each pause into its bucket. The collector's mode is the process's, so these run alone.</summary>
[Collection(GcShowCollection.Name)]
public sealed class GcShowTests
{
    private static EngineCore Engine(MachineNode node) =>
        new(EngineServices.ForTests() with { MachineNode = node, ArmHooks = [new GcShowHook(() => node.Saved, node)] }, Project.CreateDefault("GC"));

    private static async Task<JsonElement> Run(EngineCore engine, string line)
    {
        Assert.True(PlayMap.Core.Wire.WireGrammar.TryParse(line, out var parsed, out _));
        var reply = await engine.HandleAsync(parsed, MachineRig.Desk, CancellationToken.None);
        Assert.True(reply.Ok, reply.Payload);
        return JsonDocument.Parse(reply.Payload!).RootElement.Clone();
    }

    private static string Hook(JsonElement reply) =>
        reply.GetProperty("hooks").EnumerateArray().Single(h => h.GetProperty("name").GetString() == "gc").GetProperty("words").GetString()!;

    [Fact]
    public async Task UnderDefaultArmingChangesNothingAndSaysSo()
    {
        var node = new MachineNode(MachineSettings.Default, _ => { });
        await using var engine = Engine(node);
        var before = GCSettings.LatencyMode;
        Assert.Equal("the collector keeps its own mode (GC DEFAULT)", Hook(await Run(engine, "SHOW ARM")));
        Assert.Equal(before, GCSettings.LatencyMode);
        Assert.Equal("", node.GcHeld);
        _ = await Run(engine, "SHOW DISARM");
        Assert.Equal(before, GCSettings.LatencyMode);
    }

    [Fact]
    public async Task UnderShowTheCollectorHoldsSustainedLowLatencyWhileArmedAndIsPutBackAtDisarm()
    {
        var node = new MachineNode(MachineSettings.Default with { Gc = "show" }, _ => { });
        await using var engine = Engine(node);
        var before = GCSettings.LatencyMode;
        try
        {
            var words = Hook(await Run(engine, "SHOW ARM"));
            if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency)
            {
                Assert.Equal("the collector holds back blocking full collections while armed (SustainedLowLatency)", words);
                Assert.Equal("sustainedLowLatency", node.GcHeld);
            }
            else
            {
                // A runtime without concurrent collection keeps its mode, and the words say which.
                Assert.StartsWith("the runtime kept the collector in", words, StringComparison.Ordinal);
                Assert.Equal("", node.GcHeld);
            }
            var disarm = await Run(engine, "SHOW DISARM");
            Assert.Equal($"the collector is back in {GcShowHook.Word(before)}", Hook(disarm));
            Assert.Equal(before, GCSettings.LatencyMode);
            Assert.Equal("", node.GcHeld);
        }
        finally
        {
            GCSettings.LatencyMode = before;
        }
    }

    [Fact]
    public async Task MachineSetGcAppliesFromTheNextArmAndStateSaysTheSettingAndWhatIsHeld()
    {
        await using var rig = new MachineRig();
        var receipt = await rig.Ok("MACHINE SET GC SHOW");
        Assert.Equal(("next arm", "GC SHOW is kept and applies from the show's next arm"),
            (receipt.GetProperty("applies").GetString(), receipt.GetProperty("words").GetString()));
        Assert.Equal("The GC's mode while armed is already show", await rig.Refused("MACHINE SET GC SHOW"));
        var gc = rig.Machine().GetProperty("platform").GetProperty("gc");
        Assert.Equal(("show", "", JsonValueKind.Null), (gc.GetProperty("setting").GetString(), gc.GetProperty("held").GetString(), gc.GetProperty("pauses").ValueKind));
    }

    [Fact]
    public void ThePauseHistogramCountsEachPauseIntoItsBucket()
    {
        using var pauses = new GcPauses();
        var before = pauses.Summary().Count;
        foreach (var ms in new[] { 0.4, 1.5, 3, 7, 15, 30, 80 })
            pauses.Record(ms);
        var summary = pauses.Summary();
        // The listener may also have counted the runtime's own pauses meanwhile: the recorded ones are in on top of them.
        Assert.True(summary.Count >= before + 7);
        Assert.True(summary.MaxMs >= 80);
        Assert.Equal(7, summary.Buckets.Length);
        Assert.All(summary.Buckets, b => Assert.True(b >= 1));
        Assert.Equal(GcPauses.EdgesMs.Length + 1, summary.Buckets.Length);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GcShowCollection
{
    public const string Name = "the collector's mode";
}
