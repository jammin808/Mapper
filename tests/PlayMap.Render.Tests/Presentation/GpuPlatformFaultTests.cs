using PlayMap.Core.Media;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>A DXGI platform failing at each start stage leaves the node on OpenGL with words naming the stage; a DXGI request where
/// there is no DXGI (Linux) is OpenGL with "Windows only"; AUTO and GL are OpenGL with no words; the fuse's words lead.</summary>
/// <remarks>Made surfaceless (EGL, Linux), so the GL fallback needs no window system.</remarks>
public sealed class GpuPlatformFaultTests
{
    private static readonly GlDeviceOptions Device = GlDeviceOptions.Default;

    /// <summary>A fake DXGI platform maker that fails at a chosen stage, counting its calls.</summary>
    private sealed class FailingDxgi(DxgiStartStage stage)
    {
        public int Calls { get; private set; }

        public IGpuPlatform Make(GlDeviceOptions options)
        {
            Calls++;
            throw new GpuPlatformStartException(stage, $"fake {GpuPlatformStartException.Word(stage)} failure");
        }
    }

    [Theory]
    [InlineData(DxgiStartStage.Adapter, "adapter")]
    [InlineData(DxgiStartStage.Device, "device")]
    [InlineData(DxgiStartStage.Angle, "angle")]
    [InlineData(DxgiStartStage.Extensions, "extensions")]
    [InlineData(DxgiStartStage.Context, "context")]
    [InlineData(DxgiStartStage.Programs, "programs")]
    public void AStageThatFailsFallsBackToOpenGlNamingTheStage(DxgiStartStage stage, string word)
    {
        var fake = new FailingDxgi(stage);
        using var platform = GpuPlatforms.Create(new PlatformRequest("dxgi", Surfaceless: true), Device, fake.Make);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("gl-surfaceless", platform.Name);
        Assert.Equal(PresentPath.Offscreen, platform.Path);
        Assert.Equal(GpuApi.OpenGl, platform.Share.Api);
        Assert.Equal($"the DXGI path stopped at {word}: fake {word} failure; outputs present through OpenGL", platform.Words);
    }

    [Fact]
    public void AStartThatThrowsSomethingElseIsOpenGlWithWordsAndTheFuseWordsLead()
    {
        using var platform = GpuPlatforms.Create(new PlatformRequest("DXGI", Surfaceless: true) { Words = "fuse words" }, Device,
            _ => throw new DllNotFoundException("no d3d11.dll"));
        Assert.Equal("gl-surfaceless", platform.Name);
        Assert.Equal("fuse words; the DXGI path did not start: no d3d11.dll; outputs present through OpenGL", platform.Words);
    }

    [Fact]
    public void DxgiWithNoDxgiOnThisMachineIsOpenGlSayingWindowsOnly()
    {
        using var platform = GpuPlatforms.Create(new PlatformRequest("dxgi", Surfaceless: true), Device, null);
        Assert.Equal("gl-surfaceless", platform.Name);
        Assert.Equal("the DXGI path is Windows only: outputs present through OpenGL", platform.Words);
        if (!OperatingSystem.IsWindows())
        {
            // This machine's own maker: none off Windows.
            using var mine = GpuPlatforms.Create(new PlatformRequest("dxgi", Surfaceless: true), Device);
            Assert.Equal("the DXGI path is Windows only: outputs present through OpenGL", mine.Words);
        }
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("GL")]
    [InlineData("")]
    public void AutoAndGlAreOpenGlAndNeverAskForDxgi(string path)
    {
        var fake = new FailingDxgi(DxgiStartStage.Adapter);
        using var platform = GpuPlatforms.Create(new PlatformRequest(path, Surfaceless: true), Device, fake.Make);
        Assert.Equal(0, fake.Calls);
        Assert.Equal("gl-surfaceless", platform.Name);
        Assert.Equal("", platform.Words);
    }
}
