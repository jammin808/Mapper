using System.Diagnostics;
using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Video;
using PlayMap.Media.Video.Ndi;

namespace PlayMap.Media.Tests.Video;

/// <summary>NDI® through a runtime loaded at run time, here PlayMap's fake (tests/native/fake_ndi.c, built with cc): the layouts PlayMap
/// passes are the headers'; the loader finds the runtime where it is told and refuses with the words that say where to get one when it
/// is not; the finder lists the sources and is given this node's groups while the runtime reads its discovery server; a receiver
/// delivers timestamped frames in their own layout with their colour; the send ring publishes and never lets the runtime read a
/// frame after it is let go; tally goes both ways; 6.3's advertisers are made on a 6.3 runtime and not on 6.2.</summary>
[Collection("ndi")]
public sealed class NdiFakeRuntimeTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly FakeNdi.Control? _fake = FakeNdi.Dir is null ? null : new FakeNdi.Control(FakeNdi.Folder63);
    private readonly string _config = Directory.CreateTempSubdirectory("playmap-ndi-config-").FullName;

    public NdiFakeRuntimeTests() => _fake?.Reset();

    private NdiProvider Provider(string discovery = "", string groups = "", string? folder = null) =>
        new(new NdiOptions(folder ?? FakeNdi.Folder63, discovery, groups, _config), MonoClock.Instance);

    private static async Task Until(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, "never: " + what);
            await Task.Delay(5);
        }
    }

    [NdiFact]
    public unsafe void TheLayoutsPlayMapPassesAreTheHeaders()
    {
        // The fake's transcribed structs, measured by the C compiler (and checked against NDI's own 6.3 headers when it was written):
        // NDIlib_source_t 16, NDIlib_video_frame_v2_t 72 with FourCC at 8 and timestamp at 64, NDIlib_recv_create_v3_t 40, ...
        long[] ours =
        [
            sizeof(NdiSourceT), Off<NdiSourceT>("NdiName"), Off<NdiSourceT>("UrlAddress"),
            sizeof(NdiVideoFrame), Off<NdiVideoFrame>("XRes"), Off<NdiVideoFrame>("YRes"), Off<NdiVideoFrame>("FourCc"),
            Off<NdiVideoFrame>("FrameRateN"), Off<NdiVideoFrame>("FrameRateD"), Off<NdiVideoFrame>("PictureAspectRatio"),
            Off<NdiVideoFrame>("FrameFormatType"), Off<NdiVideoFrame>("Timecode"), Off<NdiVideoFrame>("Data"),
            Off<NdiVideoFrame>("LineStrideInBytes"), Off<NdiVideoFrame>("Metadata"), Off<NdiVideoFrame>("Timestamp"),
            sizeof(NdiMetadataFrame), Off<NdiMetadataFrame>("Length"), Off<NdiMetadataFrame>("Timecode"), Off<NdiMetadataFrame>("Data"),
            sizeof(NdiTally), Off<NdiTally>("OnProgram"), Off<NdiTally>("OnPreview"),
            sizeof(NdiFindCreate), Off<NdiFindCreate>("ShowLocalSources"), Off<NdiFindCreate>("Groups"), Off<NdiFindCreate>("ExtraIps"),
            sizeof(NdiRecvCreate), Off<NdiRecvCreate>("SourceToConnectTo"), Off<NdiRecvCreate>("ColorFormat"), Off<NdiRecvCreate>("Bandwidth"),
            Off<NdiRecvCreate>("AllowVideoFields"), Off<NdiRecvCreate>("RecvName"),
            sizeof(NdiSendCreate), Off<NdiSendCreate>("NdiName"), Off<NdiSendCreate>("Groups"), Off<NdiSendCreate>("ClockVideo"),
            Off<NdiSendCreate>("ClockAudio"),
            sizeof(NdiAdvertiserCreate), Off<NdiAdvertiserCreate>("UrlAddress"),
            sizeof(NdiAdvertiserCreate), Off<NdiAdvertiserCreate>("UrlAddress"),
            64,
        ];
        long[] headers =
        [
            16, 0, 8,
            72, 0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 56, 64,
            24, 0, 8, 16,
            2, 0, 1,
            24, 0, 8, 16,
            40, 0, 16, 20, 24, 32,
            24, 0, 8, 16, 17,
            8, 0,
            8, 0,
            64,
        ];
        Assert.Equal(headers, ours);
        Assert.Equal(headers, _fake!.Layout(headers.Length));
        // The planar float audio FourCC is 'FLTp' (0x70544C46), as the header defines it.
        Assert.Equal(0x70544C46, NdiFourCc.AudioFltp);
    }

    private static long Off<T>(string field) => Marshal.OffsetOf<T>(field).ToInt64();

    private static unsafe byte[] Bytes(FramePlane plane) => new ReadOnlySpan<byte>((void*)plane.Data, plane.Stride * plane.Height).ToArray();

    [NdiFact]
    public void TheLoaderFindsTheRuntimeWhereItIsToldAndAMissingOneRefusesWithWordsThatSayWhereToGetOne()
    {
        var runtime = NdiRuntime.Load(FakeNdi.Folder63, out var why);
        Assert.NotNull(runtime);
        Assert.Equal("", why);
        Assert.Equal(FakeNdi.Lib(FakeNdi.Folder63), runtime!.Path);
        Assert.Equal(new Version(6, 3, 2), runtime.Numbers);
        Assert.True(runtime.HasAdvertisers);
        Assert.Contains("advertisers", runtime.Features());
        Assert.Equal(new Version(6, 2, 1), NdiRuntime.ParseVersion("NDI SDK LINUX 6.2.1 rel"));
        Assert.Equal(new Version(6, 3, 0), NdiRuntime.ParseVersion("6.3"));

        // Nowhere it is told and none installed on this machine: absent, with the variable that points at it and where to get it.
        var empty = Directory.CreateTempSubdirectory("playmap-no-ndi-").FullName;
        if (Environment.GetEnvironmentVariable(NdiRuntime.DirVariable) is null && !NativeLibrary.TryLoad(NdiRuntime.FileName, out _)
            && NdiRuntime.Candidates("").All(c => !File.Exists(c)))
        {
            using var none = new NdiProvider(new NdiOptions(empty), MonoClock.Instance);
            var status = none.Status;
            Assert.Equal("absent", status.State);
            Assert.Contains(NdiRuntime.DirVariable, status.Words, StringComparison.Ordinal);
            Assert.Contains("https://ndi.video", status.Words, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(empty, NdiRuntime.FileName), status.Words, StringComparison.Ordinal);
            Assert.Equal("https://ndi.video", status.Link);
            Assert.Throws<InvalidOperationException>(() => none.Open("STUDIO (Cam 1)", false, LiveSettings.Default, "in"));
        }
    }

    [NdiFact]
    public void TheFinderListsTheSourcesWithThisNodesGroupsAndTheRuntimeReadsItsDiscoveryServer()
    {
        using var ndi = Provider(discovery: "10.0.0.2:5959", groups: "stage,foh");
        Assert.Equal("ready", ndi.Status.State);
        Assert.True(_fake!.InjectSource("STUDIO (Cam 1)", "10.0.0.5:5961"));
        Assert.True(_fake.InjectSource("FOH (Program)", "10.0.0.9:5961"));
        var changed = 0;
        ndi.EndpointsChanged += () => changed++;
        ndi.Refresh();
        Assert.Equal(["FOH (Program)", "STUDIO (Cam 1)"], ndi.Endpoints.Select(e => e.FullName));
        var cam = ndi.Endpoints[1];
        Assert.Equal(("ndi:STUDIO (Cam 1)", "STUDIO", "Cam 1", "10.0.0.5:5961"), (cam.Uri, cam.Host, cam.Name, cam.Address));
        Assert.Equal(1, changed);
        ndi.Refresh();
        Assert.Equal(1, changed);
        Assert.Equal("stage,foh", _fake.Strings(0));
        Assert.Equal("10.0.0.2:5959", _fake.Strings(3));
        Assert.Equal(1, _fake.Stats().FindsCreated);
    }

    [NdiFact]
    public async Task AReceiverDeliversTimestampedFramesInTheirOwnLayoutWithTheirColour()
    {
        using var ndi = Provider();
        Assert.True(_fake!.InjectSource("STUDIO (Cam 1)", "10.0.0.5:5961"));
        using var source = ndi.Open("STUDIO (Cam 1)", byUrl: false, LiveSettings.Default with { Proxy = true }, "md_cam");
        Assert.Equal(0, _fake.Stats().LastRecvBandwidth);
        Assert.Equal(100, _fake.Stats().LastRecvColorFormat);
        Assert.Equal("PlayMap md_cam", _fake.Strings(5));
        const int w = 64, h = 16;
        var rgba = Enumerable.Range(0, w * h * 4).Select(i => (i % 4 == 3 ? i / 4 % w : (i * 7 + i / 64) % 200 + 20) / 255f).ToArray();
        var cases = new (PixelLayout Layout, int FourCc, string? Meta, TransferFunction Transfer, ColorMatrix Matrix)[]
        {
            (PixelLayout.Uyvy, NdiFourCc.Uyvy, null, TransferFunction.Sdr, ColorMatrix.Bt601),
            (PixelLayout.Uyva, NdiFourCc.Uyva, null, TransferFunction.Sdr, ColorMatrix.Bt601),
            (PixelLayout.P216, NdiFourCc.P216, "<ndi_color_info transfer=\"bt_2100_pq\" matrix=\"bt_2020\" primaries=\"bt_2020\"/>", TransferFunction.Pq, ColorMatrix.Bt2020),
            (PixelLayout.Pa16, NdiFourCc.Pa16, "<ndi_color_info transfer=\"bt_2100_hlg\" matrix=\"bt_2020\"/>", TransferFunction.Hlg, ColorMatrix.Bt2020),
        };
        var n = 0;
        foreach (var c in cases)
        {
            var frame = PackReference.Pack(c.Layout, rgba, w, h);
            var ts = 1_000_000L * (n + 1);
            Assert.True(_fake.InjectFrame("STUDIO (Cam 1)", c.FourCc, w, h, frame, LivePlanes.Stride(c.Layout, 0, w), ts, c.Meta));
            n++;
            await Until(() => source.Live.Received == n, $"{c.Layout} received");
            using var lease = source.Acquire(0)!;
            Assert.Equal((c.Layout, w, h), (lease.Layout, lease.Width, lease.Height));
            Assert.Equal(ts * 100, lease.SenderTimestampNs);
            Assert.True(lease.ArrivalNs > 0);
            Assert.Equal(c.Transfer, lease.Transfer);
            Assert.Equal(c.Matrix, lease.Matrix);
            Assert.Equal(LivePlanes.HasAlpha(c.Layout), lease.HasAlpha);
            var got = new List<byte>();
            foreach (var plane in lease.Planes)
                got.AddRange(Bytes(plane));
            Assert.Equal(frame, got.ToArray());
        }
        Assert.Equal(LiveState.Ok, source.Live.State);
        Assert.Equal(1, source.Live.Connections);
    }

    [NdiFact]
    public async Task AnInterlacedSourcesFieldsAreWovenIntoOneFrameAndAPaddedStrideIsReadRowByRow()
    {
        using var ndi = Provider();
        Assert.True(_fake!.InjectSource("STUDIO (Cam 2)", "10.0.0.5:5962"));
        using var source = ndi.Open("STUDIO (Cam 2)", byUrl: false, LiveSettings.Default, "md_cam2");
        const int w = 32, h = 4, stride = 2 * w + 16;
        // A field of UYVY rows, each row's bytes its own code (field 0: 10 + row, field 1: 100 + row), with padding past each row.
        static byte[] Field(int baseCode)
        {
            var bytes = new byte[stride * h];
            for (var y = 0; y < h; y++)
                Array.Fill(bytes, (byte)(baseCode + y), y * stride, 2 * w);
            return bytes;
        }
        _fake.InjectFormat(2);
        Assert.True(_fake.InjectFrame("STUDIO (Cam 2)", NdiFourCc.Uyvy, w, h, Field(10), stride, 1_000));
        await Until(() => _fake.Stats().VideoCaptured == 1, "field 0 captured");
        // A field alone is not a picture: nothing is published until its pair is in.
        Assert.Equal(0, source.Live.Received);
        _fake.InjectFormat(3);
        Assert.True(_fake.InjectFrame("STUDIO (Cam 2)", NdiFourCc.Uyvy, w, h, Field(100), stride, 2_000));
        await Until(() => source.Live.Received == 1, "the woven frame");
        using (var lease = source.Acquire(0)!)
        {
            Assert.Equal((PixelLayout.Uyvy, w, 2 * h), (lease.Layout, lease.Width, lease.Height));
            // Field 0's stamp is the frame's.
            Assert.Equal(1_000L * 100, lease.SenderTimestampNs);
            var rows = Bytes(lease.Planes[0]);
            var tight = lease.Planes[0].Stride;
            Assert.Equal(2 * w, tight);
            for (var y = 0; y < 2 * h; y++)
                Assert.All(rows.AsSpan(y * tight, tight).ToArray(), b => Assert.Equal(y % 2 == 0 ? 10 + y / 2 : 100 + y / 2, b));
        }
        Assert.Equal(2 * h, source.Live.Height);

        // A field 1 with no field 0 before it is dropped, never published as a picture of half the height.
        Assert.True(_fake.InjectFrame("STUDIO (Cam 2)", NdiFourCc.Uyvy, w, h, Field(100), stride, 3_000));
        await Until(() => _fake.Stats().VideoCaptured == 3, "a lone field 1 captured");
        // A progressive frame after it is read whole, through its padded stride.
        _fake.InjectFormat(1);
        Assert.True(_fake.InjectFrame("STUDIO (Cam 2)", NdiFourCc.Uyvy, w, h, Field(40), stride, 4_000));
        await Until(() => source.Live.Received == 2, "the progressive frame");
        using (var lease = source.Acquire(0)!)
        {
            Assert.Equal((w, h), (lease.Width, lease.Height));
            Assert.Equal(4_000L * 100, lease.SenderTimestampNs);
            var rows = Bytes(lease.Planes[0]);
            for (var y = 0; y < h; y++)
                Assert.All(rows.AsSpan(y * 2 * w, 2 * w).ToArray(), b => Assert.Equal(40 + y, b));
        }
        Assert.True(source.Live.Dropped >= 1);
    }

    [Fact]
    public void AnUntaggedFrameTakesNdisColourBySizeAndATagSaysOtherwise()
    {
        Assert.Equal((ColorMatrix.Bt601, TransferFunction.Sdr), NdiSource.ColorInfo(0, 720, 576));
        Assert.Equal(ColorMatrix.Bt709, NdiSource.ColorInfo(0, 722, 480).Matrix);
        Assert.Equal(ColorMatrix.Bt709, NdiSource.ColorInfo(0, 640, 578).Matrix);
        Assert.Equal(ColorMatrix.Bt709, NdiSource.ColorInfo(0, 1920, 1080).Matrix);
        Assert.Equal(ColorMatrix.Bt2020, NdiSource.ColorInfo(0, 3840, 2160).Matrix);
        Assert.Equal(ColorMatrix.Bt2020, NdiSource.ColorInfo(0, 1920, 1082).Matrix);
        static (ColorMatrix, TransferFunction) Tagged(string xml, int w, int h)
        {
            var p = Marshal.StringToCoTaskMemUTF8(xml);
            try
            {
                return NdiSource.ColorInfo(p, w, h);
            }
            finally
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
        Assert.Equal((ColorMatrix.Bt709, TransferFunction.Sdr), Tagged("<ndi_color_info transfer=\"bt_709\" matrix=\"bt_709\" primaries=\"bt_709\"/>", 3840, 2160));
        Assert.Equal((ColorMatrix.Bt2020, TransferFunction.Pq),
            Tagged("<ndi_metadata_group><ndi_color_info primaries=\"bt_2100\" transfer=\"bt_2100_pq\" matrix=\"bt_2100\"/></ndi_metadata_group>", 1920, 1080));
        Assert.Equal((ColorMatrix.Bt601, TransferFunction.Hlg), Tagged("<ndi_color_info transfer=\"bt_2100_hlg\" matrix=\"bt_601\"/>", 1920, 1080));
        // Other metadata is no colour: the size's rule stands.
        Assert.Equal((ColorMatrix.Bt709, TransferFunction.Sdr), Tagged("<ndi_capabilities ntk_ptz=\"true\"/>", 1280, 720));
    }

    [NdiFact]
    public async Task TheSendRingPublishesAndTheRuntimeNeverReadsAFrameAfterItIsLetGo()
    {
        using var ndi = Provider(groups: "stage");
        const int w = 64, h = 16;
        using var sink = ndi.Create(new SendSpec("sd_m", "Main", w, h, 60, 1, SendFormat.Uyvy, "default", "", "", false));
        Assert.Equal("FAKEHOST (Main)", sink.FullName);
        Assert.Equal("stage", _fake!.Strings(2));
        Assert.Equal(0, _fake.Stats().LastSendClockVideo);
        using var source = ndi.Open("FAKEHOST (Main)", byUrl: false, LiveSettings.Default, "md_loop");
        var frames = new List<(ArrayFrame Frame, byte[] Bytes)>();
        for (var k = 0; k < 6; k++)
        {
            var bytes = PackReference.Pack(PixelLayout.Uyvy, Enumerable.Repeat((k * 30 + 20) / 255f, w * h * 4).ToArray(), w, h);
            var frame = new ArrayFrame(bytes, w, h, SendFormat.Uyvy, (k + 1) * 16_666_600L, 2 * w);
            frames.Add((frame, bytes));
            sink.Offer(frame);
            await Until(() => _fake.Stats().FramesSent == k + 1, $"frame {k} sent");
            await Until(() => source.Live.Received == k + 1, $"frame {k} received");
            using var lease = source.Acquire(0)!;
            Assert.Equal(bytes[1], Bytes(lease.Planes[0])[1]);
            // Every sent frame says BT.709: without it a 64×16 frame would be read as BT.601 (NDI's rule for SD sizes).
            Assert.Equal(ColorMatrix.Bt709, lease.Matrix);
            // The runtime stamps a frame at its submit time: frames arrive in the order sent.
            Assert.True(lease.SenderTimestampNs > 0);
            // A frame let go may be reused at once: scribbling over it never reaches the runtime, which no longer reads it.
            if (k > 0)
            {
                await Until(() => frames[k - 1].Frame.IsReleased, $"frame {k - 1} let go after frame {k}'s send");
                Array.Fill(frames[k - 1].Bytes, (byte)0xEE);
            }
        }
        // The last frame is let go when the send goes quiet (a send of nothing first), then the sender closes.
        await Until(() => frames[^1].Frame.IsReleased, "the last frame let go once the send is quiet");
        Array.Fill(frames[^1].Bytes, (byte)0xEE);
        sink.Dispose();
        Assert.Equal(0, _fake.Stats().LifetimeViolations);
        Assert.Equal(6, sink.Stats.Sent);
    }

    [NdiFact]
    public async Task TheFakeCatchesAFrameChangedBeforeItsSyncEvent()
    {
        // The check the previous test relies on, seen to fire: a buffer changed while the runtime may still read it is counted.
        using var ndi = Provider();
        const int w = 64, h = 16;
        using var sink = ndi.Create(new SendSpec("sd_v", "Violate", w, h, 60, 1, SendFormat.Uyvy, "default", "", "", false));
        var bytes = new byte[2 * w * h];
        var held = new ArrayFrame(bytes, w, h, SendFormat.Uyvy, 1, 2 * w);
        sink.Offer(held);
        await Until(() => _fake!.Stats().FramesSent == 1, "the first frame sent");
        Array.Fill(bytes, (byte)0x55);
        sink.Offer(new ArrayFrame(new byte[2 * w * h], w, h, SendFormat.Uyvy, 2, 2 * w));
        await Until(() => _fake!.Stats().FramesSent == 2, "the second frame sent");
        Assert.Equal(1, _fake!.Stats().LifetimeViolations);
    }

    [NdiFact]
    public async Task TallyGoesBothWays()
    {
        using var ndi = Provider();
        const int w = 64, h = 16;
        using var sink = ndi.Create(new SendSpec("sd_t", "Tally", w, h, 60, 1, SendFormat.Uyvy, "default", "", "", false));
        using var source = ndi.Open("FAKEHOST (Tally)", byUrl: false, LiveSettings.Default, "md_t");
        await Until(() => sink.Connections == 1, "the receiver connected");
        source.SetTally(program: true, preview: false);
        await Until(() => _fake!.Strings(6) == "1,0", "the receiver's tally at the runtime");
        await Until(() => sink.Tally == (true, false), "program at the sender");
        source.SetTally(program: false, preview: true);
        await Until(() => sink.Tally == (false, true), "preview at the sender");
        Assert.Equal("0,1", _fake!.Strings(6));
    }

    [NdiFact]
    public void TheAdvertisersAreMadeOnA63RuntimeAndNotOn62()
    {
        using (var ndi = Provider(discovery: "10.0.0.2:5959"))
        {
            Assert.True(ndi.Advertising);
            using var sink = ndi.Create(new SendSpec("sd_a", "Advertised", 64, 16, 60, 1, SendFormat.Uyvy, "default", "", "", false));
            using var source = ndi.Open("FAKEHOST (Advertised)", byUrl: false, LiveSettings.Default, "md_a");
            var s = _fake!.Stats();
            Assert.Equal((1, 1, 1, 1), (s.SendAdvertisers, s.RecvAdvertisers, s.SendersAdvertised, s.ReceiversAdvertised));
            Assert.Equal("10.0.0.2:5959", _fake.Strings(4));
        }
        var older = new FakeNdi.Control(FakeNdi.Folder62);
        older.Reset();
        older.SetVersion("NDI SDK LINUX 6.2.1 fake");
        using var ndi62 = Provider(discovery: "10.0.0.2:5959", folder: FakeNdi.Folder62);
        Assert.Equal("ready", ndi62.Status.State);
        Assert.StartsWith("NDI SDK LINUX 6.2.1", ndi62.Status.Version, StringComparison.Ordinal);
        Assert.False(ndi62.Advertising);
        using var sink62 = ndi62.Create(new SendSpec("sd_b", "Plain", 64, 16, 60, 1, SendFormat.Uyvy, "default", "", "", false));
        var s62 = older.Stats();
        Assert.Equal((0, 0, 0), (s62.SendAdvertisers, s62.RecvAdvertisers, s62.SendersAdvertised));
    }

    public void Dispose()
    {
        _fake?.Reset();
        Directory.Delete(_config, recursive: true);
    }
}

/// <summary>Opt-in: with PLAYMAP_NDI_DIR naming a folder that holds a real NDI 6 runtime (not the fake), 120 frames sent by this node
/// are received back by it through the runtime.</summary>
[Collection("ndi")]
public sealed class NdiRealRuntimeTests
{
    [NdiRealFact]
    public async Task ASendIsReceivedBackThroughTheRealRuntime()
    {
        using var ndi = new NdiProvider(new NdiOptions(Environment.GetEnvironmentVariable(NdiRuntime.DirVariable)!), MonoClock.Instance);
        Assert.Equal("ready", ndi.Status.State);
        const int w = 1280, h = 720;
        using var sink = ndi.Create(new SendSpec("sd_r", "PlayMap real loopback", w, h, 60, 1, SendFormat.Uyvy, "default", "", "", false));
        using var source = ndi.Open(sink.FullName, byUrl: false, LiveSettings.Default, "md_r");
        var waited = Stopwatch.StartNew();
        var bytes = PackReference.Pack(PixelLayout.Uyvy, Enumerable.Repeat(0.5f, w * h * 4).ToArray(), w, h);
        for (var k = 0; k < 120; k++)
        {
            sink.Offer(new ArrayFrame((byte[])bytes.Clone(), w, h, SendFormat.Uyvy, (k + 1) * 16_666_667L, 2 * w));
            while (source.Live.Received < k + 1 && waited.Elapsed < TimeSpan.FromSeconds(60))
                await Task.Delay(2);
        }
        Assert.True(source.Live.Received >= 120, $"{source.Live.Received} received");
    }
}

/// <summary>Runs only where PLAYMAP_NDI_DIR names a real NDI runtime (one without the fake's control exports).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class NdiRealFactAttribute : FactAttribute
{
    public NdiRealFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable(NdiRuntime.DirVariable);
        var file = dir is null ? null : Path.Combine(dir, NdiRuntime.FileName);
        if (file is null || !File.Exists(file))
            Skip = $"opt-in: set {NdiRuntime.DirVariable} to the folder of a real NDI 6 runtime (https://ndi.video) to run this";
        else if (NativeLibrary.TryLoad(file, out var h) && NativeLibrary.TryGetExport(h, "fake_ndi_reset", out _))
            Skip = $"{NdiRuntime.DirVariable} names the fake runtime; this test needs a real one";
    }
}

[CollectionDefinition("ndi", DisableParallelization = true)]
public sealed class NdiCollection;
