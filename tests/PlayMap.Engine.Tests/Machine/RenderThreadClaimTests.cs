using PlayMap.Core.Machine;
using PlayMap.Engine.Host;
using PlayMap.Engine.Machine;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Spine;
using RenderRig = PlayMap.Engine.Tests.Rendering.HostRig;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>A node composed as the exe composes it — the node's own render host (headless, drawing offscreen) beside the machine part
/// over a fake Windows platform — claims the render role through that platform on the thread named "render", so the composed
/// platform keeps the render thread's claims and STATE shows them. ThreadClaims is one static seam for the process, so these run
/// alone (and apart from every other render host).</summary>
[Collection(SeamHostCollection.Name)]
public sealed class RenderThreadClaimTests
{
    private static EngineParts Parts(FakePlatform platform) => EngineParts.Bare with
    {
        RenderHost = RenderHost.ForNode,
        Extensions = [_ => new MachineParts(new MachineNode(MachineSettings.Default, _ => { }), platform)],
    };

    [Fact]
    public async Task TheRenderThreadClaimsItsRoleThroughTheComposedPlatformAndStateShowsIt()
    {
        var platform = new FakePlatform();
        await using var node = await HostRig.StartAsync("Render claim", parts: Parts(platform));
        var render = Assert.IsType<RenderHost>(node.Host.Render);
        await render.FramesAsync(2).WaitAsync(RenderRig.Patience);

        // The render role was claimed once, on the render thread, through the node's platform.
        Assert.Equal<(string, string)>([("render", "render")], platform.ClaimedOn.Where(c => c.Role == "render"));
        var recorded = Assert.IsType<RecordedPlatform>(node.Engine.Services.MachinePlatform);
        var claims = recorded.Claims.Where(c => c.What.StartsWith("thread.render.", StringComparison.Ordinal))
            .Select(c => (c.What, c.Asked, c.Applied)).ToList();
        Assert.Equal<(string, string, bool)>([("thread.render.priority", "aboveNormal", true), ("thread.render.mmcss", "Games", true)], claims);
        var published = node.State().GetProperty("machine").GetProperty("platform").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("what").GetString()).ToList();
        Assert.Contains("thread.render.priority", published);
        Assert.Contains("thread.render.mmcss", published);
    }
}
