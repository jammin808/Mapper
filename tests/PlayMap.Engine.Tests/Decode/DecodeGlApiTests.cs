using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Render.Platform;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Decode;

/// <summary>decode.glApi followed down the chain: a render host asked for EGL makes its desktop context on EGL, and STATE.decode's
/// platform says so — binding egl, the dmabuf share of that display, and why zero-copy waits (a software GL names no render node).</summary>
[Collection("render host")]
public sealed class DecodeGlApiTests(ITestOutputHelper log)
{
    [Fact]
    public async Task AnEglContextGivesTheDecodePlatformItsBindingAndTheDmabufShare()
    {
        var options = RenderHostOptions.Default with { Windows = false, Platform = PlatformRequest.Default with { GlApi = "egl" } };
        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#336699"), 64, 36)), options: options);
        await rig.Host.FramesAsync(5).WaitAsync(HostRig.Patience);
        rig.Engine.FactsChanged();
        using var state = JsonDocument.Parse(rig.Engine.State().Json);
        var platform = state.RootElement.GetProperty("decode").GetProperty("platform");
        log.WriteLine(platform.GetRawText());
        Assert.Equal("egl", platform.GetProperty("gl").GetString());
        Assert.Equal("dmabuf", platform.GetProperty("api").GetString());
        if (rig.Host.RendererName.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal("unavailable", platform.GetProperty("zeroCopy").GetString());
            Assert.StartsWith("the GL is software", platform.GetProperty("why").GetString(), StringComparison.Ordinal);
        }
    }
}
