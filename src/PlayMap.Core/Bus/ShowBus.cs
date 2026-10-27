using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlayMap.Core.Bus;

/// <summary>The Show Bus 1.3 numbers this node keeps (docs/SHOWBUS.md, the copy of Stagewatch's master, §14; ShowBusSpecTests holds
/// each to its row) and PlayMap's ports. PlayMap's own ports sit clear of Patterns (9696–9701) and Stagewatch (9923–9928).</summary>
public static class ShowBusConstants
{
    /// <summary>The hello's <c>proto</c>: the revision whose shapes this node speaks, 3 for Show Bus 1.3 (§2, §14 <c>proto</c>).</summary>
    public const int Proto = 3;

    /// <summary>Every node broadcasts its hello here every 2 s and every node listens (SO_REUSEADDR).</summary>
    public const int HelloPort = 9928;
    public const int HelloEveryMs = 2000;

    /// <summary>The most bytes a hello is as sent: one datagram that needs no fragmenting (§14 <c>hello.bytes</c>).</summary>
    public const int HelloMaxBytes = 1200;

    /// <summary>The largest datagram this reader takes as a hello: at least §14's <c>hello.read.bytes</c> (2048).</summary>
    public const int HelloReadBytes = 4096;

    /// <summary>A heard hello keeps its first 32 caps of §2's shape, each 1–24 of <c>[a-z0-9.-]</c>, and at most 8 doors.</summary>
    public const int HelloCaps = 32;
    public const int HelloCapChars = 24;
    public const int HelloPorts = 8;

    /// <summary>A node's name and host, each at most 64 characters, sent and kept whole up to that.</summary>
    public const int HelloNameChars = 64;

    /// <summary>A hello's kind and id: at most 16 and 40 of letters, digits, '-' and '_'.</summary>
    public const int HelloKindChars = 16;
    public const int HelloIdChars = 40;

    /// <summary>A hello's <c>inst</c>: exactly 8 lowercase hex, new at every start of the sender's process.</summary>
    public const int HelloInstChars = 8;

    /// <summary>The most ids a reader keeps: a hello from a new id past that is dropped (§14 <c>peers.max</c>).</summary>
    public const int PeersMax = 128;

    /// <summary>The most addresses kept for one id, each heard within <see cref="StaleMs"/> (§14 <c>peer.addresses</c>).</summary>
    public const int PeerAddresses = 4;

    /// <summary>An id's conflict mark clears this long after the last alternation that set it (§14 <c>peer.conflict.clear.ms</c>).</summary>
    public const int ConflictClearMs = 60000;

    /// <summary>The most peer rows a context lists (§6, §14 <c>context.peers</c>).</summary>
    public const int ContextPeers = 32;

    /// <summary>A peer is live while hellos keep coming, stale after 6 s, gone after 12 s, forgotten at 60 s.</summary>
    public const int StaleMs = 6000;
    public const int GoneMs = 12000;
    public const int ForgetMs = 60000;

    /// <summary>A link beats at least every 2 s; silent 6 s = stale; 12 s = drop and redial.</summary>
    public const int BeatMs = 2000;

    /// <summary>Redial backoff: 0.5 s doubling to 5 s, ±20 % jitter.</summary>
    public const int RedialMinMs = 500;
    public const int RedialMaxMs = 5000;

    /// <summary>Clock offset: the min-RTT sample of the last 8; "CLOCKS APART" past 2 s.</summary>
    public const int OffsetWindow = 8;
    public const int ClocksApartMs = 2000;

    /// <summary>PlayMap's HTTP, WebSocket, HTTPS (same port, TLS sniffed by the first byte) and MCP.</summary>
    public const int PlayMapHttpPort = 9940;
    /// <summary>PlayMap's line wire (Patterns' grammar).</summary>
    public const int PlayMapWirePort = 9941;
    /// <summary>PlayMap's fine clock sync (UDP, nanoseconds, between PlayMap nodes).</summary>
    public const int PlayMapSyncPort = 9942;
    /// <summary>PlayMap's OSC control input (UDP), clear of Patterns (9696–9701) and Stagewatch (9923–9928).</summary>
    public const int PlayMapOscPort = 9943;
    /// <summary>PlayMap's OSCQuery (TCP, HTTP and WebSocket).</summary>
    public const int PlayMapOscQueryPort = 9944;

    public const int PatternsWirePort = 9697;
    public const int PatternsHttpPort = 9696;
    public const int StagewatchHttpPort = 9924;

    /// <summary>Patterns' own beacon (twin, nodes): no other app sends on it.</summary>
    public const int PatternsBeaconPortNeverSend = 9700;

    public const string KindPlayMap = "playmap";
    public const string KindPatterns = "patterns";
    public const string KindStagewatch = "main";
}

/// <summary>A node's hello (Show Bus §2). Discovery says what a node is, never what the show is doing.</summary>
public sealed record ShowBusHello(
    string Kind,
    int Version,
    int Proto,
    string Id,
    string Name,
    string Host,
    IReadOnlyDictionary<string, int> Ports,
    IReadOnlyList<string> Caps,
    long ClockUnixMs,
    string? Instance,
    string? TlsFingerprint)
{
    /// <summary>PlayMap's capabilities, each a §14 <c>cap</c> row that names <c>playmap</c> (a dotted word names a finer ability of
    /// the word before its dot, the way Patterns' "video.transport" does).</summary>
    public static readonly IReadOnlyList<string> PlayMapCaps =
    [
        "video", "video.clock", "video.transport", "video.seek", "video.end",
        "state", "context", "receipts", "clock", "mcp",
        "playlist", "media", "mapping", "scan",
    ];

    /// <summary>The build the node runs, its whole identity in SemVer (<see cref="BuildInfo.Informational"/>: <c>0.2.0+17.abc1234</c>,
    /// <c>0.2.0-dev+abc1234</c>), so a peer tells builds apart before it connects; null from a node that names none. A reader keeps at
    /// most <see cref="MaxBuildChars"/> characters of SemVer's alphabet and drops any other value, keeping the rest of the hello.</summary>
    public string? Build { get; init; }

    /// <summary>The longest build a reader keeps.</summary>
    public const int MaxBuildChars = 64;

    public int Port(string name) => Ports.TryGetValue(name, out var p) ? p : 0;

    /// <summary>Names in any script ride as UTF-8, a few bytes a character rather than six-byte escapes; a hello is never HTML.</summary>
    private static readonly JsonWriterOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The datagram: compact JSON. Top-level "web" and "wire" ride beside "ports" for older readers
    /// (Stagewatch's Patterns finder reads a top-level "wire").</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, Wire))
        {
            w.WriteStartObject();
            w.WriteString("t", Kind);
            w.WriteNumber("v", Version);
            w.WriteNumber("proto", Proto);
            w.WriteString("id", Id);
            w.WriteString("name", Name);
            w.WriteString("host", Host);
            if (Port("http") > 0)
                w.WriteNumber("web", Port("http"));
            if (Port("wire") > 0)
                w.WriteNumber("wire", Port("wire"));
            w.WriteStartObject("ports");
            foreach (var (k, v) in Ports.OrderBy(p => p.Key, StringComparer.Ordinal))
                if (v > 0)
                    w.WriteNumber(k, v);
            w.WriteEndObject();
            w.WriteStartArray("caps");
            foreach (var c in Caps)
                w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteNumber("clock", ClockUnixMs);
            if (Instance is not null)
                w.WriteString("inst", Instance);
            if (TlsFingerprint is not null)
                w.WriteString("tls", TlsFingerprint);
            if (Build is not null)
                w.WriteString("build", Build);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>This hello within <see cref="ShowBusConstants.HelloMaxBytes"/> as sent: itself when it fits, else with its name and host
    /// cut to 32, 16, 8 and then no characters until it does (only a name of escaped or unassigned characters can need it).</summary>
    public ShowBusHello Fitted()
    {
        var hello = this;
        foreach (var max in (ReadOnlySpan<int>)[32, 16, 8, 0])
        {
            if (hello.ToJson().Length <= ShowBusConstants.HelloMaxBytes)
                return hello;
            hello = hello with { Name = Clip(hello.Name, max), Host = Clip(hello.Host, max) };
        }
        return hello;
    }

    /// <summary>A name or a host as a hello says and keeps it (§2): no control characters, at most
    /// <see cref="ShowBusConstants.HelloNameChars"/> characters, never ending inside a surrogate pair.</summary>
    public static string NameOf(string? text)
    {
        var s = text ?? "";
        if (s.Any(char.IsControl))
            s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return Clip(s, ShowBusConstants.HelloNameChars).Trim();
    }

    /// <summary>A cap word as §2 allows it: 1 to <see cref="ShowBusConstants.HelloCapChars"/> of lowercase letters, digits, '.' and '-'.</summary>
    public static bool CapShape(string? cap) =>
        cap is { Length: > 0 and <= ShowBusConstants.HelloCapChars } && cap.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    /// <summary>An <c>inst</c> as §2 has it: exactly <see cref="ShowBusConstants.HelloInstChars"/> lowercase hex.</summary>
    public static bool InstShape(string? inst) =>
        inst is { Length: ShowBusConstants.HelloInstChars } && inst.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Parses a datagram by Show Bus 1.3 §2's reader rules: at most <see cref="ShowBusConstants.HelloReadBytes"/> bytes,
    /// v == 1, t ≤ 16 and id ≤ 40 characters of [A-Za-z0-9_-]; the first 32 caps of §2's shape kept (a word of another shape is one
    /// no reader knows, so it is ignored); at most 8 doors, each a port 1–65535, and the legacy top-level web/wire/port used when ports
    /// names none; name and host kept whole up to 64 characters; an inst only of its shape. Null when it is not a hello — and a datagram
    /// the JSON reader takes but cannot read out (a string or a name that is a lone surrogate or invalid UTF-8) is none: it never throws.</summary>
    public static ShowBusHello? Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length is 0 or > ShowBusConstants.HelloReadBytes)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(datagram.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 1)
                return null;
            var t = Str(root, "t");
            var id = Str(root, "id");
            if (!IsToken(t, ShowBusConstants.HelloKindChars) || !IsToken(id, ShowBusConstants.HelloIdChars))
                return null;
            var ports = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("ports", out var pe) && pe.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in pe.EnumerateObject())
                {
                    if (ports.Count >= ShowBusConstants.HelloPorts)
                        break;
                    if (IsToken(p.Name, ShowBusConstants.HelloKindChars) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var port)
                        && port is > 0 and <= 65535)
                        ports[p.Name] = port;
                }
            }
            if (!ports.ContainsKey("http") && !ports.ContainsKey("wire") && !ports.ContainsKey("ws"))
            {
                if (Int(root, "web") is > 0 and <= 65535 and var web)
                    ports["http"] = web;
                if (Int(root, "wire") is > 0 and <= 65535 and var wire)
                    ports["wire"] = wire;
                if (Int(root, "port") is > 0 and <= 65535 and var legacy)
                    ports["http"] = legacy;
            }
            var caps = new List<string>();
            if (root.TryGetProperty("caps", out var ce) && ce.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in ce.EnumerateArray())
                {
                    if (caps.Count >= ShowBusConstants.HelloCaps)
                        break;
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { } cap && CapShape(cap))
                        caps.Add(cap);
                }
            }
            var proto = Int(root, "proto");
            var clock = root.TryGetProperty("clock", out var ck) && ck.ValueKind == JsonValueKind.Number && ck.TryGetInt64(out var ms) ? ms : 0;
            var name = NameOf(Str(root, "name"));
            var inst = Str(root, "inst");
            return new ShowBusHello(t!, 1, proto <= 0 ? 1 : proto, id!, name.Length > 0 ? name : id!, NameOf(Str(root, "host") ?? Str(root, "machine")),
                ports, caps, clock, InstShape(inst) ? inst : null, Str(root, "tls")) { Build = BuildOrNull(Str(root, "build")) };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // JsonDocument keeps a string escaping a lone surrogate, or holding invalid UTF-8, and its read-out throws this.
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : 0;

    private static bool IsToken(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length > max)
            return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        return true;
    }

    /// <summary>A build as a hello may carry one: 1–<see cref="MaxBuildChars"/> characters of SemVer's alphabet (letters, digits,
    /// '.', '-' and '+'); null otherwise.</summary>
    private static string? BuildOrNull(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > MaxBuildChars)
            return null;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
                return null;
        return s;
    }

    /// <summary>At most <paramref name="max"/> UTF-16 units of <paramref name="s"/>, never ending on the first half of a surrogate pair.</summary>
    private static string Clip(string s, int max) =>
        max <= 0 ? "" : s.Length <= max ? s : char.IsHighSurrogate(s[max - 1]) ? s[..(max - 1)] : s[..max];

    public override string ToString() => Encoding.UTF8.GetString(ToJson());
}

public enum PeerLiveness
{
    Live,
    Stale,
    Gone,
}

public static class Liveness
{
    /// <summary>A peer heard <paramref name="silentMs"/> ago: live while the silence is within the stale mark, stale while it is within
    /// the gone mark, then gone. A limit includes itself (Show Bus 1.3 §13's "within"): 6000 ms is live, 6001 stale, 12000 stale,
    /// 12001 gone.</summary>
    public static PeerLiveness Of(long silentMs) =>
        silentMs <= ShowBusConstants.StaleMs ? PeerLiveness.Live
        : silentMs <= ShowBusConstants.GoneMs ? PeerLiveness.Stale
        : PeerLiveness.Gone;

    /// <summary>The largest double below 1: a jitter draw of 1 is read as the top of [0, 1).</summary>
    private static readonly double BelowOne = Math.BitDecrement(1.0);

    /// <summary>The wait before redial <paramref name="attempt"/> (failed dials since the link was last stable, from 0) with a jitter
    /// draw in [0, 1): 13.15.1's wait, the PeerLink core's (<see cref="PeerLink.RedialWait"/>), one rule for every link.</summary>
    public static int RedialMs(int attempt, double jitter01) =>
        (int)PeerLink.RedialWait(Math.Clamp(attempt, 0, 16), Math.Clamp(jitter01, 0, BelowOne));
}
