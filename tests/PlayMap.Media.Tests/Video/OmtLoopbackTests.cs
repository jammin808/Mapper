using System.Diagnostics;
using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Video;
using PlayMap.Media.Video.Omt;

namespace PlayMap.Media.Tests.Video;

/// <summary>A packed frame in managed memory, pinned while the sender may read it, released once.</summary>
internal sealed class ArrayFrame : SendFrame
{
    private readonly GCHandle _pin;
    private int _released;

    public ArrayFrame(byte[] bytes, int width, int height, SendFormat format, long leaderNs, int stride)
    {
        _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Data = _pin.AddrOfPinnedObject();
        Bytes = bytes.Length;
        Stride = stride;
        Width = width;
        Height = height;
        Format = format;
        LeaderNs = leaderNs;
        FpsN = 60;
        FpsD = 1;
    }

    public bool IsReleased => Volatile.Read(ref _released) != 0;

    public override void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _pin.Free();
    }
}

/// <summary>OMT for real on this machine's loopback, with libvmx built from the vendored source: frames sent are received lock-step
/// (each counted as delivered before the next is sent: no pacing, no sleeps), in order by their stamps, within a code of luma; with
/// alpha; BGRA; the 1/8 preview; tally from receiver to sender; the sender's quality; and no avahi-daemon or IPv6 is needed.</summary>
[Collection("omt")]
public sealed class OmtLoopbackTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private readonly OmtProvider _omt = new(new OmtOptions(Path.Combine(Path.GetTempPath(), "playmap-omt-tests")), MonoClock.Instance);

    private static async Task Until(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, "never: " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>A picture of flat 16-pixel blocks whose colours move with <paramref name="k"/>, alpha a ramp of blocks.</summary>
    private static float[] Blocks(int w, int h, int k)
    {
        var rgba = new float[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var b = (y / 16) * 131 + (x / 16) * 7 + k * 29;
                var o = (y * w + x) * 4;
                rgba[o] = (b * 37 % 200 + 28) / 255f;
                rgba[o + 1] = (b * 91 % 200 + 28) / 255f;
                rgba[o + 2] = (b * 53 % 200 + 28) / 255f;
                rgba[o + 3] = (x / 16 * 16 % 256) / 255f;
            }
        }
        return rgba;
    }

    private static (int Bytes, int Stride) Shape(SendFormat format, int w, int h) => format switch
    {
        SendFormat.Uyvy => (2 * w * h, 2 * w),
        SendFormat.Uyva => (3 * w * h, 2 * w),
        SendFormat.P216 => (4 * w * h, 2 * w),
        SendFormat.Pa16 => (6 * w * h, 2 * w),
        _ => (4 * w * h, 4 * w),
    };

    private static PixelLayout LayoutOf(SendFormat format) =>
        format switch
        {
            SendFormat.Uyvy => PixelLayout.Uyvy,
            SendFormat.Uyva => PixelLayout.Uyva,
            SendFormat.P216 => PixelLayout.P216,
            SendFormat.Pa16 => PixelLayout.Pa16,
            _ => PixelLayout.Bgra8,
        };

    private static bool Deep(SendFormat format) => format is SendFormat.P216 or SendFormat.Pa16;

    [OmtTheory]
    [InlineData(SendFormat.Uyvy)]
    [InlineData(SendFormat.Uyva)]
    [InlineData(SendFormat.Bgra8)]
    [InlineData(SendFormat.P216)]
    [InlineData(SendFormat.Pa16)]
    public async Task FramesSentAre1080pReceivedLockStepInOrderWithinACode(SendFormat format)
    {
        const int w = 1920, h = 1080, n = 12;
        var alpha = format is not (SendFormat.Uyvy or SendFormat.P216);
        using var sink = (OmtSink)_omt.Create(new SendSpec("sd_t", "Loop " + format, w, h, 60, 1, format, "default", "", "", alpha));
        var url = sink.Url["omt://".Length..];
        // A 16-bit sender is received at 16 bits when the input asks for depth.
        var settings = LiveSettings.Default with { Depth = Deep(format) ? LiveDepth.High : LiveDepth.Auto };
        using var source = _omt.Open(url, byUrl: true, settings, "in_t");
        await Until(() => sink.Connections > 0, "the receiver connected");
        var (bytes, stride) = Shape(format, w, h);
        var layout = LayoutOf(format);
        var lastStamp = long.MinValue;
        for (var k = 0; k < n; k++)
        {
            var picture = Blocks(w, h, k);
            var packed = PackReference.Pack(layout, picture, w, h);
            Assert.Equal(bytes, packed.Length);
            var leaderNs = (k + 1) * 16_666_600L;
            var frame = new ArrayFrame(packed, w, h, format, leaderNs, stride);
            sink.Offer(frame);
            // Lock-step: the next frame goes only once this one is received.
            await Until(() => source.Live.Received == k + 1, $"frame {k} received");
            await Until(() => frame.IsReleased, $"frame {k} released by the sender");
            using var lease = source.Acquire(0)!;
            Assert.Equal(w, lease.Width);
            Assert.Equal(h, lease.Height);
            // The receiver asks for UYVY, or UYVA when the sender has alpha (a BGRA sender's alpha arrives as UYVA); P216 or PA16 when
            // the sender is 16-bit and the input asks for depth.
            Assert.Equal(Deep(format) ? layout : alpha ? PixelLayout.Uyva : PixelLayout.Uyvy, lease.Layout);
            // In order: the sender's stamp is the leader instant in 100 ns.
            Assert.Equal(leaderNs / 100 * 100, lease.SenderTimestampNs);
            Assert.True(lease.SenderTimestampNs > lastStamp);
            lastStamp = lease.SenderTimestampNs;
            Assert.True(lease.ArrivalNs > 0);
            CompareInteriors(format, packed, lease, w, h, k);
        }
        Assert.Equal(n, sink.Stats.Sent);
        Assert.Equal(0, source.Live.Dropped);
        Assert.Equal("ok", PlayMap.Media.Video.LiveSource.StateWord(source.Live.State));
    }

    /// <summary>Luma (and alpha) at the middle of every 16-pixel block within a code of what was sent (an 8-bit code: 256 of 16 bits).</summary>
    private static unsafe void CompareInteriors(SendFormat format, byte[] sent, VideoFrameLease lease, int w, int h, int k)
    {
        var planes = lease.Planes;
        if (Deep(format))
        {
            var worst16 = 0;
            for (var y = 8; y < h; y += 16)
            {
                for (var x = 8; x < w; x += 16)
                {
                    var o = y * 2 * w + 2 * x;
                    var ySent = sent[o] | (sent[o + 1] << 8);
                    var yGot = ((ushort*)((byte*)planes[0].Data + (long)y * planes[0].Stride))[x];
                    worst16 = Math.Max(worst16, Math.Abs(yGot - ySent));
                    if (format == SendFormat.Pa16)
                    {
                        var a = 4 * w * h + o;
                        var aSent = sent[a] | (sent[a + 1] << 8);
                        var aGot = ((ushort*)((byte*)planes[2].Data + (long)y * planes[2].Stride))[x];
                        worst16 = Math.Max(worst16, Math.Abs(aGot - aSent));
                    }
                }
            }
            Assert.True(worst16 <= 256, $"{format} frame {k}: {worst16} codes of 65535");
            return;
        }
        var worst = 0;
        for (var y = 8; y < h; y += 16)
        {
            for (var x = 8; x < w; x += 16)
            {
                int ySent, aSent;
                if (format == SendFormat.Bgra8)
                {
                    var o = (y * w + x) * 4;
                    ySent = (int)MathF.Floor(PackReference.Encode(sent[o + 2] / 255f, sent[o + 1] / 255f, sent[o] / 255f).Y + 0.5f);
                    aSent = sent[o + 3];
                }
                else
                {
                    ySent = sent[y * 2 * w + 2 * x + 1];
                    aSent = format == SendFormat.Uyva ? sent[2 * w * h + y * w + x] : 255;
                }
                var yGot = ((byte*)planes[0].Data)[(long)y * planes[0].Stride + 2 * x + 1];
                worst = Math.Max(worst, Math.Abs(yGot - ySent));
                if (format != SendFormat.Uyvy)
                {
                    var aGot = ((byte*)planes[1].Data)[(long)y * planes[1].Stride + x];
                    worst = Math.Max(worst, Math.Abs(aGot - aSent));
                }
            }
        }
        Assert.True(worst <= 1, $"{format} frame {k}: {worst} codes");
    }

    [OmtFact]
    public async Task AProxyReceivesTheEighthSizePreview()
    {
        const int w = 1920, h = 1080;
        using var sink = (OmtSink)_omt.Create(new SendSpec("sd_p", "Preview loop", w, h, 60, 1, SendFormat.Uyvy, "default", "", "", false));
        using var source = _omt.Open(sink.Url["omt://".Length..], byUrl: true, LiveSettings.Default with { Proxy = true }, "in_p");
        await Until(() => sink.Connections > 0, "the receiver connected");
        var packed = PackReference.Pack(PixelLayout.Uyvy, Blocks(w, h, 3), w, h);
        sink.Offer(new ArrayFrame(packed, w, h, SendFormat.Uyvy, 16_666_600, 2 * w));
        await Until(() => source.Live.Received >= 1, "the preview received");
        using var lease = source.Acquire(0)!;
        Assert.Equal((240, 135), (lease.Width, lease.Height));
    }

    [OmtFact]
    public async Task TallyReachesTheSenderAndTheSendersQualityChangesInPlace()
    {
        const int w = 64, h = 32;
        using var sink = (OmtSink)_omt.Create(new SendSpec("sd_y", "Tally loop", w, h, 60, 1, SendFormat.Uyvy, "default", "", "", false));
        using var source = _omt.Open(sink.Url["omt://".Length..], byUrl: true, LiveSettings.Default, "in_y");
        await Until(() => sink.Connections > 0, "the receiver connected");
        sink.Offer(new ArrayFrame(PackReference.Pack(PixelLayout.Uyvy, Blocks(w, h, 1), w, h), w, h, SendFormat.Uyvy, 16_666_600, 2 * w));
        await Until(() => source.Live.Received >= 1, "a frame received");
        source.SetTally(program: true, preview: false);
        await Until(() => sink.Tally == (true, false), "program tally at the sender");
        source.SetTally(program: false, preview: true);
        await Until(() => sink.Tally == (false, true), "preview tally at the sender");
        // The sender itself encodes at the new quality from its next frame: changed in place, not made again.
        Assert.Equal(libomtnet.OMTQuality.Default, sink.EncoderQuality);
        sink.Update(sink.Spec with { Quality = "high" });
        Assert.Equal(libomtnet.OMTQuality.High, sink.EncoderQuality);
    }

    [OmtFact]
    public void TheProviderIsReadyWithItsCodecAndSaysHowItDiscovers()
    {
        var status = _omt.Status;
        Assert.Equal("ready", status.State);
        Assert.EndsWith(OmtSetup.VmxFileName, status.Path, StringComparison.Ordinal);
        Assert.StartsWith("1.0.0.20", status.Version, StringComparison.Ordinal);
        Assert.Contains(_omt.Vmx, new[] { "avx2", "sse42", "neon" });
        var daemon = File.Exists("/run/avahi-daemon/socket") || File.Exists("/var/run/avahi-daemon/socket");
        if (OperatingSystem.IsLinux())
            Assert.Equal(daemon ? "avahi" : "none", _omt.Mdns);
    }

    public void Dispose() => _omt.Dispose();
}

/// <summary>OMT's discovery with no avahi-daemon (as on this machine) and no IPv6: it starts and says "none", a sender still starts
/// and listens on OMT's range, and disposing an avahi discovery returns cleanly — no abort from avahi's assertions on a null client,
/// no PlatformNotSupportedException from Thread.Abort (patches P1, P2, P3, P6). With neither mDNS nor a discovery server, discovery
/// lists nothing (a sender is reached by its address: omt://host:port).</summary>
[Collection("omt")]
public sealed class OmtDiscoveryTests
{
    [OmtFact]
    public void DiscoveryStartsWithNoDaemonSaysSoAndDisposesCleanly()
    {
        using var omt = new OmtProvider(new OmtOptions(Path.Combine(Path.GetTempPath(), "playmap-omt-tests")), MonoClock.Instance);
        using var sink = (OmtSink)omt.Create(new SendSpec("sd_d", "Discovered", 64, 32, 60, 1, SendFormat.Uyvy, "default", "", "", false));
        omt.Refresh();
        var daemon = File.Exists("/run/avahi-daemon/socket") || File.Exists("/var/run/avahi-daemon/socket");
        if (OperatingSystem.IsLinux() && !daemon)
        {
            Assert.Equal("none", omt.Mdns);
            Assert.Empty(omt.Endpoints);
            // A second avahi discovery made and disposed here: with no daemon its client is null, and its stop needs no abort.
            var avahi = typeof(libomtnet.OMTDiscovery).Assembly.GetType("libomtnet.linux.OMTDiscoveryAvahi", throwOnError: true)!;
            var discovery = (IDisposable)Activator.CreateInstance(avahi, nonPublic: true)!;
            discovery.Dispose();
        }
        // The sender listens on a port of OMT's range on either family (IPv4 only where the host has no IPv6).
        Assert.Matches(@"^omt://.+:6[4-6]\d\d$", sink.Url);
        Assert.EndsWith("(Discovered)", sink.FullName, StringComparison.Ordinal);
    }
}

[CollectionDefinition("omt", DisableParallelization = true)]
public sealed class OmtCollection;
