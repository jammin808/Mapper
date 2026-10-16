using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Presentation;

namespace PlayMap.Engine.Tests.Presentation;

/// <summary>An engine whose present settings and fuse live in a temporary node home, with the scripted output backend.</summary>
internal sealed class PathRig : IAsyncDisposable
{
    public readonly string Folder = Directory.CreateTempSubdirectory("playmap-present-").FullName;
    public readonly List<PresentSettings> Saved = [];
    public readonly ScriptedOutputs Outputs = new ScriptedOutputs().WithFreeLocks("op_a");
    public readonly EngineCore Engine;
    public readonly PresentNode Node;

    public PathRig(string path = "auto", bool backend = true)
    {
        Node = new PresentNode(PresentSettings.Default with { Path = path }, Saved.Add, new PresentFuse(Folder, () => 1_760_000_123_000));
        var services = EngineServices.ForTests() with { PresentNode = Node };
        if (backend)
            services = services with { Outputs = Outputs };
        Engine = new EngineCore(services, PresentRig.With(PresentRig.Screen("op_a")));
    }

    public PresentFuse Fuse => Node.Fuse;

    public async Task<CommandReply> Run(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, PresentRig.Desk, CancellationToken.None);
    }

    public JsonElement PresentPath()
    {
        Engine.FactsChanged();
        using var doc = JsonDocument.Parse(Engine.State().Json);
        return doc.RootElement.GetProperty("machine").GetProperty("present").Clone();
    }

    public async Task<PreflightRow> PathRow()
    {
        var rows = await new OutputPresentCheck().RunAsync(new PreflightContext(Engine, PreflightDepth.Quick, false), CancellationToken.None);
        return Assert.Single(rows, r => r.Id == "outputs.present-path");
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        Directory.Delete(Folder, recursive: true);
    }
}

/// <summary>PRESENT PATH: AUTO is OpenGL on every platform this round (said in the receipt); DXGI is refused off Windows with the words;
/// a no-op is refused; what is asked is kept on the node and observed in STATE <c>machine.present.asked</c>.</summary>
public sealed class PresentPathTests
{
    [Fact]
    public async Task AutoIsOpenGlWithWordsAndTheAskedPathIsKeptAndPublished()
    {
        await using var rig = new PathRig();
        Assert.Equal("auto", rig.PresentPath().GetProperty("asked").GetString());
        Assert.True(rig.PresentPath().GetProperty("kept").GetBoolean());

        var noop = await rig.Run("PRESENT PATH AUTO");
        Assert.False(noop.Ok);
        Assert.Equal("PRESENT PATH is already AUTO (from the render host's next start)", noop.Payload);
        Assert.Empty(rig.Saved);

        var gl = await rig.Run("PRESENT PATH GL");
        Assert.True(gl.Ok, gl.Payload);
        var receipt = JsonDocument.Parse(gl.Payload!).RootElement;
        Assert.Equal("gl", receipt.GetProperty("asked").GetString());
        Assert.Equal("auto", receipt.GetProperty("was").GetString());
        Assert.Equal("next start", receipt.GetProperty("applies").GetString());
        Assert.False(receipt.GetProperty("experimental").GetBoolean());
        Assert.Equal("gl", Assert.Single(rig.Saved).Path);
        Assert.Equal("gl", rig.PresentPath().GetProperty("asked").GetString());

        var auto = await rig.Run("PRESENT PATH AUTO");
        Assert.True(auto.Ok, auto.Payload);
        Assert.Equal("outputs present through OpenGL from the render host's next start (AUTO is OpenGL on every platform this round)",
            JsonDocument.Parse(auto.Payload!).RootElement.GetProperty("words").GetString());
        Assert.Equal("auto", rig.PresentPath().GetProperty("asked").GetString());
        Assert.Equal(2, rig.Saved.Count);
    }

    [Fact]
    public async Task DxgiIsRefusedOffWindowsAndExperimentalOnIt()
    {
        await using var rig = new PathRig();
        var r = await rig.Run("PRESENT PATH DXGI");
        if (OperatingSystem.IsWindows())
        {
            Assert.True(r.Ok, r.Payload);
            var receipt = JsonDocument.Parse(r.Payload!).RootElement;
            Assert.True(receipt.GetProperty("experimental").GetBoolean());
            Assert.Equal("dxgi", rig.PresentPath().GetProperty("asked").GetString());
            return;
        }
        Assert.False(r.Ok);
        Assert.Equal("the DXGI path is Windows only (this node runs Linux): outputs present through OpenGL", r.Payload);
        Assert.Empty(rig.Saved);
        Assert.Equal("auto", rig.PresentPath().GetProperty("asked").GetString());
    }

    [Fact]
    public async Task ANodeWithNoHomeRefusesAndAnUnknownWordIsRefused()
    {
        await using (var rig = new PathRig())
        {
            var bad = await rig.Run("PRESENT PATH VULKAN");
            Assert.False(bad.Ok);
            Assert.Equal("PRESENT PATH AUTO|DXGI|GL", bad.Payload);
        }
        await using var engine = new EngineCore(EngineServices.ForTests(), PresentRig.With(PresentRig.Screen("op_a")));
        Assert.True(WireGrammar.TryParse("PRESENT PATH GL", out var line, out _));
        var r = await engine.HandleAsync(line!, PresentRig.Desk, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.StartsWith("this node keeps no settings", r.Payload, StringComparison.Ordinal);
    }
}
