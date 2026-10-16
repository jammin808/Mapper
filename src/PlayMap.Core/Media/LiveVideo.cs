using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Media;

/// <summary>Who receives or sends a live picture.</summary>
public enum LiveProtocol { Ffmpeg, Ndi, Omt, Spout, Syphon, DeckLink }

/// <summary>The one reader of a live input's location: "ndi:FULL NAME" | "ndi://ip:port" | "omt:FULL NAME" | "omt://host:port" |
/// "spout:NAME" | "syphon:NAME"; anything else with a scheme or a device path is Ffmpeg. A full name is "HOST (Name)"; a '*'
/// is allowed only as the whole host part ("ndi:*(Cam 1)") or as a whole Spout or Syphon name (the active sender).</summary>
public static class LiveUri
{
    /// <summary>Reads a location. <paramref name="address"/> is the full name, "host:port" (<paramref name="byUrl"/>), or for
    /// Ffmpeg the location as given. False, with nothing set, for an empty name, a misplaced '*', a malformed address, or text
    /// with neither a scheme nor a device path.</summary>
    public static bool TryParse(string? location, out LiveProtocol protocol, out string address, out bool byUrl)
    {
        protocol = LiveProtocol.Ffmpeg;
        address = "";
        byUrl = false;
        if (string.IsNullOrWhiteSpace(location))
            return false;
        var text = location.Trim();
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var scheme = colon > 0 ? text[..colon].ToLowerInvariant() : "";
        LiveProtocol? named = scheme switch
        {
            "ndi" => LiveProtocol.Ndi,
            "omt" => LiveProtocol.Omt,
            "spout" => LiveProtocol.Spout,
            "syphon" => LiveProtocol.Syphon,
            _ => null,
        };
        if (named is not { } p)
        {
            if (!IsScheme(scheme) && !IsDevicePath(text))
                return false;
            address = text;
            byUrl = text.Contains("://", StringComparison.Ordinal);
            return true;
        }
        var rest = text[(colon + 1)..].Trim();
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            if (p is LiveProtocol.Spout or LiveProtocol.Syphon || !IsHostPort(rest[2..]))
                return false;
            protocol = p;
            address = rest[2..];
            byUrl = true;
            return true;
        }
        if (!IsFullName(p, rest))
            return false;
        protocol = p;
        address = rest;
        return true;
    }

    /// <summary>The location for a protocol and an address read by <see cref="TryParse"/>: "host:port" of NDI or OMT takes the
    /// URL form; a full name, a Spout or Syphon name and an Ffmpeg location are written as they are.</summary>
    public static string Format(LiveProtocol protocol, string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return protocol switch
        {
            LiveProtocol.Ffmpeg or LiveProtocol.DeckLink => address,
            LiveProtocol.Ndi or LiveProtocol.Omt when IsHostPort(address) => Scheme(protocol) + "://" + address,
            _ => Scheme(protocol) + ":" + address,
        };
    }

    /// <summary>Whether a full name answers a pattern: equal (host ignoring case, name exactly), or a '*' host matching any host
    /// with that name; a pattern of "*" alone matches every name.</summary>
    public static bool Matches(string pattern, string fullName)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(fullName);
        var pt = pattern.Trim();
        if (pt == "*")
            return true;
        var (ph, pn) = Split(pt);
        var (fh, fn) = Split(fullName.Trim());
        if (!string.Equals(pn, fn, StringComparison.Ordinal))
            return false;
        return ph == "*" || string.Equals(ph, fh, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"HOST (Name)" → (HOST, Name); a name with no parenthesis is all host and an empty name.</summary>
    public static (string Host, string Name) Split(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var t = fullName.Trim();
        var open = t.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !t.EndsWith(')'))
            return (t, "");
        return (t[..open].Trim(), t[(open + 1)..^1].Trim());
    }

    private static string Scheme(LiveProtocol protocol) => protocol.ToString().ToLowerInvariant();

    private static bool IsFullName(LiveProtocol protocol, string rest)
    {
        if (rest.Length == 0)
            return false;
        if (protocol is LiveProtocol.Spout or LiveProtocol.Syphon)
            return rest == "*" || !rest.Contains('*', StringComparison.Ordinal);
        var (host, name) = Split(rest);
        if (host.Length == 0 || (name.Length == 0 && rest.Contains('(', StringComparison.Ordinal)))
            return false;
        if (name.Contains('*', StringComparison.Ordinal))
            return false;
        return host == "*" ? name.Length > 0 : !host.Contains('*', StringComparison.Ordinal);
    }

    private static bool IsHostPort(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
            return false;
        var host = text[..colon];
        if (host.Contains(' ', StringComparison.Ordinal) || host.Contains('(', StringComparison.Ordinal) || host.Contains('*', StringComparison.Ordinal))
            return false;
        return int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535;
    }

    /// <summary>A URI scheme of two or more characters (one letter is a Windows drive).</summary>
    private static bool IsScheme(string scheme) =>
        scheme.Length >= 2 && char.IsAsciiLetter(scheme[0]) && scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');

    /// <summary>A capture device: a device node, or a DirectShow/AVFoundation "video=…" name.</summary>
    private static bool IsDevicePath(string text) =>
        text.StartsWith("/dev/", StringComparison.Ordinal) || text.StartsWith("video=", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A live source a provider found on the network or on this machine.</summary>
public sealed record LiveEndpoint(LiveProtocol Protocol, string Uri, string FullName, string Host, string Name,
    string Address, long SeenUnixMs)
{
    /// <summary>A heard Show Bus peer at that address.</summary>
    public string PeerId { get; init; } = "";
    public string PeerName { get; init; } = "";
    /// <summary>The media id that names it, or "".</summary>
    public string InLibrary { get; init; } = "";
}

public enum LiveState { Connecting, Ok, Stale, Lost, Absent, Failed }

/// <summary>What a live input measured: its state in words, its picture, its pacing and its losses.</summary>
public sealed record LiveHealth(LiveState State, string Words, int Width, int Height, double Fps, long AgeMs,
    double JitterMsP95, long Received, long Dropped, long Repeated, long Late, string Format, bool Alpha,
    TransferFunction Transfer, double Mbps, int Connections)
{
    public bool Ptz { get; init; }
    public string WebControl { get; init; } = "";
    /// <summary>The smooth or locked pacing delay.</summary>
    public double DelayMs { get; init; }
    public double ClockPpm { get; init; }
    public static readonly LiveHealth Opening = new(LiveState.Connecting, "connecting", 0, 0, 0, -1, 0, 0, 0, 0, 0, "", false, TransferFunction.Sdr, 0, 0);
}

/// <summary>A camera's pan, tilt, zoom and focus, when its sender offers them.</summary>
public interface IPtz
{
    bool Supported { get; }
    bool Preset(int number, float speed);
    bool Store(int number);
    bool Zoom(float value);
    bool PanTilt(float pan, float tilt);
    /// <summary>null = auto focus.</summary>
    bool Focus(float? value);
}

/// <summary>A network picture: Acquire(mediaNs) takes the frame's leader instant (latest ignores it).</summary>
public interface ILiveSource : IVideoSource
{
    LiveProtocol Protocol { get; }
    LiveHealth Live { get; }
    IPtz? Ptz { get; }
    void SetTally(bool program, bool preview);
    /// <summary>Render-side selection; the anchor is used by Locked.</summary>
    void SetPacing(LivePacing pacing, LiveAnchor? anchor);
}

/// <summary>Where a locked live input sits on the show clock.</summary>
public readonly record struct LiveAnchor(long OffsetNs, long DelayNs, long Seq);

/// <summary>A provider's runtime: loaded or absent, its version and where it came from, in words.</summary>
public sealed record ProviderStatus(string State, string Version, string Path, string Words, string Link)
{
    public static readonly ProviderStatus Absent = new("absent", "", "", "", "");
}

/// <summary>Receives one protocol's live inputs.</summary>
public interface ILiveInputProvider : IDisposable
{
    LiveProtocol Protocol { get; }
    ProviderStatus Status { get; }
    /// <summary>An immutable snapshot.</summary>
    IReadOnlyList<LiveEndpoint> Endpoints { get; }
    event Action? EndpointsChanged;
    void Refresh();
    /// <summary>Starts receiving; never blocks (frames arrive later). The caller owns the source.</summary>
    ILiveSource Open(string address, bool byUrl, LiveSettings settings, string key);
}

/// <summary>What a sender is asked to send.</summary>
public sealed record SendSpec(string Id, string Name, int Width, int Height, int FpsN, int FpsD, SendFormat Format,
    string Quality, string Groups, string Failover, bool Alpha);

/// <summary>What a sender measured.</summary>
public sealed record SendStats(long Sent, long Dropped, double EncodeMsP95, double Mbps, long BytesSent);

/// <summary>One sender of packed frames.</summary>
public interface ISendSink : IDisposable
{
    string FullName { get; }
    /// <summary>Refreshed at most 4 times a second.</summary>
    int Connections { get; }
    (bool Program, bool Preview) Tally { get; }
    SendStats Stats { get; }
    /// <summary>"" while healthy.</summary>
    string Words { get; }
    /// <summary>Takes a packed frame; latest wins (an older unsent frame is released and counted). Never blocks.</summary>
    void Offer(SendFrame frame);
    /// <summary>Applies a changed spec: quality, failover and groups in place; a new name or format recreates the sender.</summary>
    void Update(SendSpec spec);
}

/// <summary>Makes senders of one protocol.</summary>
public interface ISendProvider : IDisposable
{
    LiveProtocol Protocol { get; }
    ProviderStatus Status { get; }
    /// <summary>Creates the sender (binds sockets): call off the render thread.</summary>
    ISendSink Create(SendSpec spec);
}

/// <summary>A packed picture owned by the producer until <see cref="Release"/> (any thread, once).</summary>
public abstract class SendFrame
{
    public nint Data { get; protected set; }
    public int Bytes { get; protected set; }
    public int Stride { get; protected set; }
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public SendFormat Format { get; protected set; }
    public long LeaderNs { get; protected set; }
    public int FpsN { get; protected set; }
    public int FpsD { get; protected set; }
    public TransferFunction Transfer { get; protected set; }
    public abstract void Release();
}

/// <summary>The frame a GPU sender is drawing: its number, its leader instant, the tap and the size.</summary>
public readonly record struct SendFrameInfo(long Frame, long LeaderNs, string Tap, int Width, int Height);

/// <summary>A GPU sender's state in words with its counters.</summary>
public sealed record SendHealth(string State, long Frames, long Dropped, long Busy, double Fps, string Receivers, string Words);

/// <summary>A sender that takes the picture on the GPU (Spout): the render thread draws into its surface; never blocks.</summary>
public interface IGpuSendTarget : IDisposable
{
    string Key { get; }
    SendHealth Health { get; }
    /// <summary>Render thread, non-blocking: the surface to draw this frame into, or false to skip it (counted).</summary>
    bool TryBegin(int width, int height, SendFormat format, out GpuSurface target);
    void End(bool drawn, in SendFrameInfo info);
}

/// <summary>Makes GPU senders of one protocol on the renderer's device.</summary>
public interface ISendSinkProvider : IDisposable
{
    SendProtocol Protocol { get; }
    ProviderStatus Status { get; }
    /// <summary>Control lane: registers the send's published name and makes its shared texture.</summary>
    IGpuSendTarget Create(VideoSend send, string publishedName, IGpuShare gpu);
}

/// <summary>The plane geometry of the four packed network layouts as NDI and OMT lay them out (tight rows, no padding): Uyvy one
/// plane of 2·w bytes a row; Uyva that plane then an 8-bit alpha plane of w; P216 a 16-bit Y plane of 2·w then an interleaved 16-bit
/// CbCr plane of 2·w at full height; Pa16 P216's two planes then a 16-bit alpha plane of 2·w. Samples of the 16-bit layouts are
/// little-endian and use all 16 bits. Pure: the frame pool sizes its slots, the uploader its textures and the senders their strides
/// from it.</summary>
public static class LivePlanes
{
    public static bool IsPacked(PixelLayout layout) => layout is PixelLayout.Uyvy or PixelLayout.Uyva or PixelLayout.P216 or PixelLayout.Pa16;

    public static bool HasAlpha(PixelLayout layout) => layout is PixelLayout.Uyva or PixelLayout.Pa16;

    public static bool IsDeep(PixelLayout layout) => layout is PixelLayout.P216 or PixelLayout.Pa16;

    /// <summary>Planes of a packed layout; 0 for any other.</summary>
    public static int PlaneCount(PixelLayout layout) => layout switch
    {
        PixelLayout.Uyvy => 1,
        PixelLayout.Uyva or PixelLayout.P216 => 2,
        PixelLayout.Pa16 => 3,
        _ => 0,
    };

    /// <summary>The tight row of one plane, in bytes, at width <paramref name="width"/>.</summary>
    public static int Stride(PixelLayout layout, int plane, int width) => layout switch
    {
        PixelLayout.Uyvy => 2 * width,
        PixelLayout.Uyva => plane == 0 ? 2 * width : width,
        PixelLayout.P216 or PixelLayout.Pa16 => 2 * width,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "not a packed layout"),
    };

    /// <summary>The bytes of one whole frame (every plane, tight).</summary>
    public static long FrameBytes(PixelLayout layout, int width, int height)
    {
        long bytes = 0;
        for (var i = 0; i < PlaneCount(layout); i++)
            bytes += (long)Stride(layout, i, width) * height;
        return bytes;
    }

    /// <summary>Why a frame of this size cannot be laid out (4:2:2 pairs need an even width), or null.</summary>
    public static string? Refuse(PixelLayout layout, int width, int height)
    {
        if (!IsPacked(layout))
            return $"{layout} is not a packed network layout";
        if (width < 2 || height < 1)
            return string.Create(CultureInfo.InvariantCulture, $"a {Word(layout)} frame of {width}×{height} has no picture");
        if (width % 2 != 0)
            return string.Create(CultureInfo.InvariantCulture, $"a {Word(layout)} frame needs an even width (4:2:2 pairs), not {width}");
        return null;
    }

    /// <summary>The lower-case word STATE uses for a layout.</summary>
    public static string Word(PixelLayout layout) => layout switch
    {
        PixelLayout.Uyvy => "uyvy",
        PixelLayout.Uyva => "uyva",
        PixelLayout.P216 => "p216",
        PixelLayout.Pa16 => "pa16",
        PixelLayout.Bgra8 => "bgra",
        PixelLayout.Rgba8 => "rgba",
        _ => layout.ToString().ToLowerInvariant(),
    };
}
