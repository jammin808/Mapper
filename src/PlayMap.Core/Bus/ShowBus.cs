using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PlayMap.Core.Bus;

/// <summary>The Show Bus 1.2 numbers (Stagewatch's docs/SHOWBUS.md is the contract) and PlayMap's ports.
/// PlayMap's own ports sit clear of Patterns (9696–9701) and Stagewatch (9923–9928).</summary>
public static class ShowBusConstants
{
    /// <summary>Every node broadcasts its hello here every 2 s and every node listens (SO_REUSEADDR).</summary>
    public const int HelloPort = 9928;
    public const int HelloEveryMs = 2000;
    public const int HelloMaxBytes = 2048;

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
    /// <summary>PlayMap's capabilities: the contract's words plus PlayMap's own (dotted words name a finer
    /// ability, the way Patterns' "video.transport" does).</summary>
    public static readonly IReadOnlyList<string> PlayMapCaps =
    [
        "video", "video.clock", "video.transport", "video.seek", "video.end",
        "state", "context", "receipts", "clock", "mcp",
        "playlist", "media", "mapping", "scan",
    ];

    public int Port(string name) => Ports.TryGetValue(name, out var p) ? p : 0;

    /// <summary>The datagram: compact JSON. Top-level "web" and "wire" ride beside "ports" for older readers
    /// (Stagewatch's Patterns finder reads a top-level "wire").</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
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
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Parses a datagram with the strictest reader's rules (Stagewatch's): ≤ 2048 bytes, v == 1,
    /// t ≤ 16 and id ≤ 40 characters of [A-Za-z0-9_-], ≤ 16 caps kept; ports 1–65535; the legacy top-level
    /// web/wire/port used when ports names none. Null when it is not a hello.</summary>
    public static ShowBusHello? Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length is 0 or > 4096)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(datagram.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
                return null;
            var t = Str(root, "t");
            var id = Str(root, "id");
            if (!IsToken(t, 16) || !IsToken(id, 40))
                return null;
            var ports = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("ports", out var pe) && pe.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in pe.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var port) && port is > 0 and <= 65535)
                        ports[p.Name] = port;
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
                    if (caps.Count >= 16)
                        break;
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 and <= 32 } cap)
                        caps.Add(cap);
                }
            }
            var proto = Int(root, "proto");
            var clock = root.TryGetProperty("clock", out var ck) && ck.ValueKind == JsonValueKind.Number && ck.TryGetInt64(out var ms) ? ms : 0;
            return new ShowBusHello(t!, 1, proto <= 0 ? 1 : proto, id!, Trim(Str(root, "name") ?? id!, 64), Trim(Str(root, "host") ?? Str(root, "machine") ?? "", 64),
                ports, caps, clock, Str(root, "inst"), Str(root, "tls"));
        }
        catch (JsonException)
        {
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

    private static string Trim(string s, int max) => s.Length <= max ? s.Trim() : s[..max].Trim();

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
    public static PeerLiveness Of(long silentMs) =>
        silentMs < ShowBusConstants.StaleMs ? PeerLiveness.Live
        : silentMs < ShowBusConstants.GoneMs ? PeerLiveness.Stale
        : PeerLiveness.Gone;

    /// <summary>The redial delay for an attempt (0-based) with a jitter draw in [0, 1).</summary>
    public static int RedialMs(int attempt, double jitter01)
    {
        var baseMs = Math.Min(ShowBusConstants.RedialMaxMs, ShowBusConstants.RedialMinMs * Math.Pow(2, Math.Clamp(attempt, 0, 16)));
        return (int)Math.Round(baseMs * (0.8 + 0.4 * Math.Clamp(jitter01, 0, 1)));
    }
}
