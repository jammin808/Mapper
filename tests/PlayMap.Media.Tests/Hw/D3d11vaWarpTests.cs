using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Hw;

namespace PlayMap.Media.Tests.Hw;

/// <summary>D3D11VA on the renderer's own device (Category=Windows: the Windows CI job runs it). A WARP device made with video
/// support stands in for wp2-d3d11's share; FFmpeg's D3D11VA device context is made on it (hwcontext_d3d11va.h: the caller sets
/// <c>device</c>, BindFlags and the lock callbacks before av_hwdevice_ctx_init) — it opens, or the test says why: WARP has no video
/// decoder, and the runner may have no FFmpeg. Elsewhere it asserts the share is refused without a device.</summary>
public sealed unsafe partial class D3d11vaWarpTests
{
    private const int DriverTypeWarp = 5;
    private const uint CreateBgraSupport = 0x20;
    private const uint CreateVideoSupport = 0x800;
    private const uint SdkVersion = 7;

    [Fact]
    [Trait("Category", "Windows")]
    public void TheHardwareDeviceOpensOnTheRenderersDeviceOrSaysWhy()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("", StreamDecoder.ZeroCopyDevice(GpuApi.OpenGl));
            Assert.Equal("d3d11va", StreamDecoder.ZeroCopyDevice(GpuApi.D3D11));
            Assert.Equal("vaapi", StreamDecoder.ZeroCopyDevice(GpuApi.DmaBuf));
            return;
        }
        OnWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void OnWindows()
    {
        nint device = 0;
        var hr = D3D11CreateDevice(0, DriverTypeWarp, 0, CreateBgraSupport | CreateVideoSupport, null, 0, SdkVersion, &device, null, null);
        if (hr < 0 || device == 0)
        {
            // WARP without video support: the words are the HRESULT, and nothing else is tried.
            Assert.NotEqual(0, hr);
            return;
        }
        try
        {
            if (!FfmpegLoader.TryLoad(out var problem))
            {
                Assert.False(string.IsNullOrWhiteSpace(problem), "FFmpeg neither loaded nor said why");
                return;
            }
            var share = new D3D11Share(device);
            var context = HwDecode.D3d11vaOnShare(share, out var why);
            if (context is null)
            {
                Assert.False(string.IsNullOrWhiteSpace(why), "the D3D11VA device neither opened nor said why");
                Assert.StartsWith("d3d11va", why, StringComparison.Ordinal);
                return;
            }
            var hw = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)context->data)->hwctx;
            Assert.Equal(device, (nint)hw->device);
            Assert.Equal(0x200u | 0x8u, hw->BindFlags);
            Assert.True(hw->@lock.Pointer != 0 && hw->unlock.Pointer != 0);
            ffmpeg.av_buffer_unref(&context);
            Assert.Equal(0, share.Depth);
        }
        finally
        {
            var vtable = *(nint**)device;
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(device);
        }
    }

    [LibraryImport("d3d11.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* featureLevels, uint levels,
        uint sdkVersion, nint* device, int* featureLevel, nint* context);

    /// <summary>The renderer's D3D11 device as wp2-d3d11's share would hand it over, with a recursive lock that counts its depth.</summary>
    private sealed class D3D11Share(nint device) : IGpuShare
    {
        private readonly Lock _lock = new();

        public int Depth { get; private set; }
        public GpuApi Api => GpuApi.D3D11;
        public string Describe => "WARP";
        public nint D3D11Device => device;
        public long AdapterLuid => 0;
        public string? DrmRenderNode => null;
        public long DeviceEpoch => 1;
        public bool DirectProven => false;

        public bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier) => true;

        public void Enter()
        {
            _lock.Enter();
            Depth++;
        }

        public void Exit()
        {
            Depth--;
            _lock.Exit();
        }

        public void ReportImportFailure(string sourceKey, string words)
        {
            // Nothing imports in this test.
        }

        public bool ImportFailed(string sourceKey, out string words)
        {
            words = "";
            return false;
        }
    }
}
