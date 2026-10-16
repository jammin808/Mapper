using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.Presentation;

/// <summary>Per-output latency on a running render host: outputs of 17 and 50 ms at 60 Hz publish a 2-frame delay on the faster one and
/// the node's air latency of 50 ms; an edit of the input lag moves the plan; a group's slower air raises it through the seam sync writes;
/// an input lag that would need more than six frames is refused with the words.</summary>
public sealed class LatencyStateTests
{
    private static JsonElement State(HostRig rig)
    {
        rig.Engine.FactsChanged();
        return JsonDocument.Parse(rig.Engine.State().Json).RootElement.Clone();
    }

    private static JsonElement Present(JsonElement state, string id) =>
        state.GetProperty("outputs").EnumerateArray().Single(o => o.GetProperty("id").GetString() == id).GetProperty("present");

    [Fact]
    public async Task TheFasterOutputWaitsTwoFramesAndTheNodesAirIsTheSlowests()
    {
        var project = HostRig.WithOutputs(
            HostRig.Screen("op_fast", SourceRef.Solid("#336699"), 64, 36) with { LatencyMs = 17 },
            HostRig.Screen("op_slow", SourceRef.Solid("#996633"), 64, 36) with { LatencyMs = 50 });
        await using var rig = HostRig.Start(project, options: RenderHostOptions.Default with { Windows = false });
        await rig.Host.FramesAsync(3).WaitAsync(HostRig.Patience);
        var s = State(rig);
        Assert.Equal(2, Present(s, "op_fast").GetProperty("delayFrames").GetInt32());
        Assert.Equal(-0.33, Present(s, "op_fast").GetProperty("latencyResidualMs").GetDouble());
        Assert.Equal(0, Present(s, "op_slow").GetProperty("delayFrames").GetInt32());
        var machine = s.GetProperty("machine").GetProperty("present");
        Assert.Equal(50, machine.GetProperty("airLatencyMs").GetDouble());
        Assert.Equal("", machine.GetProperty("latencyWords").GetString());
        Assert.Equal(50, s.GetProperty("sync").GetProperty("latencyMs").GetDouble());
        Assert.Equal(50, rig.Host.Latency.AirLatencyMs);

        // The slow display's lag edited: the plan follows at the render host's next frame.
        Assert.True((await rig.Run("SET op_slow latencyMs 33")).Ok);
        await rig.Host.FramesAsync(3).WaitAsync(HostRig.Patience);
        s = State(rig);
        Assert.Equal(1, Present(s, "op_fast").GetProperty("delayFrames").GetInt32());
        Assert.Equal(33, s.GetProperty("machine").GetProperty("present").GetProperty("airLatencyMs").GetDouble());

        // A group's slower node raises the air (the seam sync's group latency writes through); letting it go lowers it again.
        rig.Host.SetGroupAirLatency(100);
        await rig.Host.FramesAsync(3).WaitAsync(HostRig.Patience);
        s = State(rig);
        Assert.Equal(100, s.GetProperty("machine").GetProperty("present").GetProperty("airLatencyMs").GetDouble());
        Assert.Equal(4, Present(s, "op_slow").GetProperty("delayFrames").GetInt32());
        Assert.Equal(5, Present(s, "op_fast").GetProperty("delayFrames").GetInt32());
        rig.Host.SetGroupAirLatency(0);
        await rig.Host.FramesAsync(3).WaitAsync(HostRig.Patience);
        Assert.Equal(33, State(rig).GetProperty("machine").GetProperty("present").GetProperty("airLatencyMs").GetDouble());

        // More than six frames apart cannot be lined up: refused with the words, nothing changed.
        var refused = await rig.Run("SET op_slow latencyMs 150");
        Assert.False(refused.Ok);
        Assert.Contains("op_fast would wait 8 frames", refused.Payload, StringComparison.Ordinal);
        Assert.Contains("the delay ring holds at most 6", refused.Payload, StringComparison.Ordinal);
        Assert.Equal(33, rig.Engine.Store.Project.Outputs.Single(o => o.Id == "op_slow").LatencyMs);
    }
}

/// <summary>The six-frame check runs only on an edit that moves the plan: a project loaded with lags too far apart still takes a rename.</summary>
public sealed class LatencyEditTests
{
    [Fact]
    public async Task ARenameIsNotRefusedForLagsTheEditDoesNotMove()
    {
        var project = HostRig.WithOutputs(
            HostRig.Screen("op_fast", SourceRef.Solid("#336699"), 64, 36) with { LatencyMs = 0 },
            HostRig.Screen("op_slow", SourceRef.Solid("#996633"), 64, 36) with { LatencyMs = 150 });
        await using var engine = new EngineCore(EngineServices.ForTests(), project);
        Assert.True(Core.Wire.WireGrammar.TryParse("SET op_fast name \"Left\"", out var rename, out _));
        Assert.True((await engine.HandleAsync(rename!, PresentRig.Desk, CancellationToken.None)).Ok);
        Assert.True(Core.Wire.WireGrammar.TryParse("SET op_fast latencyMs 1", out var lag, out _));
        var refused = await engine.HandleAsync(lag!, PresentRig.Desk, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("the delay ring holds at most 6", refused.Payload, StringComparison.Ordinal);
    }
}
