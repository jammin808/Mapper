using System.Diagnostics;
using System.Text.Json;
using PlayMap.Core.Effects;
using PlayMap.Engine.Host;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Render.Effects;
using NodeRig = PlayMap.Engine.Tests.Host.HostRig;
using RenderRig = PlayMap.Engine.Tests.Rendering.HostRig;

namespace PlayMap.Engine.Tests.Shaders;

/// <summary>A node composed with the exe's render host and shaders part (EngineParts.Standard's own): a slot whose kind the node
/// quarantines is drawn bypassed — its output shows the picture its chain was given — and STATE's fx.slots[].state reads "bypassed"
/// with the renderer's words and no GPU time while the quarantine holds the kind; FX RELEASE lets the slot draw again, and STATE
/// reads it ok.</summary>
[Collection("render host")]
public sealed class QuarantineRenderTests
{
    private const string Quarantined = "quarantined on this node (FX RELEASE lifts it): its input passes unchanged";

    private static EngineParts Parts()
    {
        var standard = EngineParts.Standard();
        Assert.NotNull(standard.RenderHost);
        return EngineParts.Bare with
        {
            RenderHost = standard.RenderHost,
            Extensions = [Assert.Single(standard.Extensions, f => f == ShadersExtension.Factory)],
        };
    }

    private static JsonElement Slot(NodeRig node, string id) =>
        node.State().GetProperty("fx").GetProperty("slots").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);

    /// <summary>Waits, counting renderer frames, until STATE's slot reads <paramref name="state"/> and the output's left edge reads
    /// <paramref name="edge"/>; answers the slot's STATE object.</summary>
    private static async Task<JsonElement> Until(NodeRig node, RenderHost render, string id, string state, (int, int, int) edge)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            await render.FramesAsync(1).WaitAsync(RenderRig.Patience);
            var slot = Slot(node, id);
            var px = await render.ReadOutputAsync("op_1").WaitAsync(RenderRig.Patience);
            var at = px is null ? default : RenderRig.At(px, 1920, 0, 540);
            if (slot.GetProperty("state").GetString() == state && at == edge)
                return slot;
            Assert.True(waited.Elapsed < RenderRig.Patience,
                $"{id} reads {slot.GetProperty("state").GetString()} ({slot.GetProperty("words").GetString()}) and the edge {at}; expected {state} and {edge}");
        }
    }

    [Fact]
    public async Task AQuarantinedKindsSlotDrawsItsInputAndReadsBypassedUntilFxReleaseLetsItDrawAgain()
    {
        await using var node = await NodeRig.StartAsync("Quarantine node", parts: Parts());
        var render = Assert.IsType<RenderHost>(node.Host.Render);
        await render.FramesAsync(2).WaitAsync(RenderRig.Patience);
        var slot = await node.OkAsync("FX ADD op_1 fx.invert");
        // The default show's test card is white at the output's left edge: the effect draws it black.
        Assert.Equal("ok", (await Until(node, render, slot, "ok", (0, 0, 0))).GetProperty("state").GetString());

        var hash = FxCatalog.For(node.Engine.Store.Project.FxAssets).Source("fx.invert")!.Hash;
        Assert.True(node.Engine.Services.Quarantine.Add(new QuarantineEntry(hash, "Invert", QuarantineReason.GpuTime,
            "its pass ran past the output's period", 1_760_000_000_000, "0.2.0+17.abc1234")).Applied);
        var bypassed = await Until(node, render, slot, "bypassed", (255, 255, 255));
        // The show's slot is not bypassed: the renderer bypasses it for the node's quarantine, and draws nothing of it.
        Assert.Equal((Quarantined, false, 0.0),
            (bypassed.GetProperty("words").GetString(), bypassed.GetProperty("bypass").GetBoolean(), bypassed.GetProperty("gpuMs").GetDouble()));
        var held = Assert.Single(node.State().GetProperty("fx").GetProperty("quarantine").EnumerateArray());
        Assert.Equal(slot, Assert.Single(held.GetProperty("slots").EnumerateArray()).GetString());

        await node.OkAsync($"FX RELEASE {hash}");
        Assert.Empty(node.Engine.Services.Quarantine.All);
        var drawn = await Until(node, render, slot, "ok", (0, 0, 0));
        Assert.Equal("", drawn.GetProperty("words").GetString());
    }
}
