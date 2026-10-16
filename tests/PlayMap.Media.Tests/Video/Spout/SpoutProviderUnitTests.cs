using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Video.Spout;

namespace PlayMap.Media.Tests.Video.Spout;

/// <summary>The provider as videoio sees it: off Windows with no kernel it is unavailable with "Spout is Windows only"; over a fake
/// kernel its discovery lists senders, an input it opens receives frames into the pool lock-step (BGRA, the pending copy read back one
/// step later), and a send it makes takes the send stage's packed frames, converts them and publishes them under its default name,
/// sending one black frame when it stops.</summary>
public sealed class SpoutProviderUnitTests
{
    private static readonly ManualClock Clock = new(1_000_000_000);

    private static SpoutProvider Fake(FakeSpoutKernel kernel, FakeSpoutGpu gpu) =>
        new(new SpoutOptions("stage", DiscoveryMs: 0, ReceivePollMs: 0) { UnixMs = () => 1_760_000_000_000 }, Clock, kernel, () => gpu);

    [Fact]
    public void WithNoKernelItIsUnavailableSayingWindowsOnly()
    {
        using var provider = new SpoutProvider(new SpoutOptions("stage", 0, 0), Clock, null, null);
        Assert.Equal("unavailable", provider.Status.State);
        Assert.Equal("Spout is Windows only", provider.Status.Words);
        Assert.Equal(LiveProtocol.Spout, provider.Protocol);
        Assert.Equal("Spout is Windows only", Assert.Throws<NotSupportedException>(() => ((ISendProvider)provider).Create(
            new SendSpec("sd_1", "Main", 64, 36, 60, 1, SendFormat.Bgra8, "default", "", "", false))).Message);
        if (!OperatingSystem.IsWindows())
            Assert.Equal("Spout is Windows only", SpoutProvider.ForThisMachine(new SpoutOptions("stage", 0, 0), Clock).Status.Words);
    }

    [Fact]
    public void DiscoveryAndAnInputReceiveLockStep()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu();
        using var provider = Fake(kernel, gpu);
        Assert.Equal("ready", provider.Status.State);
        using var sender = new SpoutSender(kernel, gpu, "Arena", SpoutFormats.Bgra8);
        sender.Register(4, 2);
        var changed = 0;
        provider.EndpointsChanged += () => changed++;
        provider.Refresh();
        var e = Assert.Single(provider.Endpoints);
        Assert.Equal(("spout:Arena", "Arena", 1_760_000_000_000L), (e.Uri, e.FullName, e.SeenUnixMs));
        Assert.Equal(1, changed);
        provider.Refresh();
        Assert.Equal(1, changed);

        using var source = (SpoutSource)provider.Open("Arena", false, LiveSettings.Default, "md_arena");
        Assert.Equal(LiveState.Connecting, source.Live.State);
        var frame = new byte[4 * 2 * 4];
        Array.Fill(frame, (byte)200);
        sender.Publish(frame, 16);
        source.Poll();
        Assert.Equal(0, source.Received);
        source.Poll();
        Assert.Equal(1, source.Received);
        using (var lease = source.Acquire(0))
        {
            Assert.NotNull(lease);
            Assert.Equal((PixelLayout.Bgra8, 4, 2), (lease.Layout, lease.Width, lease.Height));
        }
        Assert.Equal(LiveState.Ok, source.Live.State);

        // spout:* follows the active sender.
        using var active = (SpoutSource)provider.Open("*", false, LiveSettings.Default, "md_any");
        active.Poll();
        Assert.Equal("Arena", active.Receiver!.Name);
    }

    /// <summary>A GPU slower than the receive step (each copy reads back one step late) while the sender draws a new frame every step:
    /// a copy still in flight is read back before another is made over it, so frames keep arriving, and every copy made is delivered
    /// (or still in flight) — none is lost unsaid.</summary>
    [Fact]
    public void ACopyStillInFlightIsReadBackBeforeAnotherIsMadeOverIt()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu { PendingReads = 1 };
        using var provider = Fake(kernel, gpu);
        using var sender = new SpoutSender(kernel, gpu, "Arena", SpoutFormats.Bgra8);
        sender.Register(4, 2);
        using var source = (SpoutSource)provider.Open("Arena", false, LiveSettings.Default, "md_arena");
        var frame = new byte[4 * 2 * 4];
        for (var step = 0; step < 12; step++)
        {
            Array.Fill(frame, (byte)(10 + step));
            Assert.True(sender.Publish(frame, 16));
            source.Poll();
        }
        Assert.True(source.Received >= 3, $"{source.Received} frames arrived in 12 steps with every copy one step late");
        var pending = source.Receiver!.HasPending ? 1 : 0;
        Assert.Equal(source.Receiver.Copies, source.Received + pending);
    }

    [Fact]
    public async Task ASendPublishesTheSendStagesFramesAndBlackWhenItStops()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu();
        using var provider = Fake(kernel, gpu);
        var spec = new SendSpec("sd_1", "Main", 4, 2, 60, 1, SendFormat.Uyvy, "default", "", "", false);
        var sink = ((ISendProvider)provider).Create(spec);
        Assert.Equal("PlayMap stage - Main", sink.FullName);
        Assert.Equal(1, sink.Connections);
        Assert.Contains("PlayMap stage - Main", SpoutNames.Read(kernel.MapBytes(SpoutNames.List)));
        var texture = gpu.Sender(SpoutInfo.Read(kernel.MapBytes("PlayMap stage - Main")).ShareHandle)!;

        // A white UYVY frame from the send stage arrives white in the shared texture.
        using (var white = new TestFrame(4, 2, 8, SendFormat.Uyvy, [128, 235, 128, 235]))
        {
            sink.Offer(white);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (sink.Stats.Sent == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(2);
        }
        Assert.Equal(1, sink.Stats.Sent);
        Assert.All(texture.Rows, b => Assert.Equal(255, b));
        // A receiver holding the texture past the worker's wait: the frame is skipped and counted dropped, never counted sent.
        Assert.True(kernel.HoldByAnotherApp(SpoutNames.AccessMutex("PlayMap stage - Main")));
        using (var grey = new TestFrame(4, 2, 8, SendFormat.Uyvy, [128, 126, 128, 126]))
        {
            sink.Offer(grey);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (sink.Stats.Dropped == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(2);
        }
        Assert.Equal(1, sink.Stats.Dropped);
        Assert.Equal(1, sink.Stats.Sent);
        Assert.Contains("held", sink.Words, StringComparison.Ordinal);
        Assert.All(texture.Rows, b => Assert.Equal(255, b));
        kernel.ReleaseByAnotherApp(SpoutNames.AccessMutex("PlayMap stage - Main"));
        sink.Dispose();
        // Stopping sent one black frame, then the name went from the list.
        Assert.Equal([0, 0, 0, 255], texture.Rows[..4]);
        Assert.DoesNotContain("PlayMap stage - Main", SpoutNames.Read(kernel.MapBytes(SpoutNames.List) ?? []));
    }

    /// <summary>A packed frame of one repeated pattern, freed on release.</summary>
    private sealed unsafe class TestFrame : SendFrame, IDisposable
    {
        private readonly byte[] _bytes;
        private System.Runtime.InteropServices.GCHandle _pin;

        public TestFrame(int width, int height, int stride, SendFormat format, byte[] pattern)
        {
            _bytes = new byte[stride * height];
            for (var i = 0; i < _bytes.Length; i++)
                _bytes[i] = pattern[i % pattern.Length];
            _pin = System.Runtime.InteropServices.GCHandle.Alloc(_bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            Data = _pin.AddrOfPinnedObject();
            Bytes = _bytes.Length;
            Stride = stride;
            Width = width;
            Height = height;
            Format = format;
            FpsN = 60;
            FpsD = 1;
        }

        public override void Release()
        {
        }

        public void Dispose()
        {
            if (_pin.IsAllocated)
                _pin.Free();
        }
    }
}
