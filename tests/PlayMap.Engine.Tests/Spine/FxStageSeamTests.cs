using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>FX CHECK's seam for the stages beyond the GL dialects (wp3-shaders' and wp3-shaders-2's road to a device): a node composed
/// with none reports as it always has, with no stages key; one composed with a check carries each stage in its order, and a stage that
/// fails makes the report's ok false.</summary>
public sealed class FxStageSeamTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    /// <summary>A check that answers the stages it was made with, and the name of the effect it was asked about.</summary>
    private sealed class Stages(params FxStage[] stages) : IFxStageCheck
    {
        public string Asked { get; private set; } = "";

        public Task<ImmutableArray<FxStage>> CheckAsync(string name, string fragment, string? vertex, CancellationToken ct)
        {
            Asked = name;
            return Task.FromResult(ImmutableArray.Create(stages));
        }
    }

    private static async Task<JsonElement> Check(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        var reply = await engine.HandleAsync(parsed, Desk, CancellationToken.None);
        Assert.True(reply.Ok, reply.Payload);
        using var doc = JsonDocument.Parse(reply.Payload!);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ANodeWithNoStageCheckReportsNoStages()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var report = await Check(engine, "FX CHECK fx.feedback");
        Assert.True(report.GetProperty("ok").GetBoolean());
        Assert.False(report.TryGetProperty("stages", out _));
    }

    [Fact]
    public async Task EachStageIsReportedInItsOrderAndAFailedOneMakesTheReportNotOk()
    {
        var passing = new Stages(new FxStage("spirv", true, "compiled", 0), new FxStage("dxil", null, "no Direct3D 12 device on this node", 0));
        await using (var engine = new EngineCore(EngineServices.ForTests() with { FxStages = passing }))
        {
            var report = await Check(engine, "FX CHECK fx.feedback");
            Assert.True(report.GetProperty("ok").GetBoolean());
            var stages = report.GetProperty("stages").EnumerateArray().ToList();
            Assert.Equal(["spirv", "dxil"], stages.Select(s => s.GetProperty("name").GetString()));
            Assert.True(stages[0].GetProperty("ok").GetBoolean());
            Assert.Equal(JsonValueKind.Null, stages[1].GetProperty("ok").ValueKind);
            Assert.Equal("no Direct3D 12 device on this node", stages[1].GetProperty("words").GetString());
            Assert.False(string.IsNullOrEmpty(passing.Asked));
        }

        var failing = new Stages(new FxStage("validate", false, "an image is read past its bounds", 12));
        await using (var engine = new EngineCore(EngineServices.ForTests() with { FxStages = failing }))
        {
            var report = await Check(engine, "FX CHECK fx.feedback");
            Assert.False(report.GetProperty("ok").GetBoolean());
            var stage = Assert.Single(report.GetProperty("stages").EnumerateArray());
            Assert.Equal(12, stage.GetProperty("line").GetInt32());
        }
    }
}
