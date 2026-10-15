using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;

namespace PlayMap.Media.Video.Ndi;

/// <summary>How this node runs NDI: where to look for the user's runtime first, its discovery servers (written for the runtime to read
/// at load, and given to 6.3's advertisers) and groups (passed to every finder and sender), and the folder its configuration is
/// written to.</summary>
public sealed record NdiOptions(string RuntimePath = "", string Discovery = "", string Groups = "", string ConfigFolder = "");

/// <summary>NDI® in and out through the runtime the user installed (<see cref="NdiRuntime"/>): a finder with this node's groups,
/// receivers that copy each captured frame into the pool in its own packed layout and free the runtime's buffer at once, senders
/// that hand frames to the runtime asynchronously (the producer's frame is let go only at the runtime's next synchronising call), and
/// tally both ways. On a 6.3 or later runtime the send and receive advertisers are made and every sender and receiver is added to
/// them, so a Discovery Server sees them. Without a runtime the provider stays registered and says why, with where to get one.</summary>
public sealed unsafe class NdiProvider : ILiveInputProvider, ISendProvider, IVideoRuntimeFacts
{
    private readonly IMonoClock _clock;
    private readonly NdiOptions _options;
    private readonly NdiRuntime? _runtime;
    private readonly string _why;
    private readonly Lock _gate = new();
    private readonly nint _groups;
    private nint _finder;
    private nint _sendAdvertiser;
    private nint _recvAdvertiser;
    private IReadOnlyList<LiveEndpoint> _endpoints = [];
    private bool _initialized;
    private int _disposed;

    public NdiProvider(NdiOptions options, IMonoClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
        _groups = options.Groups.Length > 0 ? Marshal.StringToCoTaskMemUTF8(options.Groups) : 0;
        _runtime = NdiRuntime.Load(options.RuntimePath, out _why);
        if (_runtime is null)
            return;
        if (_runtime.IsSupportedCpu() == 0)
        {
            _why = "this CPU lacks what the NDI runtime needs (SSE4.2)";
            return;
        }
        WriteConfig();
        if (_runtime.Initialize() == 0)
        {
            _why = $"the NDI runtime at {_runtime.Path} would not initialise";
            return;
        }
        _initialized = true;
        if (_runtime.HasAdvertisers)
            MakeAdvertisers();
    }

    /// <summary>The runtime in use, or null.</summary>
    public NdiRuntime? Runtime => _initialized ? _runtime : null;

    public LiveProtocol Protocol => LiveProtocol.Ndi;

    public ProviderStatus Status => _initialized
        ? new ProviderStatus("ready", _runtime!.Version, _runtime.Path, "", NdiRuntime.Link)
        : new ProviderStatus(_runtime is null ? "absent" : "failed", _runtime?.Version ?? "", _runtime?.Path ?? "", _why, NdiRuntime.Link);

    public bool Advertising => _sendAdvertiser != 0 && _recvAdvertiser != 0;

    public IReadOnlyList<LiveEndpoint> Endpoints
    {
        get
        {
            lock (_gate)
                return _endpoints;
        }
    }

    public event Action? EndpointsChanged;

    /// <summary>The runtime reads its discovery servers from ndi-config.v1.json in NDI_CONFIG_DIR when it loads: written here, in
    /// the node's own folder, before the runtime initialises, only when this node names servers or groups (the user's own NDI
    /// configuration applies otherwise).</summary>
    private void WriteConfig()
    {
        if (_options.ConfigFolder.Length == 0 || (_options.Discovery.Length == 0 && _options.Groups.Length == 0))
            return;
        Directory.CreateDirectory(_options.ConfigFolder);
        var json = new StringBuilder();
        json.Append("{\"ndi\":{\"networks\":{\"discovery\":").Append(Quote(_options.Discovery)).Append("},")
            .Append("\"groups\":{\"send\":").Append(Quote(_options.Groups)).Append(",\"recv\":").Append(Quote(_options.Groups)).Append("}}}");
        File.WriteAllText(Path.Combine(_options.ConfigFolder, "ndi-config.v1.json"), json.ToString());
        NativeEnvironment.Set("NDI_CONFIG_DIR", _options.ConfigFolder);
    }

    private static string Quote(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private void MakeAdvertisers()
    {
        var url = _options.Discovery.Length > 0 ? Marshal.StringToCoTaskMemUTF8(_options.Discovery.Split(',')[0].Trim()) : 0;
        try
        {
            var create = new NdiAdvertiserCreate { UrlAddress = url };
            _sendAdvertiser = _runtime!.SendAdvertiserCreate(&create);
            _recvAdvertiser = _runtime.RecvAdvertiserCreate(&create);
        }
        finally
        {
            if (url != 0)
                Marshal.FreeCoTaskMem(url);
        }
    }

    internal void Advertise(nint sender)
    {
        if (_sendAdvertiser != 0)
            _runtime!.SendAdvertiserAddSender(_sendAdvertiser, sender, 1);
    }

    internal void Unadvertise(nint sender)
    {
        if (_sendAdvertiser != 0 && Volatile.Read(ref _disposed) == 0)
            _runtime!.SendAdvertiserDelSender(_sendAdvertiser, sender);
    }

    internal void AdvertiseReceiver(nint receiver)
    {
        if (_recvAdvertiser != 0)
            _runtime!.RecvAdvertiserAddReceiver(_recvAdvertiser, receiver, 0, 1, 0);
    }

    internal void UnadvertiseReceiver(nint receiver)
    {
        if (_recvAdvertiser != 0 && Volatile.Read(ref _disposed) == 0)
            _runtime!.RecvAdvertiserDelReceiver(_recvAdvertiser, receiver);
    }

    internal nint Groups => _groups;

    /// <summary>Reads the finder's sources now (the finder is made at the first refresh, with this node's groups).</summary>
    public void Refresh()
    {
        if (!_initialized || Volatile.Read(ref _disposed) != 0)
            return;
        List<LiveEndpoint> seen;
        lock (_gate)
        {
            if (_finder == 0)
            {
                var create = new NdiFindCreate { ShowLocalSources = 1, Groups = _groups, ExtraIps = 0 };
                _finder = _runtime!.FindCreate(&create);
                if (_finder == 0)
                    return;
            }
            _runtime!.FindWaitForSources(_finder, 0);
            uint count = 0;
            var sources = _runtime.FindGetCurrentSources(_finder, &count);
            seen = new List<LiveEndpoint>((int)count);
            var now = _clock.NowNs / 1_000_000;
            for (var i = 0; i < count && sources != null; i++)
            {
                var full = Marshal.PtrToStringUTF8(sources[i].NdiName) ?? "";
                var url = Marshal.PtrToStringUTF8(sources[i].UrlAddress) ?? "";
                if (full.Length == 0)
                    continue;
                var (host, name) = LiveUri.Split(full);
                seen.Add(new LiveEndpoint(LiveProtocol.Ndi, LiveUri.Format(LiveProtocol.Ndi, full), full, host, name, url, now));
            }
            seen.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            var changed = seen.Count != _endpoints.Count || seen.Where((e, i) => e.FullName != _endpoints[i].FullName || e.Address != _endpoints[i].Address).Any();
            if (!changed)
                return;
            _endpoints = seen;
        }
        EndpointsChanged?.Invoke();
    }

    public ILiveSource Open(string address, bool byUrl, LiveSettings settings, string key)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(settings);
        if (!_initialized)
            throw new InvalidOperationException(_why);
        return new NdiSource(this, _runtime!, address, byUrl, settings, key, _clock);
    }

    public ISendSink Create(SendSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_initialized)
            throw new InvalidOperationException(_why);
        return new NdiSink(this, _runtime!, spec);
    }

    /// <summary>Lets go of the finder and the advertisers, then the runtime's initialisation. Every source and sink is disposed first
    /// by its owner (the hub closes them before the providers go).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_gate)
        {
            if (_finder != 0)
                _runtime!.FindDestroy(_finder);
            _finder = 0;
        }
        if (_sendAdvertiser != 0)
            _runtime!.SendAdvertiserDestroy(_sendAdvertiser);
        if (_recvAdvertiser != 0)
            _runtime!.RecvAdvertiserDestroy(_recvAdvertiser);
        _sendAdvertiser = _recvAdvertiser = 0;
        if (_initialized)
            _runtime!.Destroy();
        _initialized = false;
        if (_groups != 0)
            Marshal.FreeCoTaskMem(_groups);
    }
}

/// <summary>One NDI receiver on a capture thread of its own ("ndi &lt;key&gt;"): each video frame is copied out of the runtime's buffer
/// into the pool in its packed layout (UYVY, UYVA, P216, PA16; BGRA as is) and the runtime's buffer is freed at once; the frame's
/// 100 ns timestamp is the sender's stamp (none when the runtime says undefined); per-frame <c>ndi_color_info</c> metadata sets the
/// matrix and transfer; an error frame means the sender went away. Tally reaches the sender from the same thread.</summary>
internal sealed unsafe class NdiSource : LiveSource
{
    private const uint CaptureMs = 50;

    private readonly NdiProvider _provider;
    private readonly NdiRuntime _runtime;
    private readonly Thread? _thread;
    private readonly nint _recv;
    private volatile bool _stopping;
    private int _tallyWord = -1;

    public NdiSource(NdiProvider provider, NdiRuntime runtime, string address, bool byUrl, LiveSettings settings, string key, IMonoClock clock)
        : base(LiveProtocol.Ndi, key, settings, clock)
    {
        _provider = provider;
        _runtime = runtime;
        var name = Marshal.StringToCoTaskMemUTF8(byUrl ? "" : address);
        var url = Marshal.StringToCoTaskMemUTF8(byUrl ? address : "");
        var recvName = Marshal.StringToCoTaskMemUTF8("PlayMap " + key);
        try
        {
            var create = new NdiRecvCreate
            {
                SourceToConnectTo = new NdiSourceT { NdiName = byUrl ? 0 : name, UrlAddress = byUrl ? url : 0 },
                ColorFormat = settings.Depth == LiveDepth.High ? NdiColorFormat.Best : NdiColorFormat.Fastest,
                Bandwidth = settings.Proxy ? NdiBandwidth.Lowest : NdiBandwidth.Highest,
                AllowVideoFields = 0,
                RecvName = recvName,
            };
            _recv = runtime.RecvCreate(&create);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(url);
            Marshal.FreeCoTaskMem(recvName);
        }
        if (_recv == 0)
        {
            Fail($"the NDI runtime would not make a receiver for {address}");
            return;
        }
        provider.AdvertiseReceiver(_recv);
        Waiting($"connecting to {address}");
        _thread = new Thread(Loop) { IsBackground = true, Name = "ndi " + key };
        _thread.Start();
    }

    protected override void SendTally(bool program, bool preview) => Volatile.Write(ref _tallyWord, (program ? 1 : 0) | (preview ? 2 : 0));

    private void Loop()
    {
        var video = default(NdiVideoFrame);
        var meta = default(NdiMetadataFrame);
        var sentTally = -1;
        var nextConnections = 0L;
        while (!_stopping)
        {
            var type = (NdiFrameType)_runtime.RecvCapture(_recv, &video, null, &meta, CaptureMs);
            switch (type)
            {
                case NdiFrameType.Video:
                    Copy(in video);
                    _runtime.RecvFreeVideo(_recv, &video);
                    break;
                case NdiFrameType.Metadata:
                    _runtime.RecvFreeMetadata(_recv, &meta);
                    break;
                case NdiFrameType.Error:
                    Fail("the sender went away");
                    break;
            }
            var tally = Volatile.Read(ref _tallyWord);
            if (tally >= 0 && tally != sentTally)
            {
                var t = new NdiTally { OnProgram = (byte)(tally & 1), OnPreview = (byte)((tally >> 1) & 1) };
                _runtime.RecvSetTally(_recv, &t);
                sentTally = tally;
            }
            var now = Clock.NowNs;
            if (now >= nextConnections)
            {
                nextConnections = now + 250_000_000;
                SetConnections(_runtime.RecvGetNoConnections(_recv));
            }
        }
    }

    private void Copy(in NdiVideoFrame frame)
    {
        var layout = frame.FourCc switch
        {
            NdiFourCc.Uyvy => PixelLayout.Uyvy,
            NdiFourCc.Uyva => PixelLayout.Uyva,
            NdiFourCc.P216 => PixelLayout.P216,
            NdiFourCc.Pa16 => PixelLayout.Pa16,
            NdiFourCc.Bgra or NdiFourCc.Bgrx => PixelLayout.Bgra8,
            _ => (PixelLayout)(-1),
        };
        var w = frame.XRes;
        var h = frame.YRes;
        if ((int)layout < 0 || frame.Data == 0 || w < 2 || h < 1 || (LivePlanes.IsPacked(layout) && LivePlanes.Refuse(layout, w, h) is not null)
            || !Pool.TryBeginWrite(layout, w, h, out var planes))
        {
            CountDropped();
            return;
        }
        // NDI lays planes one after another: UYVA's alpha plane at w a row after the UYVY plane; P216's chroma and PA16's alpha at
        // the first plane's stride.
        var src = (byte*)frame.Data;
        long bytes = 0;
        for (var i = 0; i < planes.Length; i++)
        {
            var srcStride = layout == PixelLayout.Uyva && i == 1 ? w : frame.LineStrideInBytes;
            var plane = planes[i];
            var row = Math.Min(srcStride, plane.Stride);
            var dst = (byte*)plane.Data;
            for (var y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * plane.Stride, plane.Stride, row);
            src += (long)srcStride * h;
            bytes += (long)plane.Stride * h;
        }
        var (matrix, transfer) = ColorInfo(frame.Metadata, h);
        var arrival = Clock.NowNs;
        Pool.Publish(frame.Timestamp == NdiTimestamps.Undefined || frame.Timestamp <= 0 ? long.MinValue : frame.Timestamp * 100, arrival,
            matrix, ColorRange.Limited, transfer);
        Delivered(layout, w, h, bytes, arrival);
    }

    /// <summary>The frame's <c>&lt;ndi_color_info transfer=… matrix=…/&gt;</c>, when it carries one (PQ and HLG, BT.2020); the
    /// defaults by height otherwise.</summary>
    internal static (ColorMatrix Matrix, TransferFunction Transfer) ColorInfo(nint metadata, int height)
    {
        var matrix = height < 720 ? ColorMatrix.Bt601 : ColorMatrix.Bt709;
        var transfer = TransferFunction.Sdr;
        if (metadata == 0)
            return (matrix, transfer);
        var text = Marshal.PtrToStringUTF8(metadata) ?? "";
        if (!text.Contains("ndi_color_info", StringComparison.Ordinal))
            return (matrix, transfer);
        if (text.Contains("bt_2100_pq", StringComparison.Ordinal))
            transfer = TransferFunction.Pq;
        else if (text.Contains("bt_2100_hlg", StringComparison.Ordinal))
            transfer = TransferFunction.Hlg;
        if (text.Contains("matrix=\"bt_2020\"", StringComparison.Ordinal))
            matrix = ColorMatrix.Bt2020;
        else if (text.Contains("matrix=\"bt_601\"", StringComparison.Ordinal))
            matrix = ColorMatrix.Bt601;
        else if (text.Contains("matrix=\"bt_709\"", StringComparison.Ordinal))
            matrix = ColorMatrix.Bt709;
        return (matrix, transfer);
    }

    protected override void Close()
    {
        _stopping = true;
        _thread?.Join();
        if (_recv != 0)
        {
            _provider.UnadvertiseReceiver(_recv);
            _runtime.RecvDestroy(_recv);
        }
    }
}

/// <summary>One NDI sender ("HOST (name)" on the network), made with this node's groups and no video clock (the render loop paces
/// it). Each packed frame goes to <c>send_send_video_async_v2</c> with the leader instant as its timecode; the runtime may read the
/// frame's memory until its next synchronising call (the next send, a send of nothing, or destroy), so the frame is let go only then.
/// Receivers and tally are read every 250 ms.</summary>
internal sealed unsafe class NdiSink : SendSink
{
    private readonly NdiProvider _provider;
    private readonly NdiRuntime _runtime;
    private readonly nint _send;
    private readonly string _fullName;

    public NdiSink(NdiProvider provider, NdiRuntime runtime, SendSpec spec) : base(spec)
    {
        _provider = provider;
        _runtime = runtime;
        var name = Marshal.StringToCoTaskMemUTF8(spec.Name);
        var groups = spec.Groups.Length > 0 ? Marshal.StringToCoTaskMemUTF8(spec.Groups) : 0;
        try
        {
            var create = new NdiSendCreate { NdiName = name, Groups = groups != 0 ? groups : provider.Groups, ClockVideo = 0, ClockAudio = 0 };
            _send = runtime.SendCreate(&create);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            if (groups != 0)
                Marshal.FreeCoTaskMem(groups);
        }
        if (_send == 0)
            throw new InvalidOperationException($"the NDI runtime would not make a sender named {spec.Name}");
        _fullName = runtime.SendGetSourceName != null && runtime.SendGetSourceName(_send) is var source && source != null
            ? Marshal.PtrToStringUTF8(source->NdiName) ?? spec.Name
            : string.Create(CultureInfo.InvariantCulture, $"{Environment.MachineName.ToUpperInvariant()} ({spec.Name})");
        provider.Advertise(_send);
        Start();
    }

    public override string FullName => _fullName;

    protected override bool Transmit(SendFrame frame)
    {
        var fourCc = frame.Format switch
        {
            SendFormat.Uyva => NdiFourCc.Uyva,
            SendFormat.Bgra8 => NdiFourCc.Bgra,
            SendFormat.P216 => NdiFourCc.P216,
            SendFormat.Pa16 => NdiFourCc.Pa16,
            _ => NdiFourCc.Uyvy,
        };
        var video = new NdiVideoFrame
        {
            XRes = frame.Width,
            YRes = frame.Height,
            FourCc = fourCc,
            FrameRateN = frame.FpsN,
            FrameRateD = frame.FpsD,
            PictureAspectRatio = 0,
            FrameFormatType = NdiFrameFormat.Progressive,
            Timecode = frame.LeaderNs / 100,
            Data = frame.Data,
            LineStrideInBytes = frame.Stride,
            Metadata = 0,
            Timestamp = 0,
        };
        _runtime.SendVideoAsync(_send, &video);
        return true;
    }

    protected override void Flush() => _runtime.SendVideoAsync(_send, null);

    protected override void Refresh()
    {
        Connections = _runtime.SendGetNoConnections(_send, 0);
        var tally = default(NdiTally);
        _runtime.SendGetTally(_send, &tally, 0);
        Tally = (tally.OnProgram != 0, tally.OnPreview != 0);
    }

    protected override void Close()
    {
        _provider.Unadvertise(_send);
        _runtime.SendDestroy(_send);
    }
}
