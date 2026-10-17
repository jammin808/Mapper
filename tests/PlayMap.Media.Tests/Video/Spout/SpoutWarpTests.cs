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
/// ran, never a skip on Windows. Off Windows it is skipped (the protocol goldens run there in SpoutProtocolTests).</summary>
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

    [WindowsFact]
    [Trait("Category", "Windows")]
    [SupportedOSPlatform("windows")]
    public void ASenderAndAReceiverRoundTripAChartOnWarp()
    {
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
        // The read waits for the fact it needs: the copy's end mark on the GPU (WARP runs it on its own threads, at its own pace).
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!receiver.PendingCopied)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "the GPU never finished the copy");
            Thread.Yield();
        }
        output.WriteLine($"the copy was done {waited.Elapsed.TotalMilliseconds:0.0} ms after it was queued");
        Assert.Equal(StagingRead.Ready, receiver.ReadPending(picture, W * 4));
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

    /// <summary>Looking for a sender that is not there (a receiver waiting for it, discovery reading a stale name) is a question to
    /// the kernel, not an exception: none is thrown on this thread.</summary>
    [WindowsFact]
    [Trait("Category", "Windows")]
    [SupportedOSPlatform("windows")]
    public void LookingForAnAbsentSenderThrowsNothing()
    {
        var kernel = new Win32SpoutKernel();
        var name = "PlayMap absent " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var thread = Environment.CurrentManagedThreadId;
        var thrown = 0;
        void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread)
                Interlocked.Increment(ref thrown);
        }
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            Assert.Null(kernel.OpenMap(name));
            using var receiver = new SpoutReceiver(kernel, new FakeSpoutGpu(), name);
            Assert.Equal(ReceiveStep.Absent, receiver.Step());
            Assert.Equal(ReceiveStep.Absent, receiver.Step());
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }
        Assert.Equal(0, thrown);
    }

    [Fact]
    [Trait("Category", "Windows")]
    [SupportedOSPlatform("windows")]
    public void SpoutsDeviceHasTheSdksSlotsAndLayouts()
    {
        // The same goldens as the renderer's DxgiLayoutTests (mingw-w64 11.0.1 headers, x86_64).
        Assert.Equal((5, 24, 28, 40), (SpoutComSlots.CreateTexture2D, SpoutComSlots.CreateQuery, SpoutComSlots.OpenSharedResource, SpoutComSlots.GetImmediateContext));
        Assert.Equal((14, 15, 28, 29, 47, 48, 111), (SpoutComSlots.Map, SpoutComSlots.Unmap, SpoutComSlots.End, SpoutComSlots.GetData, SpoutComSlots.CopyResource,
            SpoutComSlots.UpdateSubresource, SpoutComSlots.Flush));
        Assert.Equal(8, SpoutComSlots.QueryDescSize);
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
    public static int CreateQuery => SpoutCom.DeviceCreateQuery;
    public static int End => SpoutCom.ContextEnd;
    public static int GetData => SpoutCom.ContextGetData;
    public static int QueryDescSize => Unsafe.SizeOf<SpoutQueryDesc>();
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
