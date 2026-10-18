using System.Globalization;
using System.Runtime.CompilerServices;
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
/// them, so a Discovery Server sees them. Without a runtime the provider stays registered and says why, with where to get one; a
/// runtime path refused because it was not absolute is said in its words, with a runtime or without.</summary>
public sealed unsafe class NdiProvider : ILiveInputProvider, ISendProvider, IVideoRuntimeFacts
{
    private readonly IMonoClock _clock;
    private readonly NdiOptions _options;
    private readonly string _configWhy = "";
    private readonly string _readyWords = "";
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
        // The configuration (discovery server, groups) is in place before the runtime is loaded and initialised, whichever of the
        // two reads it; one that cannot be written is said, and NDI discovers by mDNS (the finder and senders still take the groups).
        try
        {
            WriteConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _configWhy = $"NDI's discovery server is not applied, and this node's other NDI tools do not get its groups: its configuration "
                + $"could not be written in {_options.ConfigFolder} ({ex.Message}); its own finder and senders still use the groups";
        }
        var search = NdiRuntime.Search(options.RuntimePath);
        var refused = search.RefusedWords;
        _runtime = NdiRuntime.Load(search, out _why);
        if (_runtime is null)
            return;
        if (_runtime.IsSupportedCpu() == 0)
        {
            _why = Words("this CPU lacks what the NDI runtime needs (SSE4.2)", refused);
            return;
        }
        if (_runtime.Initialize() == 0)
        {
            _why = Words($"the NDI runtime at {_runtime.Path} would not initialise", refused);
            return;
        }
        _initialized = true;
        _readyWords = Words(refused, _configWhy);
        if (_runtime.HasAdvertisers)
            MakeAdvertisers();
    }

    /// <summary>The runtime in use, or null.</summary>
    public NdiRuntime? Runtime => _initialized ? _runtime : null;

    public LiveProtocol Protocol => LiveProtocol.Ndi;

    public ProviderStatus Status => _initialized
        ? new ProviderStatus("ready", _runtime!.Version, _runtime.Path, _readyWords, NdiRuntime.Link)
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

    /// <summary>The parts that say something, in one line.</summary>
    private static string Words(params string[] parts) => string.Join("; ", parts.Where(p => p.Length > 0));

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
            // Disposed meanwhile: the runtime may be gone already, so no finder is made after it.
            if (Volatile.Read(ref _disposed) != 0)
                return;
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
    private readonly nint[] _planeData = new nint[3];
    private readonly int[] _planeStride = new int[3];
    private int _planeCount;
    private bool _weaving;
    private PixelLayout _weaveLayout;
    private int _weaveWidth;
    private int _weaveHeight;
    private long _weaveStamp;
    private ColorMatrix _weaveMatrix;
    private TransferFunction _weaveTransfer;
    private bool _fieldLost;

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
        AbandonWeave();
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
        // The receiver's colour formats deliver an interlaced source as its fields, upper first: field 0 goes to the even rows of a
        // frame of twice its height, field 1 to the odd rows, and the frame is published whole once both are in.
        var field = frame.FrameFormatType is NdiFrameFormat.Field0 or NdiFrameFormat.Field1;
        var height = field ? 2 * h : h;
        if ((int)layout < 0 || frame.Data == 0 || w < 2 || h < 1 || (LivePlanes.IsPacked(layout) && LivePlanes.Refuse(layout, w, height) is not null))
        {
            // One lost picture is counted once: a field 1 whose field 0 already counted is not counted again.
            var counted = frame.FrameFormatType == NdiFrameFormat.Field1 && _fieldLost;
            AbandonWeave();
            if (!counted)
                CountDropped();
            _fieldLost = frame.FrameFormatType == NdiFrameFormat.Field0;
            return;
        }
        if (frame.FrameFormatType == NdiFrameFormat.Field1)
        {
            if (!_weaving || _weaveLayout != layout || _weaveWidth != w || _weaveHeight != h)
            {
                // A field 1 whose field 0 already counted as lost (no slot for it) is the same lost picture, counted once.
                if (!_fieldLost)
                    CountDropped();
                _fieldLost = false;
                AbandonWeave();
                return;
            }
            _weaving = false;
            CopyRows(in frame, layout, w, h, 1, 2);
            // The frame's colour: field 1's tag, else field 0's (which carries the size's rule when it had none).
            var (matrix, transfer) = Tagged(frame.Metadata) ? ColorInfo(frame.Metadata, w, height) : (_weaveMatrix, _weaveTransfer);
            PublishFrame(layout, w, height, _weaveStamp, matrix, transfer);
            return;
        }
        AbandonWeave();
        _fieldLost = false;
        if (!Pool.TryBeginWrite(layout, w, height, out var planes))
        {
            // The pool counted the drop.
            _fieldLost = field;
            return;
        }
        _planeCount = planes.Length;
        for (var i = 0; i < planes.Length; i++)
        {
            _planeData[i] = planes[i].Data;
            _planeStride[i] = planes[i].Stride;
        }
        CopyRows(in frame, layout, w, h, 0, field ? 2 : 1);
        var stamp = frame.Timestamp == NdiTimestamps.Undefined || frame.Timestamp <= 0 ? long.MinValue : frame.Timestamp * 100;
        var (colour, curve) = ColorInfo(frame.Metadata, w, height);
        if (field)
        {
            (_weaving, _weaveLayout, _weaveWidth, _weaveHeight, _weaveStamp) = (true, layout, w, h, stamp);
            (_weaveMatrix, _weaveTransfer) = (colour, curve);
            return;
        }
        PublishFrame(layout, w, height, stamp, colour, curve);
    }

    /// <summary>Copies a frame's (or a field's) rows into the planes being written: row y to row y·step + offset. NDI lays planes one
    /// after another: UYVA's alpha plane at w a row after the UYVY plane; P216's chroma and PA16's alpha at the first plane's stride.</summary>
    private void CopyRows(in NdiVideoFrame frame, PixelLayout layout, int w, int h, int offset, int step)
    {
        var src = (byte*)frame.Data;
        for (var i = 0; i < _planeCount; i++)
        {
            var srcStride = layout == PixelLayout.Uyva && i == 1 ? w : frame.LineStrideInBytes;
            var stride = _planeStride[i];
            var row = Math.Min(srcStride, stride);
            var dst = (byte*)_planeData[i];
            for (var y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + ((long)y * step + offset) * stride, stride, row);
            src += (long)srcStride * h;
        }
    }

    private void PublishFrame(PixelLayout layout, int w, int h, long stamp, ColorMatrix matrix, TransferFunction transfer)
    {
        long bytes = 0;
        for (var i = 0; i < _planeCount; i++)
            bytes += (long)_planeStride[i] * h;
        var arrival = Clock.NowNs;
        Pool.Publish(stamp, arrival, matrix, ColorRange.Limited, transfer);
        Delivered(layout, w, h, bytes, arrival);
    }

    /// <summary>Whether a frame's metadata carries a colour tag.</summary>
    private static bool Tagged(nint metadata) =>
        metadata != 0 && MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)metadata).IndexOf("<ndi_color_info"u8) >= 0;

    /// <summary>A lone field waiting for its pair is given back (its pair never came, or a whole frame came instead).</summary>
    private void AbandonWeave()
    {
        if (!_weaving)
            return;
        _weaving = false;
        Pool.Abandon();
        CountDropped();
    }

    /// <summary>The frame's colour: its <c>&lt;ndi_color_info transfer=… matrix=…/&gt;</c> when it carries one (PQ and HLG; BT.601,
    /// BT.709, BT.2020 or BT.2100), else NDI's own rule by size — BT.601 up to 720×576, Rec.2020 above 1920×1080, Rec.709 between.
    /// Read in place: nothing is allocated.</summary>
    internal static (ColorMatrix Matrix, TransferFunction Transfer) ColorInfo(nint metadata, int width, int height)
    {
        var matrix = width > 1920 || height > 1080 ? ColorMatrix.Bt2020 : width > 720 || height > 576 ? ColorMatrix.Bt709 : ColorMatrix.Bt601;
        var transfer = TransferFunction.Sdr;
        if (metadata == 0)
            return (matrix, transfer);
        var text = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)metadata);
        // Only the ndi_color_info element's own attributes count (it may sit in an ndi_metadata_group beside other elements).
        var at = text.IndexOf("<ndi_color_info"u8);
        if (at < 0)
            return (matrix, transfer);
        var element = text[at..];
        // The element ends at the first '>' outside a quoted value.
        byte quote = 0;
        for (var i = 0; i < element.Length; i++)
        {
            var c = element[i];
            if (quote != 0)
            {
                if (c == quote)
                    quote = 0;
            }
            else if (c is (byte)'"' or (byte)'\'')
            {
                quote = c;
            }
            else if (c == (byte)'>')
            {
                element = element[..i];
                break;
            }
        }
        var curve = Attribute(element, "transfer"u8);
        if (curve.SequenceEqual("bt_2100_pq"u8))
            transfer = TransferFunction.Pq;
        else if (curve.SequenceEqual("bt_2100_hlg"u8))
            transfer = TransferFunction.Hlg;
        var coefficients = Attribute(element, "matrix"u8);
        if (coefficients.SequenceEqual("bt_2020"u8) || coefficients.SequenceEqual("bt_2100"u8))
            matrix = ColorMatrix.Bt2020;
        else if (coefficients.SequenceEqual("bt_601"u8))
            matrix = ColorMatrix.Bt601;
        else if (coefficients.SequenceEqual("bt_709"u8))
            matrix = ColorMatrix.Bt709;
        return (matrix, transfer);
    }

    /// <summary>An XML attribute's value in an element (name = "value" or 'value', spaces allowed around the =), or empty.</summary>
    private static ReadOnlySpan<byte> Attribute(ReadOnlySpan<byte> element, ReadOnlySpan<byte> name)
    {
        var from = 0;
        while (from < element.Length)
        {
            var i = element[from..].IndexOf(name);
            if (i < 0)
                return default;
            i += from;
            from = i + name.Length;
            // A whole attribute name: not the tail of another.
            if (i > 0 && element[i - 1] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                continue;
            var rest = element[from..].TrimStart(" \t\r\n"u8);
            if (rest.IsEmpty || rest[0] != (byte)'=')
                continue;
            rest = rest[1..].TrimStart(" \t\r\n"u8);
            if (rest.IsEmpty || rest[0] is not ((byte)'"' or (byte)'\''))
                continue;
            var quote = rest[0];
            rest = rest[1..];
            var end = rest.IndexOf(quote);
            return end < 0 ? default : rest[..end];
        }
        return default;
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

    /// <summary>The colour every sent frame carries (null-terminated).</summary>
    private static ReadOnlySpan<byte> ColorTag => "<ndi_color_info transfer=\"bt_709\" matrix=\"bt_709\" primaries=\"bt_709\"/>\0"u8;

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
            // PlayMap packs YUV in BT.709 limited range at every size: said on every such frame, since without it a receiver reads a UHD
            // frame as Rec.2020 and an SD one as BT.601. A BGRA frame is RGB, which the runtime converts by its own rule: no tag. The
            // literal is the assembly's own read-only data, valid for as long as NDI reads it.
            Metadata = frame.Format == SendFormat.Bgra8 ? 0 : (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(ColorTag)),
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
