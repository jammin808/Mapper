using System.Collections.Concurrent;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>HEALTH: the verb's record from published state; SUBSCRIBE HEALTH pushes it at once, then once a second on the fleet's
/// clock, and at once when a light moves (a red output reaches the push without the clock moving); unknown stays −1 and grey.</summary>
public sealed class HealthTests
{
    internal sealed class FakeSampler : IMachineSampler
    {
        public MachineSample Latest { get; set; } = new(23, 1400, 8000, 812, 12.5, 86400, -1, -1, "no GPU counter here");
    }

    /// <summary>Outputs whose health a test sets.</summary>
    internal sealed class Outputs : IOutputBackend
    {
        public List<OutputHealth> Now { get; set; } = [new("op_1", true, 60, 0, 4.1, "DP-1")];

        public string RendererName => "test";

        public IReadOnlyList<DisplayInfo> Displays() => [];

        public IReadOnlyList<OutputHealth> Health() => [.. Now];
    }

    internal sealed class Sink : IPushSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public void Latest(string key, string line) => Lines.Enqueue(line);

        public bool InOrder(string line)
        {
            Lines.Enqueue(line);
            return true;
        }
    }

    internal static EngineServices Services(ManualTime time, IOutputBackend? outputs = null, IMachineSampler? sampler = null) =>
        EngineServices.ForTests(new ManualClock(5_000_000_000)) with
        {
            Outputs = outputs ?? new Outputs(),
            Machine = sampler,
            FleetTime = time,
        };

    private static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, SessionInfo.Desk("test"), CancellationToken.None);
    }

    [Fact]
    public async Task HealthAnswersEveryCategoryFromPublishedState()
    {
        await using var engine = new EngineCore(Services(new ManualTime(), sampler: new FakeSampler()), Project.CreateDefault("Show"));
        var reply = await Run(engine, "HEALTH");
        Assert.True(reply.Ok, reply.Payload);
        var record = HealthJson.Parse(reply.Payload);
        Assert.NotNull(record);
        Assert.Equal("0badc0de", record.Facts.Node);
        Assert.Equal(HealthLights.Names.ToArray(), record.Categories.Select(c => c.Name).ToArray());
        Assert.Equal(23, record.Facts.CpuPct);
        Assert.Equal(-1, record.Facts.GpuPct);
        Assert.Equal(Light.Grey, record.Categories.Single(c => c.Name == "gpu").Light);
        Assert.Equal("no GPU counter here", record.Categories.Single(c => c.Name == "gpu").Words);
        Assert.Equal(Light.Green, record.LightOf(record.Facts.Outputs.Single()));
        Assert.Equal(Light.Green, record.Light);
        Assert.False((await Run(engine, "HEALTH NOW")).Ok);
    }

    [Fact]
    public async Task ANodeWithoutTheSamplerSaysItsMachineNumbersAreUnknown()
    {
        await using var engine = new EngineCore(Services(new ManualTime()), Project.CreateDefault("Show"));
        var record = HealthJson.Parse((await Run(engine, "HEALTH")).Payload)!;
        Assert.Equal(-1, record.Facts.CpuPct);
        Assert.Equal(-1, record.Facts.DiskFreeGb);
        Assert.Equal(-1, record.Facts.LaneMs);
        Assert.All(new[] { "cpu", "memory", "disk", "gpu", "network" },
            name => Assert.Equal(Light.Grey, record.Categories.Single(c => c.Name == name).Light));
    }

    [Fact]
    public async Task SubscribeHealthPushesAtOnceEverySecondAndOnALightChange()
    {
        var time = new ManualTime();
        var outputs = new Outputs();
        await using var engine = new EngineCore(Services(time, outputs, new FakeSampler()), Project.CreateDefault("Show"));
        var topics = (IPushTopics)engine;
        Assert.Contains("HEALTH", topics.Topics);
        var sink = new Sink();
        Assert.False(topics.TryOpen("HEALTH", ["NOW"], SessionInfo.Desk("t"), sink, out _, out var refused));
        Assert.Contains("no words", refused, StringComparison.Ordinal);

        Assert.True(topics.TryOpen("HEALTH", [], new SessionInfo("w1", "wire", "10.0.0.9", false, "Somebody", false), sink, out var sub, out _));
        using (sub)
        {
            // At once: an open read, unpaired.
            Assert.True(await Eventually.TrueAsync(() => !sink.Lines.IsEmpty));
            Assert.All(sink.Lines, l => Assert.StartsWith("HEALTH {", l, StringComparison.Ordinal));
            var first = sink.Lines.Count;

            // Once a second on the fleet's clock.
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(first + 1, sink.Lines.Count);
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(first + 3, sink.Lines.Count);

            // A light that moves is pushed at once, without the clock.
            outputs.Now = [new("op_1", false, 0, 0, 0, "DP-1")];
            engine.FactsChanged();
            Assert.True(await Eventually.TrueAsync(() => sink.Lines.Count == first + 4));
            var json = sink.Lines.Last()["HEALTH ".Length..];
            Assert.Equal(Light.Red, HealthJson.Parse(json)!.Light);
            using (var doc = JsonDocument.Parse(json))
                Assert.Equal("red", doc.RootElement.GetProperty("outputs")[0].GetProperty("light").GetString());

            // The same lights again push nothing until the second is up.
            engine.FactsChanged();
            await Task.Yield();
            time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Equal(first + 4, sink.Lines.Count);
        }
        // Nobody watches: the clock stops.
        Assert.Equal(0, time.Waiting);
    }

    private static (string Fleet, string Eye) Lights(EngineCore engine)
    {
        using var doc = JsonDocument.Parse(engine.State().Json);
        return (doc.RootElement.GetProperty("fleet").GetProperty("light").GetString()!,
            doc.RootElement.GetProperty("eye").GetProperty("worstLight").GetString()!);
    }

    [Fact]
    public async Task AMachineLightThatMovesMovesStateWithNoOtherFact()
    {
        var time = new ManualTime();
        var sampler = new FakeSampler();
        await using var engine = new EngineCore(Services(time, sampler: sampler), Project.CreateDefault("Show"));
        engine.Fleet.Start();
        Assert.Equal(("green", "green"), Lights(engine));
        var key = engine.StateKey();

        // Numbers that wander inside one light move nothing.
        sampler.Latest = sampler.Latest with { CpuPct = 41 };
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(key, engine.StateKey());

        // The CPU crosses into red with nothing else moving: STATE's fleet light, its eye row and the context's machine item say it
        // within the second.
        sampler.Latest = sampler.Latest with { CpuPct = 97 };
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(key, engine.StateKey());
        Assert.Equal(("red", "red"), Lights(engine));
        Assert.Equal(Light.Red, ContextBuilder.Build(engine).Items.Single(i => i.Id == "machine").Light);

        // Stopped with the node: no clock is left.
        await engine.Fleet.StopAsync();
        Assert.Equal(0, time.Waiting);
    }
}
