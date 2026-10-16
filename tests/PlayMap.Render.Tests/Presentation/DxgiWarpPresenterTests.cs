using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using PlayMap.Render.Platform.Windows;
using PlayMap.Render.Timing;
using Xunit.Abstractions;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>The DXGI flip path on the Windows CI runner, which has no GPU: the Direct3D 11 device on WARP, ANGLE's ES 3.0 context on
/// it with every shader program translated, a waitable FLIP_DISCARD swap chain on a window, 120 presents with buffer 0 wrapped once,
/// the frame statistics read (or, where WARP gives none, the clock timing swap returns — printed, never skipped), and the frame read
/// back from buffer 0 with its top row first. Off Windows the test asserts the path is refused with words instead.</summary>
public sealed class DxgiWarpPresenterTests(ITestOutputHelper output)
{
    private const int W = 64;
    private const int H = 36;

    [Fact]
    [Trait("Category", "Windows")]
    public void TheFlipPathPresentsOnWarpAndReadsTheFrameBack()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("not Windows: the DXGI path is refused here (GpuPlatformFaultTests and PresentPathTests assert the words)");
            return;
        }
        using var platform = AnglePlatform.Create(new GlDeviceOptions(CheckErrors: true, DebugOutput: false), warp: true);
        Assert.True(platform.Warp);
        Assert.Equal("dxgi", platform.Name);
        Assert.Equal(PresentPath.DxgiFlip, platform.Path);
        Assert.Equal(GlDialect.Es300, platform.Context.Dialect);
        Assert.Equal(Core.Media.GpuApi.D3D11, platform.Share.Api);
        Assert.NotEqual(0, platform.Share.D3D11Device);
        output.WriteLine($"{platform.Adapter} · {platform.Context.Device.Renderer} · {platform.Context.Device.Version}");

        using (var renderer = new RenderEngine(platform.Context))
            renderer.WarmUp();

        var device = platform.Context.Device;
        using var picture = device.CreateTexture(W, H, TextureFormat.Rgba8);
        var rgba = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                // Top half red, bottom half blue: the read-back tells which row is the top.
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = y < H / 2 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255);
            }
        }
        device.Upload(picture, rgba, UploadFormat.Rgba8);

        using var presenter = (DxgiOutputWindow)platform.CreatePresenter(new OutputPresenterOptions("PlayMap WARP test", null, false, W, H, 0, 0));
        Assert.Equal(1, presenter.SwapInterval);
        var clock = new PresentClock(60, measured: true, PresentPath.DxgiFlip);
        int queued = 0, held = 0, occluded = 0;
        for (var f = 0; f < 120; f++)
        {
            var r = presenter.Present(picture);
            clock.OnPresented(in r);
            presenter.Observe(clock);
            switch (r.Problem)
            {
                case PresentProblem.None:
                    queued++;
                    Assert.NotEqual(0u, r.PresentCount);
                    break;
                case PresentProblem.Held:
                    held++;
                    break;
                case PresentProblem.Occluded:
                    occluded++;
                    break;
                default:
                    Assert.Fail($"present {f} came back {r.Problem}");
                    break;
            }
        }
        output.WriteLine($"120 presents: {queued} queued, {held} held, {occluded} occluded; buffer 0 wrapped {presenter.BackBufferWraps}×");
        Assert.Equal(120, queued + held + occluded);
        Assert.True(queued > 0, "no present was queued");
        Assert.Equal(1, presenter.BackBufferWraps);
        if (clock.DisplayTimed)
        {
            output.WriteLine($"frame statistics read: {clock.Vblank.Count} vblank samples, {clock.Vblank.RefreshHz:0.###} Hz, {clock.Ledger.Shown} shown");
            Assert.True(clock.Vblank.Count > 0);
        }
        else
        {
            output.WriteLine($"WARP gave no frame statistics ({presenter.StatsMisses} reads answered nothing new): the clock times swap returns, evidence none");
            Assert.True(presenter.StatsMisses > 0);
        }
        Assert.Equal(queued, clock.Ledger.Presented);

        var bgra = new byte[W * H * 4];
        Assert.True(presenter.ReadBack(picture, bgra));
        // BGRA: the top row red, the bottom row blue.
        Assert.Equal((0, 0, 255), (bgra[0], bgra[1], bgra[2]));
        var last = (H - 1) * W * 4;
        Assert.Equal((255, 0, 0), (bgra[last], bgra[last + 1], bgra[last + 2]));
    }
}
