using PlayMap.Engine.Host;
using PlayMap.Engine.Rendering;
using NodeRig = PlayMap.Engine.Tests.Host.HostRig;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The render host in a real node (the engine host with the exe's render factory): it renders, its
/// outputs' health reaches STATE, the scan display is the render host, VIEW reaches it; and a headless node
/// with no display runs without one and no fault, while a windowed node with no display says why.</summary>
[Collection("render host")]
public sealed class NodeRenderTests
{
    private static EngineParts WithRender => EngineParts.Bare with { RenderHost = RenderHost.ForNode };

    [Fact]
    public async Task ARealNodeRendersAndItsOutputsHealthIsInState()
    {
        Assert.NotNull(EngineParts.Standard().RenderHost);
        await using var node = await NodeRig.StartAsync("Render node", parts: WithRender);
        var render = Assert.IsType<RenderHost>(node.Host.Render);
        await render.FramesAsync(30).WaitAsync(HostRig.Patience);
        var px = await render.ReadOutputAsync("op_1").WaitAsync(HostRig.Patience);
        Assert.NotNull(px);
        Assert.Equal((255, 255, 255), HostRig.At(px, 1920, 0, 540));

        Assert.True(await Tests.Host.Eventually.TrueAsync(() =>
        {
            var output = node.State().GetProperty("outputs").EnumerateArray().First();
            return output.GetProperty("fps").GetDouble() > 0;
        }, timeoutMs: 20_000), "the output's rate never reached STATE");
        var state = node.State();
        var op = state.GetProperty("outputs").EnumerateArray().First();
        Assert.Equal("op_1", op.GetProperty("id").GetString());
        Assert.False(op.GetProperty("open").GetBoolean());
        Assert.StartsWith("OpenGL ", state.GetProperty("machine").GetProperty("renderer").GetString(), StringComparison.Ordinal);
        Assert.Empty(state.GetProperty("faults").EnumerateArray());

        // The wire's VIEW reaches the render host of a real node.
        Assert.True((await node.RunAsync("VIEW op-op_1 SHOW output:op_1 480x270")).Ok);
        Assert.Equal(1, render.ViewsShown);
    }

    [Fact]
    public async Task AHeadlessNodeWithNoDisplayRunsWithoutGlAndAWindowedOneSaysWhy()
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", null);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            if (!OperatingSystem.IsLinux())
            {
                // DISPLAY and WAYLAND_DISPLAY name Linux's display servers: elsewhere the window system needs neither, and a
                // node renders without them.
                await using var rendering = await NodeRig.StartAsync("GL", parts: WithRender);
                Assert.NotNull(rendering.Host.Render);
                Assert.Empty(rendering.State().GetProperty("faults").EnumerateArray());
                return;
            }
            await using (var headless = await NodeRig.StartAsync("No GL", parts: WithRender))
            {
                Assert.Null(headless.Host.Render);
                Assert.Empty(headless.State().GetProperty("faults").EnumerateArray());
                Assert.Equal("none", headless.State().GetProperty("machine").GetProperty("renderer").GetString());
            }
            await using var windowed = await NodeRig.StartAsync("Desk node", parts: WithRender,
                options: home => NodeRig.Parse("engine", "--home", home, "--no-recover"));
            Assert.Null(windowed.Host.Render);
            var fault = Assert.Single(windowed.State().GetProperty("faults").EnumerateArray());
            Assert.Equal("render", fault.GetProperty("part").GetString());
            Assert.Contains("no display", fault.GetProperty("words").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", display);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", wayland);
        }
    }
}
