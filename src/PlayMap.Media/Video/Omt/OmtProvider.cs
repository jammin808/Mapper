using System.Reflection;
using libomtnet;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;

namespace PlayMap.Media.Video.Omt;

/// <summary>Open Media Transport in and out (libomtnet, vendored; its codec libvmx loaded at run time): discovery through OMT's own
/// (mDNS where a daemon runs, a discovery server where one is set, senders of this process always), receivers that copy each
/// decoded frame into a pool in the sender's own packed layout, and senders that encode packed frames on their worker. Ready only
/// when libvmx loaded: without it OMT can neither encode nor decode, and the status says where it looked.</summary>
public sealed class OmtProvider : ILiveInputProvider, ISendProvider, IVideoRuntimeFacts
{
    private readonly IMonoClock _clock;
    private readonly Lock _gate = new();
    private readonly bool _ready;
    private IReadOnlyList<LiveEndpoint> _endpoints = [];
    private int _disposed;

    public OmtProvider(OmtOptions options, IMonoClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _ready = OmtSetup.Configure(options);
    }

    /// <summary>libomtnet's version with the vendored commit.</summary>
    public static string Version =>
        typeof(OMTSend).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0.20";

    public LiveProtocol Protocol => LiveProtocol.Omt;

    public ProviderStatus Status => _ready
        ? new ProviderStatus("ready", Version, OmtSetup.VmxPath, "", "https://www.openmediatransport.org")
        : new ProviderStatus("failed", Version, "", OmtSetup.VmxWhy, "https://www.openmediatransport.org");

    public string Vmx => OmtSetup.VmxPathWord;

    public string Mdns => OmtSetup.Mdns();

    public IReadOnlyList<LiveEndpoint> Endpoints
    {
        get
        {
            lock (_gate)
                return _endpoints;
        }
    }

    public event Action? EndpointsChanged;

    /// <summary>Reads OMT's discovery now (the senders it knows by mDNS, a discovery server or this process).</summary>
    public void Refresh()
    {
        if (!_ready || Volatile.Read(ref _disposed) != 0)
            return;
        var names = OMTDiscovery.GetInstance().GetAddresses();
        var seen = new List<LiveEndpoint>(names.Length);
        var now = _clock.NowNs / 1_000_000;
        foreach (var full in names)
        {
            var (host, name) = LiveUri.Split(full);
            seen.Add(new LiveEndpoint(LiveProtocol.Omt, LiveUri.Format(LiveProtocol.Omt, full), full, host, name, "", now));
        }
        seen.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        bool changed;
        lock (_gate)
        {
            changed = seen.Count != _endpoints.Count || seen.Where((e, i) => e.FullName != _endpoints[i].FullName).Any();
            if (changed)
                _endpoints = seen;
        }
        if (changed)
            EndpointsChanged?.Invoke();
    }

    public ILiveSource Open(string address, bool byUrl, LiveSettings settings, string key)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(settings);
        if (!_ready)
            throw new InvalidOperationException(OmtSetup.VmxWhy);
        return new OmtSource(byUrl ? "omt://" + address : address, settings, key, _clock);
    }

    public ISendSink Create(SendSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_ready)
            throw new InvalidOperationException(OmtSetup.VmxWhy);
        return new OmtSink(spec);
    }

    /// <summary>OMT's quality word as its enum.</summary>
    internal static OMTQuality Quality(string word) => word switch
    {
        "low" => OMTQuality.Low,
        "medium" => OMTQuality.Medium,
        "high" => OMTQuality.High,
        _ => OMTQuality.Default,
    };

    /// <summary>OMT's discovery is per process and outlives every provider (upstream: never disposed while a sender or receiver may
    /// still run): disposing the provider ends its own refreshes only.</summary>
    public void Dispose() => Volatile.Write(ref _disposed, 1);
}

/// <summary>One OMT receiver on a thread of its own ("omt &lt;key&gt;"): the connection is made there (it never blocks the caller),
/// each decoded frame is copied out of OMT's buffer (valid only until the next receive) into the pool in its packed layout, with
/// OMT's 100 ns timestamp as the sender's stamp; tally and the suggested quality go to the sender from the same thread.</summary>
internal sealed class OmtSource : LiveSource
{
    private const int ReceiveMs = 50;

    private readonly string _address;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _tallyWord = -1;
    private int _qualityWord = -1;
    private OMTReceive? _receive;

    public OmtSource(string address, LiveSettings settings, string key, IMonoClock clock) : base(LiveProtocol.Omt, key, settings, clock)
    {
        _address = address;
        Volatile.Write(ref _qualityWord, (int)OmtProvider.Quality(settings.Quality));
        _thread = new Thread(Loop) { IsBackground = true, Name = "omt " + key };
        _thread.Start();
    }

    protected override void SendTally(bool program, bool preview) => Volatile.Write(ref _tallyWord, (program ? 1 : 0) | (preview ? 2 : 0));

    protected override void OnSettings(LiveSettings settings) => Volatile.Write(ref _qualityWord, (int)OmtProvider.Quality(settings.Quality));

    private void Loop()
    {
        var settings = Settings;
        var format = settings.Depth == LiveDepth.High ? OMTPreferredVideoFormat.UYVYorUYVAorP216orPA16 : OMTPreferredVideoFormat.UYVYorUYVA;
        var flags = settings.Proxy ? OMTReceiveFlags.Preview : OMTReceiveFlags.None;
        OMTReceive receive;
        try
        {
            receive = new OMTReceive(_address, OMTFrameType.Video | OMTFrameType.Metadata, format, flags);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException or DllNotFoundException)
        {
            Fail($"OMT could not receive {_address}: {ex.Message}");
            return;
        }
        Volatile.Write(ref _receive, receive);
        Waiting($"connecting to {_address}");
        var frame = default(OMTMediaFrame);
        var sentTally = -1;
        var sentQuality = -1;
        while (!_stopping)
        {
            if (receive.Receive(OMTFrameType.Video, ReceiveMs, ref frame) && frame.Type == OMTFrameType.Video)
                Copy(in frame);
            SetConnections(receive.IsConnected() ? 1 : 0);
            var tally = Volatile.Read(ref _tallyWord);
            if (tally >= 0 && tally != sentTally)
            {
                receive.SetTally(new OMTTally((tally & 2) != 0 ? 1 : 0, (tally & 1) != 0 ? 1 : 0));
                sentTally = tally;
            }
            var quality = Volatile.Read(ref _qualityWord);
            if (quality != sentQuality)
            {
                receive.SetSuggestedQuality((OMTQuality)quality);
                sentQuality = quality;
            }
        }
    }

    private unsafe void Copy(in OMTMediaFrame frame)
    {
        var layout = frame.Codec switch
        {
            (int)OMTCodec.UYVY => PixelLayout.Uyvy,
            (int)OMTCodec.UYVA => PixelLayout.Uyva,
            (int)OMTCodec.P216 => PixelLayout.P216,
            (int)OMTCodec.PA16 => PixelLayout.Pa16,
            (int)OMTCodec.BGRA => PixelLayout.Bgra8,
            _ => (PixelLayout)(-1),
        };
        var w = frame.Width;
        var h = frame.Height;
        if ((int)layout < 0 || w < 2 || h < 1 || frame.Data == 0 || (LivePlanes.IsPacked(layout) && LivePlanes.Refuse(layout, w, h) is not null))
        {
            CountDropped();
            return;
        }
        // OMT lays planes one after another: the first at its stride, UYVA's alpha at w a row, P216's chroma and PA16's alpha at the
        // same stride as the first plane.
        var count = LivePlanes.IsPacked(layout) ? LivePlanes.PlaneCount(layout) : 1;
        long needed = 0;
        for (var i = 0; i < count; i++)
            needed += (long)SourceStride(layout, i, w, frame.Stride) * h;
        if (frame.DataLength < needed || !Pool.TryBeginWrite(layout, w, h, out var planes))
        {
            CountDropped();
            return;
        }
        var src = (byte*)frame.Data;
        long bytes = 0;
        for (var i = 0; i < count; i++)
        {
            var srcStride = SourceStride(layout, i, w, frame.Stride);
            var plane = planes[i];
            var row = Math.Min(srcStride, plane.Stride);
            var dst = (byte*)plane.Data;
            for (var y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * plane.Stride, plane.Stride, row);
            src += (long)srcStride * h;
            bytes += (long)plane.Stride * h;
        }
        var arrival = Clock.NowNs;
        var matrix = frame.ColorSpace switch
        {
            OMTColorSpace.BT601 => ColorMatrix.Bt601,
            OMTColorSpace.BT709 => ColorMatrix.Bt709,
            _ => h < 720 ? ColorMatrix.Bt601 : ColorMatrix.Bt709,
        };
        Pool.Publish(frame.Timestamp > 0 ? frame.Timestamp * 100 : long.MinValue, arrival, matrix);
        Delivered(layout, w, h, bytes, arrival);
    }

    private static int SourceStride(PixelLayout layout, int plane, int width, int stride) => layout switch
    {
        PixelLayout.Uyva when plane == 1 => width,
        _ => stride,
    };

    protected override void Close()
    {
        _stopping = true;
        _thread.Join();
        Volatile.Read(ref _receive)?.Dispose();
    }
}

/// <summary>One OMT sender: announced as "HOST (name)" (its <see cref="FullName"/>) and reachable at <see cref="Url"/>; each packed frame
/// is encoded synchronously on the send's worker, which then lets go of it; receivers, tally and statistics are read every 250 ms.</summary>
internal sealed class OmtSink : SendSink
{
    private readonly OMTSend _send;

    public OmtSink(SendSpec spec) : base(spec)
    {
        _send = new OMTSend(spec.Name, OmtProvider.Quality(spec.Quality));
        Start();
    }

    public override string FullName => _send.Address;

    /// <summary>omt://host:port of this sender.</summary>
    public string Url => _send.URL;

    /// <summary>The quality the sender encodes at now (as the sender holds it, not as asked).</summary>
    internal OMTQuality EncoderQuality => _send.Quality;

    protected override bool Transmit(SendFrame frame)
    {
        var codec = frame.Format switch
        {
            SendFormat.Uyva => OMTCodec.UYVA,
            SendFormat.Bgra8 => OMTCodec.BGRA,
            SendFormat.P216 => OMTCodec.P216,
            SendFormat.Pa16 => OMTCodec.PA16,
            _ => OMTCodec.UYVY,
        };
        var alpha = frame.Format is SendFormat.Uyva or SendFormat.Bgra8 or SendFormat.Pa16;
        var m = new OMTMediaFrame
        {
            Type = OMTFrameType.Video,
            // Leader time in OMT's 100 ns units: a downstream PlayMap aligns by it; never −1, which would make OMT pace the sender.
            Timestamp = Math.Max(0, frame.LeaderNs / 100),
            Codec = (int)codec,
            Width = frame.Width,
            Height = frame.Height,
            Stride = frame.Stride,
            Flags = alpha ? OMTVideoFlags.Alpha : OMTVideoFlags.None,
            FrameRateN = frame.FpsN,
            FrameRateD = frame.FpsD,
            AspectRatio = frame.Width / (float)frame.Height,
            ColorSpace = OMTColorSpace.BT709,
            Data = frame.Data,
            DataLength = frame.Bytes,
        };
        _send.Send(m);
        return false;
    }

    protected override void Refresh()
    {
        Connections = _send.Connections;
        var tally = default(OMTTally);
        _send.GetTally(0, ref tally);
        Tally = (tally.Program != 0, tally.Preview != 0);
    }

    protected override void OnUpdate(SendSpec spec) => _send.Quality = OmtProvider.Quality(spec.Quality);

    protected override void Close() => _send.Dispose();
}
