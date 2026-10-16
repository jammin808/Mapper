using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Core.Time;
using PlayMap.Media.Video.Spout;
using Xunit.Abstractions;

namespace PlayMap.Media.Tests.Video.Spout;

/// <summary>Spout on the Windows CI runner (WARP, no GPU): the real kernel's named objects and two WARP devices in one process — a
/// sender publishes a 64×36 chart, a receiver opens its texture by the share handle and reads it back through staging. Where WARP
/// shares no resources the test says so and runs the protocol goldens over the real kernel with the fake GPU instead — printed which
/// ran, never a skip. Off Windows it says the round trip is a Windows test (the goldens run in SpoutProtocolTests).</summary>
public sealed class SpoutWarpTests(ITestOutputHelper output)
{
    private const int W = 64;
    private const int H = 36;

    private static byte[] Chart()
    {
        var chart = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                (chart[i], chart[i + 1], chart[i + 2], chart[i + 3]) = ((byte)(x * 4), (byte)(y * 7), (byte)(x ^ y), 255);
            }
        return chart;
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ASenderAndAReceiverRoundTripAChartOnWarp()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("not Windows: the WARP round trip runs on the Windows CI job; the protocol goldens ran in SpoutProtocolTests");
            return;
        }
        var kernel = new Win32SpoutKernel();
        var name = "PlayMap CI " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var sendGpu = D3D11SpoutGpu.Create(warp: true);
        using var receiveGpu = D3D11SpoutGpu.Create(warp: true);
        using var sender = new SpoutSender(kernel, sendGpu, name, SpoutFormats.Bgra8, new SpoutDataWriter("ci", "sd_ci", "output:op_1", "60/1"));
        sender.Register(W, H);
        Assert.Contains(SpoutDirectory.Read(kernel), s => s.Name == name);
        var chart = Chart();
        Assert.True(sender.Publish(chart, W * 4));

        using var receiver = new SpoutReceiver(kernel, receiveGpu, name);
        var step = receiver.Step();
        if (step == ReceiveStep.Absent)
        {
            output.WriteLine($"WARP shares no resources here ({receiver.Words}): the goldens over the real kernel ran instead");
            KernelGoldens(kernel, name, sender);
            return;
        }
        Assert.Equal(ReceiveStep.Copied, step);
        var picture = new byte[W * H * 4];
        var read = StagingRead.Pending;
        for (var tries = 0; tries < 10_000 && read == StagingRead.Pending; tries++)
            read = receiver.ReadPending(picture, W * 4);
        Assert.Equal(StagingRead.Ready, read);
        Assert.Equal(chart, picture);
        Assert.Equal(ReceiveStep.NotNew, receiver.Step());
        output.WriteLine($"round trip of a {W}×{H} chart over WARP: handle 0x{receiver.Info.ShareHandle:X}, frame count {receiver.LastCount}");
    }

    /// <summary>The kernel half of the protocol on Windows' own objects: the counter net +1 per frame, the receiver's count read.</summary>
    [SupportedOSPlatform("windows")]
    private static void KernelGoldens(Win32SpoutKernel kernel, string name, SpoutSender sender)
    {
        using var counter = kernel.OpenSemaphore(SpoutNames.Counter(name));
        Assert.NotNull(counter);
        Assert.True(counter.TryTake(0));
        var before = counter.Release(1);
        Assert.True(sender.Publish(Chart(), W * 4));
        Assert.True(counter.TryTake(0));
        Assert.Equal(before + 1, counter.Release(1));
        using var access = kernel.OpenMutex(SpoutNames.AccessMutex(name));
        Assert.NotNull(access);
        Assert.Equal(LockTry.Acquired, access.TryEnter(0));
        access.Exit();
        Assert.Contains("\"send\":\"sd_ci\"", SpoutDataWriter.Read(kernel.OpenMap(SpoutNames.DataMap(name))!.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Windows")]
    [SupportedOSPlatform("windows")]
    public void SpoutsDeviceHasTheSdksSlotsAndLayouts()
    {
        // The same goldens as the renderer's DxgiLayoutTests (mingw-w64 11.0.1 headers, x86_64).
        Assert.Equal((5, 28, 40), (SpoutComSlots.CreateTexture2D, SpoutComSlots.OpenSharedResource, SpoutComSlots.GetImmediateContext));
        Assert.Equal((14, 15, 47, 48, 111), (SpoutComSlots.Map, SpoutComSlots.Unmap, SpoutComSlots.CopyResource, SpoutComSlots.UpdateSubresource, SpoutComSlots.Flush));
        Assert.Equal((10, 8, 8, 9), (SpoutComSlots.GetDesc, SpoutComSlots.GetSharedHandle, SpoutComSlots.AcquireSync, SpoutComSlots.ReleaseSync));
        Assert.Equal((44, 16, 8), (SpoutComSlots.TextureDescSize, SpoutComSlots.MappedSize, SpoutComSlots.MappedRowPitch));
    }
}

/// <summary>Spout's device's slots and layouts (constants and sizes only: they read the same on every platform).</summary>
[SupportedOSPlatform("windows")]
internal static class SpoutComSlots
{
    public static int CreateTexture2D => SpoutCom.DeviceCreateTexture2D;
    public static int OpenSharedResource => SpoutCom.DeviceOpenSharedResource;
    public static int GetImmediateContext => SpoutCom.DeviceGetImmediateContext;
    public static int Map => SpoutCom.ContextMap;
    public static int Unmap => SpoutCom.ContextUnmap;
    public static int CopyResource => SpoutCom.ContextCopyResource;
    public static int UpdateSubresource => SpoutCom.ContextUpdateSubresource;
    public static int Flush => SpoutCom.ContextFlush;
    public static int GetDesc => SpoutCom.Texture2DGetDesc;
    public static int GetSharedHandle => SpoutCom.ResourceGetSharedHandle;
    public static int AcquireSync => SpoutCom.KeyedMutexAcquireSync;
    public static int ReleaseSync => SpoutCom.KeyedMutexReleaseSync;
    public static int TextureDescSize => Unsafe.SizeOf<SpoutTextureDesc>();
    public static int MappedSize => Unsafe.SizeOf<SpoutMapped>();
    public static int MappedRowPitch => (int)Marshal.OffsetOf<SpoutMapped>("<RowPitch>k__BackingField");
}
