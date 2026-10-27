using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Bus;

/// <summary>
/// Show Bus 1.3 §13 PeerLink, the pure part, as PlayMap keeps it (docs/SHOWBUS.md, the copy of the master; the core the master's own
/// tree keeps, byte for byte in behaviour, so one ```peerlink block holds both): the code's canonical form, K, kid, the transcript T,
/// both proofs, the lid, the session credential S and an enrolled K; the level-0 proof (13.6.8); the parsers an acceptor reads LINK
/// with (13.6.4, 13.6.5, 13.7.1) and a connector reads the acceptor's answer with; liveness (13.10.3), the redial wait (13.15.1), the
/// clock's admission and quality (13.11), request ids (13.12.1) and 13.12.3's table of answers. Nothing here reads a clock, a socket or
/// a file: time comes in as a number, j as a number, so every row of 13.20's block runs on it. <see cref="PeerLinkConnector"/> is
/// 13.16's machine on top of it, <see cref="PeerClock"/> 13.11's window and <see cref="PeerReceipts"/> 13.12.6's store. It uses only
/// the runtime.
/// </summary>
public static class PeerLink
{
    /// <summary>The PeerLink version this core speaks (§14 <c>peerlink.version</c>).</summary>
    public const int Version = 1;

    /// <summary>T's first line, and the start of the code, kid and lid labels (§14 <c>peerlink.label</c>).</summary>
    public const string Label = "showbus-peerlink-1";

    public const int NonceBytes = 16, KidBytes = 8, LidBytes = 8, CanWords = 8;
    public const long HandshakeMs = 10_000, HoldRefusedMs = 60_000, StableMs = 10_000;
    public const int JitterPct = 20, PingsMatched = 8;
    public const long StaleMs = 6_000, DropMs = 12_000, RedialFirstMs = ShowBusConstants.RedialMinMs, RedialMaxMs = ShowBusConstants.RedialMaxMs;
    public const long SelfGapMs = 2_000, ConnectTimeoutMs = 5_000;
    public const int InflightMax = 32;
    public const int ClockWindow = ShowBusConstants.OffsetWindow;
    public const long ClockRttMaxMs = 60_000, ClockFreshMs = 10_000, ClockApartMs = ShowBusConstants.ClocksApartMs;
    public const long ReceiptDeadlineMs = 3_000, ReceiptFinalMs = 30_000, ReceiptsKeptMs = 600_000;
    public const int ReceiptsKept = 256, ReceiptsAskMax = 32, RequestIdChars = 32;
    public const long PeerStaleMs = ShowBusConstants.StaleMs, ConflictClearMs = ShowBusConstants.ConflictClearMs;

    /// <summary>13.12.9's owed reports: retried from <see cref="ReportRetryFirstMs"/>, doubling to <see cref="ReportRetryMaxMs"/>, ±20 %,
    /// for <see cref="ReportRetryForMs"/>.</summary>
    public const long ReportRetryFirstMs = 3_000, ReportRetryMaxMs = 30_000, ReportRetryForMs = 1_800_000;

    /// <summary>The features a verdict's <c>can</c> may list (13.1), each a §14 <c>peerlink.feature</c> row.</summary>
    public static IReadOnlyList<string> Features { get; } = ["final", "receipts", "stuck", "expire", "enrol"];

    /// <summary>
    /// The §14 rows this core keeps, by name, each with the value it reads: a test holds every one to its row and to a read in this
    /// file. §13's rows an acceptor, a carrier's socket, timer or writer lane, or another part keeps are not here: the test names
    /// each, with what keeps it.
    /// </summary>
    public static IReadOnlyDictionary<string, long> Rows { get; } = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["peerlink.version"] = Version, ["peerlink.nonce.bytes"] = NonceBytes, ["peerlink.kid.bytes"] = KidBytes,
        ["peerlink.lid.bytes"] = LidBytes, ["peerlink.can.words"] = CanWords,
        ["peerlink.handshake.ms"] = HandshakeMs, ["peerlink.hold.refused.ms"] = HoldRefusedMs,
        ["peerlink.stable.ms"] = StableMs, ["peerlink.redial.jitter.pct"] = JitterPct, ["peerlink.pings.matched"] = PingsMatched,
        ["link.stale.ms"] = StaleMs, ["link.drop.ms"] = DropMs, ["link.redial.first.ms"] = RedialFirstMs,
        ["link.redial.max.ms"] = RedialMaxMs, ["link.selfgap.ms"] = SelfGapMs, ["link.connect.timeout.ms"] = ConnectTimeoutMs,
        ["link.inflight.max"] = InflightMax,
        ["clock.window"] = ClockWindow, ["clock.rtt.max.ms"] = ClockRttMaxMs, ["clock.fresh.ms"] = ClockFreshMs, ["clock.apart.ms"] = ClockApartMs,
        ["receipt.deadline.ms"] = ReceiptDeadlineMs, ["receipt.final.ms"] = ReceiptFinalMs,
        ["receipts.kept"] = ReceiptsKept, ["receipts.kept.ms"] = ReceiptsKeptMs, ["receipts.ask.max"] = ReceiptsAskMax,
        ["request.id.chars"] = RequestIdChars, ["peer.stale.ms"] = PeerStaleMs, ["peer.conflict.clear.ms"] = ConflictClearMs,
        ["report.retry.first.ms"] = ReportRetryFirstMs, ["report.retry.max.ms"] = ReportRetryMaxMs, ["report.retry.for.ms"] = ReportRetryForMs,
    };

    /// <summary>
    /// This process's inst for the request ids its connectors make (13.12.1): 8 lowercase hex from the CSPRNG, made once per process,
    /// so an id never repeats in a later one.
    /// </summary>
    public static string ProcessInst { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ 13.5 keys

    /// <summary>
    /// 13.5.1: a code's canonical form — <c>-</c> and the listed spaces and separators removed, <c>a</c>–<c>z</c> upper-cased — or
    /// null when what is left is not <c>[A-Z0-9]{1,64}</c>, which LINK never proves.
    /// </summary>
    public static string? Canon(string code)
    {
        var s = new StringBuilder(code.Length);
        foreach (var c in code)
        {
            if (CanonRemoves(c)) continue;
            s.Append(c is >= 'a' and <= 'z' ? (char)(c - 'a' + 'A') : c);
        }
        if (s.Length is < 1 or > 64) return null;
        for (var i = 0; i < s.Length; i++)
            if (s[i] is not (>= 'A' and <= 'Z' or >= '0' and <= '9')) return null;
        return s.ToString();
    }

    /// <summary>The characters 13.5.1's canonical form removes.</summary>
    internal static bool CanonRemoves(char c)
        => c is '-' or (>= '\u0009' and <= '\u000D') or '\u0020' or '\u0085' or '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A')
            or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    /// <summary>13.5.1: a code's K, SHA-256 of the code label and its canonical form.</summary>
    public static byte[] CodeK(string canonical) => SHA256.HashData(Utf8(Label + " code\n" + canonical));

    /// <summary>13.5.1: a machine-made key's K, SHA-256 of the key — what Stagewatch already stores for a key.</summary>
    public static byte[] KeyK(string key) => SHA256.HashData(Utf8(key));

    /// <summary>13.5.1: a key's kid, the first <see cref="KidBytes"/> bytes of SHA-256(kid label ‖ K) as lowercase hex. Not a secret.</summary>
    public static string Kid(byte[] k) => Hex(SHA256.HashData([.. Utf8(Label + " kid\n"), .. k]))[..(KidBytes * 2)];

    // ------------------------------------------------------------------ 13.6 the handshake

    /// <summary>
    /// 13.6.1: the transcript T, its ten lines joined by LF with no final LF; <paramref name="server"/> and <paramref name="client"/>
    /// are <c>t:id</c>. Null when any field breaks its shape, so no LF can enter T.
    /// </summary>
    public static string? Transcript(string carrier, long offer, long speaks, string server, string client, string kid, string snonce,
        string cnonce, string bind)
    {
        if (carrier is not ("line" or "ws") || offer < 0 || speaks < 0 || !IsNode(server) || !IsNode(client) || !IsKid(kid)
            || !IsHex(snonce, NonceBytes * 2) || !IsHex(cnonce, NonceBytes * 2) || !IsBind(bind)) return null;
        return string.Join('\n', Label, "carrier=" + carrier, "offer=" + offer.ToString(Inv), "speaks=" + speaks.ToString(Inv),
            "server=" + server, "client=" + client, "kid=" + kid, "snonce=" + snonce, "cnonce=" + cnonce, "bind=" + bind);
    }

    /// <summary>13.6.2: the connector's proof, HMAC-SHA256(K, "client\n" ‖ T), lowercase hex.</summary>
    public static string ClientProof(byte[] k, string t) => Mac(k, "client\n" + t);

    /// <summary>13.6.2: the acceptor's proof, HMAC-SHA256(K, "server\n" ‖ T), lowercase hex.</summary>
    public static string ServerProof(byte[] k, string t) => Mac(k, "server\n" + t);

    /// <summary>13.6.2: the link's id, the first <see cref="LidBytes"/> bytes of SHA-256(lid label ‖ T). Not a secret.</summary>
    public static string Lid(string t) => Hex(SHA256.HashData(Utf8(Label + " lid\n" + t)))[..(LidBytes * 2)];

    /// <summary>13.6.2: on <c>ws</c>, the session credential S = HMAC-SHA256(K, "session\n" ‖ T). A secret: it rides only side calls (13.7).</summary>
    public static string Session(byte[] k, string t) => Mac(k, "session\n" + t);

    /// <summary>13.5.7: an enrolled key, K_link = HMAC-SHA256(K_code, "enrol\n" ‖ T). Never sent; its kid is <see cref="Kid"/>.</summary>
    public static byte[] EnrolK(byte[] codeK, string t) => HMACSHA256.HashData(codeK, Utf8("enrol\n" + t));

    /// <summary>13.5.7: only a code proved over TLS enrols; a code proved over plain never does.</summary>
    public static bool MayEnrol(string kid, string bind) => kid == "code" && bind.StartsWith("tls:sha256:", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="given"/> is the proof <paramref name="expected"/>, compared in constant time once both are lowercase hex
    /// of one length (13.6.3). A malformed proof is a wrong proof.
    /// </summary>
    public static bool Verify(string expected, string? given)
        => given is not null && IsHex(given, expected.Length) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given));

    /// <summary>The version an acceptor of this core speaks to a connector that offers <paramref name="offer"/>; null when none (a <c>version</c> refusal).</summary>
    public static int? Speaks(long offer) => offer >= Version ? Version : null;

    // ------------------------------------------------------------------ 13.6.8 level 0

    /// <summary>13.6.8: the legacy proof answers only a NONCE of exactly 32 lowercase hex.</summary>
    public static bool IsProvableNonce(string nonce) => IsHex(nonce, 32);

    /// <summary>
    /// 13.6.8: the token as §7's 1.1 desks compare it — <c>-</c>, space, tab, CR and LF removed, every other character upper-cased
    /// alone, one UTF-16 unit at a time, by its simple mapping. Null when nothing is left, or when what is left still holds a character
    /// 13.5.1's canonical form removes, on which 1.1 desks differ: such a token is not proved.
    /// </summary>
    public static string? LegacyKey(string token)
    {
        var s = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            if (CanonRemoves(c)) return null;
            s.Append(char.ToUpperInvariant(c));
        }
        return s.Length == 0 ? null : s.ToString();
    }

    /// <summary>
    /// 13.6.8: §7's AUTH-PROOF, HMAC-SHA256(the legacy key, nonce + the HELLO name), lowercase hex; null for a nonce that is not 32
    /// lowercase hex or a token 13.6.8 does not prove.
    /// </summary>
    public static string? LegacyProof(string token, string nonce, string name)
        => IsProvableNonce(nonce) && LegacyKey(token) is { } key ? Hex(HMACSHA256.HashData(Utf8(key), Utf8(nonce + name))) : null;

    // ------------------------------------------------------------------ the acceptor's parsers

    /// <summary>
    /// 13.6.4 and 13.6.5 as an acceptor reads them: the connector's ask, or its proof, or the class it is refused with
    /// (<c>malformed</c>, <c>version</c>, or <c>refused</c> for a malformed proof, which is a wrong proof).
    /// </summary>
    public readonly record struct LinkParse(LinkAsk? Ask, string? Proof, string? Refused)
    {
        public static LinkParse Malformed => new(null, null, "malformed");
    }

    /// <summary>A connector's LINK: what it offers, who it says it is, its nonce and the kid it will prove.</summary>
    public sealed record LinkAsk(long Offer, string T, string Id, string CNonce, string Kid);

    /// <summary>
    /// 13.6.4: a LINK line, with or without its <c>@id</c>: <c>LINK &lt;offer&gt; &lt;t&gt; &lt;id&gt; &lt;cnonce&gt; &lt;kid&gt;</c> or
    /// <c>LINK PROOF &lt;64 hex&gt;</c>. A word it does not know refuses the line (malformed); an offer below this core's version is a
    /// <c>version</c> refusal; a proof that is not 64 lowercase hex is refused as a wrong proof.
    /// </summary>
    public static LinkParse ParseLinkLine(string line)
    {
        var w = line.Split(' ');
        var i = 0;
        if (w.Length > 0 && w[0].StartsWith('@'))
        {
            if (!IsLineId(w[0][1..])) return LinkParse.Malformed;
            i = 1;
        }
        if (w.Length <= i || w[i] != "LINK") return LinkParse.Malformed;
        if (w.Length > i + 1 && w[i + 1] == "PROOF")
            return w.Length != i + 3 ? LinkParse.Malformed : IsHex(w[i + 2], 64) ? new(null, w[i + 2], null) : new(null, null, "refused");
        if (w.Length != i + 6 || !IsCount(w[i + 1]) || !IsKind(w[i + 2]) || !IsId(w[i + 3]) || !IsHex(w[i + 4], NonceBytes * 2) || !IsKid(w[i + 5]))
            return LinkParse.Malformed;
        var offer = long.Parse(w[i + 1], NumberStyles.None, Inv);
        return Speaks(offer) is null ? new(null, null, "version") : new(new LinkAsk(offer, w[i + 2], w[i + 3], w[i + 4], w[i + 5]), null, null);
    }

    /// <summary>
    /// 13.6.5: a <c>link=</c> socket's query and the connector's <c>link</c> message: its <c>client</c> must equal the query's
    /// <c>node</c> and <c>t</c>. The ask carries the proof; a missing or misshapen field is <c>malformed</c>, a proof that is not 64
    /// lowercase hex <c>refused</c>, an offer below this core's version <c>version</c>.
    /// </summary>
    public static LinkParse ParseLinkMessage(string query, JsonElement message)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || !q.TryAdd(pair[..eq], pair[(eq + 1)..])) return LinkParse.Malformed;
        }
        if (!q.TryGetValue("node", out var node) || !q.TryGetValue("t", out var t) || !q.TryGetValue("link", out var link)
            || !IsId(node) || !IsKind(t) || !IsCount(link)) return LinkParse.Malformed;
        if (message.ValueKind != JsonValueKind.Object || Text(message, "type") != "link"
            || !message.TryGetProperty("client", out var client) || client.ValueKind != JsonValueKind.Object
            || Text(client, "t") != t || Text(client, "id") != node
            || Text(message, "cnonce") is not { } cnonce || !IsHex(cnonce, NonceBytes * 2)
            || Text(message, "kid") is not { } kid || !IsKid(kid)
            || Text(message, "proof") is not { } proof) return LinkParse.Malformed;
        if (!IsHex(proof, 64)) return new(null, null, "refused");
        var offer = long.Parse(link, NumberStyles.None, Inv);
        return Speaks(offer) is null ? new(null, null, "version") : new(new LinkAsk(offer, t, node, cnonce, kid), proof, null);
    }

    /// <summary>
    /// 13.6.4 as a connector reads the acceptor's answer to its LINK: <c>OK [@id] LINK &lt;speaks&gt; &lt;t&gt; &lt;id&gt; &lt;snonce&gt; &lt;bind&gt;</c>,
    /// exactly; null for anything else.
    /// </summary>
    public static PeerOffer? ParseLinkAnswer(string line)
    {
        var w = line.Split(' ');
        var i = 1;
        if (w.Length < 1 || w[0] != "OK") return null;
        if (w.Length > 1 && w[1].StartsWith('@'))
        {
            if (!IsLineId(w[1][1..])) return null;
            i = 2;
        }
        if (w.Length != i + 6 || w[i] != "LINK" || !IsCount(w[i + 1]) || !IsKind(w[i + 2]) || !IsId(w[i + 3]) || !IsHex(w[i + 4], NonceBytes * 2) || !IsBind(w[i + 5]))
            return null;
        return new PeerOffer(long.Parse(w[i + 1], NumberStyles.None, Inv), w[i + 2], w[i + 3], w[i + 4], w[i + 5]);
    }

    /// <summary>
    /// 13.6.5 as a connector reads the acceptor's first message on a <c>link=</c> socket:
    /// <c>{"type":"link","v":1,"speaks":1,"server":{"t","id"},"snonce","bind"}</c>; null for anything else.
    /// </summary>
    public static PeerOffer? ParseLinkOffer(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || Text(message, "type") != "link"
            || !message.TryGetProperty("speaks", out var speaks) || speaks.ValueKind != JsonValueKind.Number || !speaks.TryGetInt64(out var n) || n < 0
            || !message.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object
            || Text(server, "t") is not { } t || !IsKind(t) || Text(server, "id") is not { } id || !IsId(id)
            || Text(message, "snonce") is not { } snonce || !IsHex(snonce, NonceBytes * 2)
            || Text(message, "bind") is not { } bind || !IsBind(bind)) return null;
        return new PeerOffer(n, t, id, snonce, bind);
    }

    /// <summary>The words a proof that does not verify is refused with, at either end (13.6.3).</summary>
    public const string ProofWords = "the proof did not verify: a wrong key, or another machine between us";

    /// <summary>13.7.1: <c>Authorization: ShowBus lid=&lt;16 hex&gt;, s=&lt;64 hex&gt;</c>, exactly; null for anything else.</summary>
    public static (string Lid, string S)? ParseShowBus(string? authorization)
    {
        const string Head = "ShowBus lid=", Mid = ", s=";
        if (authorization is null || !authorization.StartsWith(Head, StringComparison.Ordinal)) return null;
        var rest = authorization[Head.Length..];
        var lidChars = LidBytes * 2;
        if (rest.Length != lidChars + Mid.Length + 64 || !rest.AsSpan(lidChars, Mid.Length).SequenceEqual(Mid)) return null;
        var lid = rest[..lidChars];
        var s = rest[(lidChars + Mid.Length)..];
        return IsHex(lid, lidChars) && IsHex(s, 64) ? (lid, s) : null;
    }

    /// <summary>
    /// What an acceptor answers a side call by its session credential: 200, or 401 with <c>relink</c>; <see cref="Ends"/> names the lid
    /// whose link the acceptor ends, its S having crossed the network in clear.
    /// </summary>
    public readonly record struct SessionCheck(int Status, bool Relink, string Words, string? Ends = null);

    /// <summary>
    /// 13.7.1: a side call's session, checked against the S of the live, verified link its lid names (<paramref name="sessionOf"/>
    /// answers null for a lid with no such link). A header that is not 13.7.1's, a lid with no live link, or a wrong S is
    /// <c>401</c> with <c>relink</c>: the connector links again. A right S that came in clear from another machine
    /// (<paramref name="inClearFromAnother"/>: no TLS, and a remote address that is not loopback) is refused the same way, and its link
    /// is ended (<see cref="SessionCheck.Ends"/>): anyone on the path read it. A wrong one ends nothing, so no one ends a link by its
    /// lid, which is not a secret.
    /// </summary>
    public static SessionCheck CheckSession(string? authorization, Func<string, string?> sessionOf, bool inClearFromAnother = false)
    {
        if (ParseShowBus(authorization) is not { } a) return new(401, true, "this call carries no session of a live link — link again");
        if (sessionOf(a.Lid) is not { } s || !Verify(s, a.S)) return new(401, true, "the link ended");
        return inClearFromAnother
            ? new(401, true, "a session credential came in clear from another machine — its link is ended; link again", a.Lid)
            : new(200, false, "");
    }

    // ------------------------------------------------------------------ 13.10, 13.11, 13.15: the pure rules

    /// <summary>13.10.3: live while the silence is within <see cref="StaleMs"/>, stale within <see cref="DropMs"/>, then down.</summary>
    public static PeerLinkState Liveness(long silenceMs)
        => silenceMs <= StaleMs ? PeerLinkState.Live : silenceMs <= DropMs ? PeerLinkState.Stale : PeerLinkState.Down;

    /// <summary>
    /// 13.15.1: the wait before redial <paramref name="n"/> (failed dials since the link was last stable, from 0):
    /// round(min(max, first × 2ⁿ) × (0.8 + 0.4 j)) for j in [0, 1).
    /// </summary>
    public static long RedialWait(int n, double j)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "n counts from 0");
        if (!(j >= 0 && j < 1)) throw new ArgumentOutOfRangeException(nameof(j), "j lies in [0, 1)");
        var b = Math.Min(RedialMaxMs, RedialFirstMs * Math.Pow(2, Math.Min(n, 30)));
        var p = JitterPct / 100.0;
        return (long)Math.Round(b * (1 - p + 2 * p * j), MidpointRounding.AwayFromZero);
    }

    /// <summary>13.15.1's j: uniform in [0, 1), from a generator seeded once per process from the CSPRNG — a shuffle so a room of
    /// connectors does not redial in step, not a secret. A test injects its own.</summary>
    public static Func<double> ProcessJitter { get; } = Seeded();

    static Func<double> Seeded()
    {
        var random = new Pcg32(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)));
        var gate = new Lock();
        return () =>
        {
            lock (gate) return random.NextDouble();
        };
    }

    /// <summary>
    /// 13.12.9: the wait before try <paramref name="n"/> (from 0) of an owed report: <see cref="ReportRetryFirstMs"/> doubling to
    /// <see cref="ReportRetryMaxMs"/>, moved ±<see cref="JitterPct"/> % by j in [0, 1).
    /// </summary>
    public static long ReportRetryWait(int n, double j)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "n counts from 0");
        if (!(j >= 0 && j < 1)) throw new ArgumentOutOfRangeException(nameof(j), "j lies in [0, 1)");
        var b = Math.Min(ReportRetryMaxMs, ReportRetryFirstMs * Math.Pow(2, Math.Min(n, 30)));
        var p = JitterPct / 100.0;
        return (long)Math.Round(b * (1 - p + 2 * p * j), MidpointRounding.AwayFromZero);
    }

    /// <summary>13.12.9: an owed report is tried again while it has been owed for no more than <see cref="ReportRetryForMs"/>.</summary>
    public static bool ReportStillOwed(long owedForMs) => owedForMs <= ReportRetryForMs;

    // ------------------------------------------------------------------ 13.12 request ids and answers

    /// <summary>13.12.1: a request id, <c>&lt;prefix&gt;&lt;n&gt;-&lt;inst&gt;</c>: 1–4 letters, a counter from 1, the hello's inst.</summary>
    public static string RequestId(string prefix, long n, string inst)
    {
        if (prefix.Length is < 1 or > 4 || !prefix.All(char.IsAsciiLetter)) throw new ArgumentException("a prefix is 1-4 letters", nameof(prefix));
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "the counter starts at 1");
        var id = prefix + n.ToString(Inv) + "-" + inst;
        return IsLineId(id) ? id : throw new ArgumentException("a request id is at most " + RequestIdChars.ToString(Inv) + " letters, digits, - and _", nameof(inst));
    }

    /// <summary>The outcome word 13.12.2 names.</summary>
    public static string Word(this PeerOutcome outcome) => outcome switch
    {
        PeerOutcome.NotSent => "not-sent",
        PeerOutcome.Refused => "refused",
        PeerOutcome.Accepted => "accepted",
        PeerOutcome.Applied => "applied",
        _ => "unconfirmed",
    };

    const string RepeatWords = "answered from its record — not run again";
    const string SecondGo = "asks for a second GO — a person's";
    const string NoStage = "took it — this node does not say whether it ran";

    /// <summary>
    /// 13.12.3 for a line answer: <c>OK @id …</c> or <c>ERR @id …</c>, with <c>final</c> after the id where it is a follow-up. Null for
    /// a line that is neither; an <c>OK</c> or <c>ERR</c> that echoes no id is an acceptor older than 1.1, unconfirmed.
    /// </summary>
    public static (string? Id, PeerReading Reading)? ReadLine(string line)
    {
        bool ok;
        string rest;
        if (line == "OK" || line.StartsWith("OK ", StringComparison.Ordinal)) { ok = true; rest = line.Length > 2 ? line[3..] : ""; }
        else if (line == "ERR" || line.StartsWith("ERR ", StringComparison.Ordinal)) { ok = false; rest = line.Length > 3 ? line[4..] : ""; }
        else return null;
        if (!rest.StartsWith('@'))
            return (null, new(PeerOutcome.Unconfirmed, "sent — this node gives no receipts; look at its state"));
        var space = rest.IndexOf(' ');
        var id = space < 0 ? rest[1..] : rest[1..space];
        rest = space < 0 ? "" : rest[(space + 1)..];
        var final = false;
        if (rest == "final" || rest.StartsWith("final ", StringComparison.Ordinal)) { final = true; rest = rest.Length > 5 ? rest[6..] : ""; }
        if (!ok) return (id, new(PeerOutcome.Refused, rest, Final: final));
        if (rest.StartsWith('{') && TryObject(rest) is { } json)
            return (id, FromJson(json, final, stageless: PeerOutcome.Applied));
        return (id, new(PeerOutcome.Applied, rest, Final: final));
    }

    /// <summary>13.12.3 for a JSON receipt (a <c>ws</c> receipt, an HTTP body that carries a stage, a <c>RECEIPTS</c> answer's entry).</summary>
    public static PeerReading ReadReceipt(JsonElement receipt)
        => receipt.ValueKind == JsonValueKind.Object
            ? FromJson(receipt, Flag(receipt, "final"), stageless: PeerOutcome.Accepted)
            : new(PeerOutcome.Unconfirmed, "an answer this node cannot read — it may have run");

    /// <summary>
    /// 13.12.3 for an HTTP answer; <paramref name="status"/> null when no response came after the request was written.
    /// <paramref name="retryAfterSeconds"/> is the 429's <c>Retry-After</c>.
    /// </summary>
    public static PeerReading ReadHttp(int? status, string? body, int retryAfterSeconds = 0)
    {
        if (status is not { } code) return new(PeerOutcome.Unconfirmed, "no answer — it may have run");
        if (code == 408 || code >= 500) return new(PeerOutcome.Unconfirmed, code.ToString(Inv) + " — it may have run");
        var json = body is null ? null : TryObject(body);
        var words = json is { } j ? BodyWords(j) : "";
        switch (code)
        {
            case 401:
                return json is { } r && Flag(r, "relink") ? new(PeerOutcome.Refused, words, Relink: true) : new(PeerOutcome.Refused, words, Hold: true);
            case 429:
                return new(PeerOutcome.Refused, "rate limited — again in " + retryAfterSeconds.ToString(Inv) + " s", RetryAfter: retryAfterSeconds);
            case >= 200 and < 300:
                if (json is not { } o) return new(PeerOutcome.Accepted, NoStage);
                if (o.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.False) return new(PeerOutcome.Refused, words, Repeat: Flag(o, "repeat"));
                if (o.TryGetProperty("stage", out _)) return ReadReceipt(o);
                if (Text(o, "msg") is { } msg && (msg == "OK" || msg.StartsWith("OK ", StringComparison.Ordinal))) return new(PeerOutcome.Applied, words);
                return new(PeerOutcome.Accepted, NoStage);
            default:
                return new(PeerOutcome.Refused, words.Length > 0 ? words : code.ToString(Inv));
        }
    }

    /// <summary>
    /// 13.12.3's rows with no answer: a command never written is not-sent; one written is unconfirmed once its link dropped
    /// or more than <see cref="ReceiptFinalMs"/> passed with no final answer (an answer at that limit still counts); null while it
    /// still waits.
    /// </summary>
    public static PeerReading? ReadUnanswered(bool written, long silenceMs, bool dropped, string node, string why = "the link is not live")
    {
        if (!written) return new(PeerOutcome.NotSent, why);
        if (dropped) return new(PeerOutcome.Unconfirmed, "the link dropped before " + node + " answered");
        return silenceMs > ReceiptFinalMs ? new(PeerOutcome.Unconfirmed, "no answer in " + (ReceiptFinalMs / 1000).ToString(Inv) + " s — it may have run") : null;
    }

    static PeerReading FromJson(JsonElement o, bool final, PeerOutcome stageless)
    {
        var repeat = Flag(o, "repeat");
        long? late = o.TryGetProperty("lateMs", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt64(out var lm) ? lm : null;
        PeerReading R(PeerOutcome outcome, string words)
            => new(outcome, repeat ? Join(words, RepeatWords) : words, Final: final, Repeat: repeat, LateMs: late);
        if (o.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) return R(PeerOutcome.Refused, BodyWords(o));
        var stage = Text(o, "stage");
        var outcome = Text(o, "outcome");
        var effect = Text(o, "effect") ?? "";
        if (stage == "accepted") return R(PeerOutcome.Accepted, effect);
        if (stage == "applied") return R(PeerOutcome.Applied, effect);
        if (outcome == "Requested") return R(PeerOutcome.Accepted, PatternsWords(o));
        if (outcome == "Confirm") return R(PeerOutcome.Accepted, Join(PatternsWords(o), SecondGo));
        if (outcome == "Done") return R(PeerOutcome.Applied, effect.Length > 0 ? effect : PatternsWords(o));
        if (stage is null && outcome is null) return R(stageless, stageless == PeerOutcome.Accepted ? NoStage : effect.Length > 0 ? effect : PatternsWords(o));
        return R(PeerOutcome.Accepted, outcome ?? stage ?? "");
    }

    /// <summary>The words a Patterns answer carries: its effect, confirm text, words or message.</summary>
    static string PatternsWords(JsonElement o) => Text(o, "effect") ?? Text(o, "confirm") ?? Text(o, "words") ?? Text(o, "msg") ?? "";

    /// <summary>A body's words: <c>msg</c> after its <c>OK</c> or <c>ERR</c> and any <c>@id</c>, else <c>error</c>.</summary>
    static string BodyWords(JsonElement o)
    {
        if (Text(o, "msg") is { } msg)
        {
            var rest = msg.StartsWith("OK", StringComparison.Ordinal) ? msg[2..] : msg.StartsWith("ERR", StringComparison.Ordinal) ? msg[3..] : msg;
            rest = rest.TrimStart(' ');
            if (rest.StartsWith('@')) rest = rest.IndexOf(' ') is var sp and > 0 ? rest[(sp + 1)..] : "";
            return rest;
        }
        return Text(o, "error") ?? "";
    }

    static string Join(string a, string b) => a.Length == 0 ? b : a + " — " + b;

    // ------------------------------------------------------------------ shapes

    /// <summary>A node kind (§2's <c>t</c>): 1–16 of <c>[a-z0-9_-]</c>.</summary>
    public static bool IsKind(string? t) => t is { Length: >= 1 and <= 16 } && t.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    /// <summary>A node id (§2): 6–40 of <c>[A-Za-z0-9_-]</c>.</summary>
    public static bool IsId(string? id) => id is { Length: >= 6 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>A kid: 16 lowercase hex, or the word <c>code</c>.</summary>
    public static bool IsKid(string? kid) => kid == "code" || IsHex(kid, KidBytes * 2);

    /// <summary>Exactly <paramref name="length"/> lowercase hex digits.</summary>
    public static bool IsHex(string? s, int length) => s is not null && s.Length == length && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A transcript's <c>bind</c>: <c>tls:sha256:</c> and 64 lowercase hex, or <c>plain</c>.</summary>
    public static bool IsBind(string? bind) => bind == "plain" || (bind is not null && bind.StartsWith("tls:sha256:", StringComparison.Ordinal) && IsHex(bind[11..], 64));

    static bool IsNode(string tid) => tid.IndexOf(':') is var i and > 0 && IsKind(tid[..i]) && IsId(tid[(i + 1)..]);

    static bool IsLineId(string id) => id is { Length: >= 1 and <= RequestIdChars } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    static bool IsCount(string s) => s.Length is >= 1 and <= 9 && s.All(char.IsAsciiDigit);

    // ------------------------------------------------------------------ bytes and JSON

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    static string Mac(byte[] k, string data) => Hex(HMACSHA256.HashData(k, Utf8(data)));

    internal static string? Text(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Flag(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static JsonElement? TryObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The acceptor's answer to LINK (13.6.4's <c>OK LINK</c>, 13.6.5's first <c>link</c> message): the version it speaks, who it says it
/// is, its nonce and the certificate it says this connection shows (<c>tls:sha256:…</c> or <c>plain</c>). Nothing here is a secret.
/// </summary>
public sealed record PeerOffer(long Speaks, string T, string Id, string SNonce, string Bind);

/// <summary>13.12.2's outcomes. One only moves forward.</summary>
public enum PeerOutcome
{
    NotSent,
    Refused,
    Accepted,
    Applied,
    Unconfirmed,
}

/// <summary>
/// What a connector read from one answer (13.12.3): its outcome and words, whether it was the final follow-up, a repeat answered
/// from the acceptor's record, a 401 that asks for a relink or holds the key, a 429's wait, and a late run's <c>lateMs</c>.
/// </summary>
public readonly record struct PeerReading(PeerOutcome Outcome, string Words, bool Final = false, bool Repeat = false, bool Relink = false,
    bool Hold = false, int RetryAfter = 0, long? LateMs = null);

/// <summary>13.16.1's closed set of link states.</summary>
public enum PeerLinkState
{
    Off,
    Finding,
    Connecting,
    Live,
    Stale,
    Down,
    Held,
    Refused,
}

/// <summary>A link's show liveness (13.10.5), kept apart from the link's.</summary>
public enum PeerShow
{
    Unknown,
    Live,
    Stuck,
}

/// <summary>A link's light (13.16): grey, green, amber or red, each drawn with its shape.</summary>
public enum PeerLight
{
    Grey,
    Green,
    Amber,
    Red,
}

/// <summary>The words of 13.16's sets as the link record writes them.</summary>
public static class PeerWords
{
    public static string Word(this PeerLinkState s) => s.ToString().ToLowerInvariant();

    public static string Word(this PeerShow s) => s.ToString().ToLowerInvariant();

    public static string Word(this PeerLight l) => l.ToString().ToLowerInvariant();

    /// <summary>The shape a light is drawn with beside its colour.</summary>
    public static string Mark(this PeerLight l) => l switch
    {
        PeerLight.Green => "✓",
        PeerLight.Amber => "△",
        PeerLight.Red => "✕",
        _ => "○",
    };
}

/// <summary>
/// 13.11's clock: the last <see cref="PeerLink.ClockWindow"/> admitted samples, the one with the smallest rtt believed (the newer of
/// two equal ones). One writer: the link that owns it.
/// </summary>
public sealed class PeerClock
{
    readonly List<(long Rtt, double Offset, long T4)> _samples = [];

    /// <summary>The believed sample's offset, error and t4; null with no sample.</summary>
    public readonly record struct Belief(double OffsetMs, long RttMs, long ErrMs, long T4);

    /// <summary>The round trip of one exchange: (t4 − t1) − (t3 − t2).</summary>
    public static long Rtt(long t1, long t2, long t3, long t4) => (t4 - t1) - (t3 - t2);

    /// <summary>13.11.2: a sample is refused when a stamp is ≤ 0, t4 &lt; t1, t3 &lt; t2, or its rtt is negative or above <see cref="PeerLink.ClockRttMaxMs"/>.</summary>
    public static bool Admits(long t1, long t2, long t3, long t4)
        => t1 > 0 && t2 > 0 && t3 > 0 && t4 > 0 && t4 >= t1 && t3 >= t2 && Rtt(t1, t2, t3, t4) is >= 0 and <= PeerLink.ClockRttMaxMs;

    /// <summary>13.11.3: errMs = ⌊(rtt + 1) / 2⌋ + 1.</summary>
    public static long ErrMs(long rtt) => (rtt + 1) / 2 + 1;

    /// <summary>13.11.5: CLOCKS APART when |offset| − err is above <see cref="PeerLink.ClockApartMs"/>.</summary>
    public static bool IsApart(double offsetMs, long errMs) => Math.Abs(offsetMs) - errMs > PeerLink.ClockApartMs;

    /// <summary>13.11.4: fresh while its age is within <see cref="PeerLink.ClockFreshMs"/>.</summary>
    public static bool IsFresh(long ageMs) => ageMs <= PeerLink.ClockFreshMs;

    /// <summary>13.11.4: fit for a tolerance while fresh and its error within that tolerance.</summary>
    public static bool IsFitFor(long ageMs, long errMs, long toleranceMs) => IsFresh(ageMs) && errMs <= toleranceMs;

    /// <summary>One matched exchange (13.10.2); false, and nothing kept, when 13.11.2 refuses it.</summary>
    public bool Add(long t1, long t2, long t3, long t4)
    {
        if (!Admits(t1, t2, t3, t4)) return false;
        _samples.Add((Rtt(t1, t2, t3, t4), ((t2 - t1) + (t3 - t4)) / 2.0, t4));
        if (_samples.Count > PeerLink.ClockWindow) _samples.RemoveAt(0);
        return true;
    }

    public int Samples => _samples.Count;

    public Belief? Believed
    {
        get
        {
            if (_samples.Count == 0) return null;
            var best = _samples[0];
            foreach (var s in _samples)
                if (s.Rtt <= best.Rtt) best = s;
            return new Belief(best.Offset, best.Rtt, ErrMs(best.Rtt), best.T4);
        }
    }

    /// <summary>Wall ms since the believed sample's t4; null with no sample.</summary>
    public long? AgeMs(long nowWall) => Believed is { } b ? nowWall - b.T4 : null;

    public bool Apart => Believed is { } b && IsApart(b.OffsetMs, b.ErrMs);

    public bool Fresh(long nowWall) => AgeMs(nowWall) is { } age && IsFresh(age);

    public bool FitFor(long toleranceMs, long nowWall) => Believed is { } b && IsFitFor(nowWall - b.T4, b.ErrMs, toleranceMs);

    /// <summary>13.11.7: emptied when the peer's inst changes or this node's wall clock steps.</summary>
    public void Clear() => _samples.Clear();
}

/// <summary>
/// 13.12.6, the acceptor's record of what it ran: per verified connector id, the receipts of its last
/// <see cref="PeerLink.ReceiptsKept"/> ids for <see cref="PeerLink.ReceiptsKeptMs"/>. A command whose id it holds is never run again.
/// A receipt is kept as its fields alone — <c>{"ok":…,"stage":…,"effect":…}</c>, or <c>{"ok":false,"error":…}</c> — which the acceptor
/// carries on its link: <c>OK @id {…}</c> on <c>line</c>, <c>{"type":"receipt","id":…,…}</c> on <c>ws</c>. One writer: the
/// acceptor's lane that reads commands.
/// </summary>
public sealed class PeerReceipts
{
    sealed class Kept
    {
        public readonly Dictionary<string, (long At, string? Receipt)> ById = new(StringComparer.Ordinal);
        public readonly Queue<string> Order = new();
    }

    readonly Dictionary<string, Kept> _by = new(StringComparer.Ordinal);

    /// <summary>What the record answers for an id it holds that still runs: accepted.</summary>
    public const string Running = "{\"ok\":true,\"stage\":\"accepted\",\"effect\":\"\"}";

    /// <summary>
    /// Before running <paramref name="id"/> from <paramref name="connector"/> (<c>t:id</c>): the answer when it holds the id — the
    /// receipt it had with <c>"repeat":true</c> appended, or <see cref="Running"/>'s while it still runs — or null, and then the id is
    /// held as running until <see cref="Keep"/>.
    /// </summary>
    public string? Begin(string connector, string id, long now)
    {
        var k = Of(connector, now);
        if (k.ById.TryGetValue(id, out var held))
            return Repeat(held.Receipt ?? Running);
        k.ById[id] = (now, null);
        k.Order.Enqueue(id);
        while (k.Order.Count > PeerLink.ReceiptsKept) k.ById.Remove(k.Order.Dequeue());
        return null;
    }

    /// <summary>The final receipt of <paramref name="id"/>, kept for its repeats and for <c>RECEIPTS</c>.</summary>
    public void Keep(string connector, string id, string receipt, long now)
    {
        var k = Of(connector, now);
        if (!k.ById.ContainsKey(id)) k.Order.Enqueue(id);
        k.ById[id] = (now, receipt);
        while (k.Order.Count > PeerLink.ReceiptsKept) k.ById.Remove(k.Order.Dequeue());
    }

    /// <summary>
    /// 13.12.6: a <c>RECEIPTS</c> answer for the connector's own ids (at most <see cref="PeerLink.ReceiptsAskMax"/>), each its final
    /// receipt, <see cref="Running"/>'s while it still runs, or <c>"unknown"</c> when the record does not hold it, with this
    /// acceptor's inst.
    /// </summary>
    public string Answer(string connector, IEnumerable<string> ids, string inst, long now)
    {
        var k = Of(connector, now);
        var s = new StringBuilder("{\"inst\":\"").Append(JsonEncodedText.Encode(inst)).Append("\",\"receipts\":{");
        var first = true;
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(PeerLink.ReceiptsAskMax))
        {
            if (!first) s.Append(',');
            first = false;
            s.Append('"').Append(JsonEncodedText.Encode(id)).Append("\":");
            s.Append(k.ById.TryGetValue(id, out var held) ? held.Receipt ?? Running : "\"unknown\"");
        }
        return s.Append("}}").ToString();
    }

    /// <summary>A receipt answered from the record: <c>"repeat":true</c> appended at its end.</summary>
    public static string Repeat(string receipt)
    {
        var end = receipt.LastIndexOf('}');
        if (end < 0) return receipt;
        var open = receipt.AsSpan(0, end).TrimEnd();
        return open.ToString() + (open.Length > 0 && open[^1] == '{' ? "" : ",") + "\"repeat\":true" + receipt[end..];
    }

    Kept Of(string connector, long now)
    {
        if (!_by.TryGetValue(connector, out var k)) _by[connector] = k = new Kept();
        while (k.Order.TryPeek(out var oldest) && (!k.ById.TryGetValue(oldest, out var held) || now - held.At > PeerLink.ReceiptsKeptMs))
        {
            k.Order.Dequeue();
            k.ById.Remove(oldest);
        }
        return k;
    }
}

/// <summary>
/// One injected now: the monotonic and the wall clock in ms. A test's single clock stands for both (13.20's machine rows), so a
/// number converts to a now whose two clocks agree.
/// </summary>
public readonly record struct PeerNow(long Mono, long Wall)
{
    public static implicit operator PeerNow(long ms) => new(ms, ms);
}

/// <summary>How a certificate came to be pinned (13.8.2): seen first on a public view, by a proof that verified over it, or by a person.</summary>
public enum PinTrust
{
    First,
    Proof,
    Person,
}

/// <summary>
/// A pin: the node it was pinned for (null until a verified link names it), its certificate's fingerprint, 64 lowercase hex, and how
/// it was pinned. A pin always names its certificate: one with no fingerprint would trust any.
/// </summary>
public sealed record PeerPin(string? NodeId, string Fingerprint, PinTrust Trust);

/// <summary>13.9.3: what a verified link to a node reached — TLS, its level, its proof — which a later link may not offer less than.</summary>
public sealed record PeerFloor(bool Tls, int Level, string Proof);

/// <summary>
/// What a connector keeps across a restart (13.15.3, 13.9.3, 13.8, 13.4.3): the pin, the floor, a hold with its end in wall time, and
/// the node id and kind the record is bound to once a verified link named them or the operator accepted a new id. Never a key, code,
/// K, S, proof or nonce.
/// </summary>
public sealed record PeerKept(PeerPin? Pin, PeerFloor? Floor, long? HeldUntilWall, string? HeldRefusal, string? NodeId = null, string? NodeKind = null);

/// <summary>What a connector is set to: its link and what it holds for the peer — the kid, never the key.</summary>
public sealed record PeerLinkSettings
{
    public PeerCarrier Carrier { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>The peer's name, its kind and its id as this desk knows them, for words and for the hello it listens for.</summary>
    public string Name { get; init; } = "the peer";
    public string? Kind { get; init; }
    public string? NodeId { get; init; }
    /// <summary>A typed address to dial; null until a hello names the node.</summary>
    public string? Address { get; init; }
    /// <summary>The kid, or <c>code</c>, of what this connector holds for the node; null when it holds none.</summary>
    public string? Key { get; init; }
    /// <summary>The dialled address is a loopback address (13.1's local).</summary>
    public bool Local { get; init; }
    /// <summary>The show needs this link (13.1): a followed link, a playout link, a grant's. Only then may its light be red.</summary>
    public bool ShowNeeds { get; init; }
    /// <summary>The scope the link is used for (<c>follow</c>, <c>post</c>, <c>drive</c>, <c>operate</c>); a verdict's lower scope is flagged.</summary>
    public string? Uses { get; init; }
    /// <summary>The tolerance of the timed commands this link schedules; null when it schedules none.</summary>
    public long? TimedToleranceMs { get; init; }
    /// <summary>The features the last verdict listed; a verified link's verdict replaces them.</summary>
    public IReadOnlyList<string> Can { get; init; } = [];
    /// <summary>The request id's prefix and this process's hello inst (13.12.1).</summary>
    public string IdPrefix { get; init; } = "s";
    public string Inst { get; init; } = "00000000";
}

/// <summary>A connector's carrier (13.1): a line wire, or a node link on Stagewatch's <c>/ws</c>.</summary>
public enum PeerCarrier
{
    Line,
    Ws,
}

/// <summary>
/// What the connector puts on the wire, in order: a line's verb or a message's <c>type</c> or <c>cmd</c> (pings left out), and the
/// credential it carries — <c>proof</c>, <c>auth-proof</c>, <c>bearer</c> or <c>session</c> — never the credential itself.
/// </summary>
public readonly record struct PeerWrite(long At, string? Verb, string? Credential, string? Detail = null);

/// <summary>What one run of the evaluator did (13.10.3–4): pinged every link after its own gap, or closed a link gone down.</summary>
public readonly record struct PeerEvaluation(bool PingedAll, bool Closed, long? GapMs);

/// <summary>13.16.3's counts.</summary>
public readonly record struct PeerCounts(int Unconfirmed, int Late, int Replayed, int Oversize, int Malformed, int Unmatched);

/// <summary>
/// 13.16.3, the link record every reader takes: no reader derives a link fact of its own. It never carries a key, code, K, S,
/// proof or nonce: the connector it comes from never holds one.
/// </summary>
public sealed record PeerLinkRecord(
    string? PeerT, string? PeerId, string PeerName, string Carrier, int Level, string Tls, string Pin, string Proof, string Kid, string Scope,
    string State, string Refusal, string Show, IReadOnlyList<string> Flags, string Light, string Mark, string Words, long Since,
    long? HeardMs, int Attempt, long? NextMs, double? OffsetMs, long? ErrMs, long? AgeMs, int Samples, bool Apart, PeerCounts Counts,
    PeerFloor? Floor)
{
    static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>13.16.3's shape, its fields in its order.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartObject();
            w.WriteStartObject("peer");
            Str(w, "t", PeerT);
            Str(w, "id", PeerId);
            w.WriteString("name", PeerName);
            w.WriteEndObject();
            w.WriteString("carrier", Carrier);
            w.WriteNumber("level", Level);
            w.WriteString("tls", Tls);
            w.WriteString("pin", Pin);
            w.WriteString("proof", Proof);
            w.WriteString("kid", Kid);
            w.WriteString("scope", Scope);
            w.WriteString("state", State);
            w.WriteString("refusal", Refusal);
            w.WriteString("show", Show);
            w.WriteStartArray("flags");
            foreach (var f in Flags) w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteString("light", Light);
            w.WriteString("mark", Mark);
            w.WriteString("words", Words);
            w.WriteNumber("since", Since);
            Num(w, "heardMs", HeardMs);
            w.WriteNumber("attempt", Attempt);
            Num(w, "nextMs", NextMs);
            w.WriteStartObject("clock");
            if (OffsetMs is { } o) w.WriteNumber("offsetMs", o); else w.WriteNull("offsetMs");
            Num(w, "errMs", ErrMs);
            Num(w, "ageMs", AgeMs);
            w.WriteNumber("samples", Samples);
            w.WriteBoolean("apart", Apart);
            w.WriteEndObject();
            w.WriteStartObject("counts");
            w.WriteNumber("unconfirmed", Counts.Unconfirmed);
            w.WriteNumber("late", Counts.Late);
            w.WriteNumber("replayed", Counts.Replayed);
            w.WriteNumber("oversize", Counts.Oversize);
            w.WriteNumber("malformed", Counts.Malformed);
            w.WriteNumber("unmatched", Counts.Unmatched);
            w.WriteEndObject();
            if (Floor is { } f2)
            {
                w.WriteStartObject("floor");
                w.WriteBoolean("tls", f2.Tls);
                w.WriteNumber("level", f2.Level);
                w.WriteString("proof", f2.Proof);
                w.WriteEndObject();
            }
            else w.WriteNull("floor");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void Str(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    static void Num(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v) w.WriteNumber(name, v); else w.WriteNull(name);
    }
}

/// <summary>
/// 13.16's machine: one connector's link to one node, on injected time and j. Each method is one thing that happened, at its
/// time, and times only move forward; what the link is at a time — its state, light, flags, the dial it makes, what it follows,
/// each command's outcome, 13.16.3's record — is read off it at that time, after every deadline that fell due by then. It decides;
/// the carrier dials, reads and writes, drains <see cref="Drain"/> for what to write, and closes its socket once
/// <see cref="KeepsConnection"/> says the connector ended it. It never holds a key, code, K, S, proof or nonce, only the kid of what
/// the carrier proves; the carrier checks the acceptor's proof with its K and says whether it verified, so nothing it publishes can
/// carry a secret and no verdict is taken unchecked. One writer: the link's own lane.
/// </summary>
public sealed class PeerLinkConnector
{
    enum Phase
    {
        /// <summary>No connection: never dialled, or closed and waiting to dial again.</summary>
        Idle,
        /// <summary>A dial is out and nothing has answered it.</summary>
        Dialling,
        /// <summary>A connection is up and nothing is followed from it yet: TLS, LINK, a level-0 pairing.</summary>
        Handshake,
        /// <summary>A link is up: verified, or up with <c>legacy</c>, <c>open</c> or <c>keyless</c>.</summary>
        Up,
    }

    sealed class Command(string id, long writtenAt)
    {
        public readonly string Id = id;
        public readonly long WrittenAt = writtenAt;
        public PeerOutcome? Outcome;
        public string Words = "";
        /// <summary>When it last stopped waiting, in settle order: the oldest settled are the first let go.</summary>
        public long Left;
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly PeerLinkSettings _s;
    readonly Func<double> _j;
    PeerPin? _pin;
    PeerFloor? _floor;
    string? _boundId, _boundT;

    long _now = long.MinValue, _wallOffset;
    Phase _phase;
    long _phaseAt, _attemptAt;
    bool _everClosed, _addressHeard;
    string? _scheme;
    bool _tls, _noTls, _tlsHint, _greeted;
    long _greetAt;
    string? _cert;
    PeerOffer? _offer, _other;
    bool _proofWritten, _nonceProvable, _authProofWritten;

    int _level = -1;
    string _proof = "none";
    bool _verified, _keyless, _legacy, _open, _plain, _codePlain;
    string? _scope;
    HashSet<string> _can;
    long _verifiedAt, _lastHeard, _recoveredAt, _closedAt;
    bool _stableDone = true, _wentDown;

    int _failed;
    long _waitMs;
    long _nextDialAt = long.MinValue;

    long _holdUntil = long.MinValue, _holdUntilWall, _holdAt;
    string? _holdRefusal;
    long _reproveAt = long.MinValue;
    string? _refused;
    long _refusedAt;
    string? _lastClass, _why;

    bool _note;
    (long At, string? Inst, string? Host)? _h1, _h2;
    long _markUntil = long.MinValue, _markAt;

    PeerShow _show;
    string? _peerInst;
    readonly PeerClock _clock = new();
    readonly List<long> _pings = [];

    long? _lastEvaluate;
    long _selfGapUntil = long.MinValue;

    readonly Dictionary<string, Command> _commands = new(StringComparer.Ordinal);
    long _leftSeq, _nextId;
    int _unconfirmed, _late, _replayed, _oversize, _malformed, _unmatched, _oversizeHere, _malformedHere;

    readonly List<PeerWrite> _writes = [];

    /// <summary>
    /// A connector as its settings and what it kept (null for none) make it at <paramref name="now"/>: a hold kept with its end in
    /// wall time holds until that end (13.15.3); a kept pin that names no certificate is no pin. <paramref name="jitter"/> is
    /// 13.15.1's j; null takes the process's.
    /// </summary>
    public PeerLinkConnector(PeerLinkSettings settings, PeerKept? kept, PeerNow now, Func<double>? jitter = null)
    {
        _s = settings;
        _j = jitter ?? PeerLink.ProcessJitter;
        _can = new HashSet<string>(settings.Can, StringComparer.Ordinal);
        _pin = kept?.Pin is { } pin && PeerLink.IsHex(pin.Fingerprint, 64) ? pin : null;
        _floor = kept?.Floor;
        _boundId = kept?.NodeId;
        _boundT = kept?.NodeKind;
        _now = now.Mono;
        _wallOffset = now.Wall - now.Mono;
        if (kept is { HeldUntilWall: { } until } && until > now.Wall)
        {
            _holdUntilWall = until;
            _holdUntil = now.Mono + (until - now.Wall);
            _holdRefusal = kept.HeldRefusal ?? "key";
            _holdAt = now.Mono;
        }
    }

    /// <summary>What this connector keeps across a restart; persisted whenever it changes, before any further I/O (13.15.3).</summary>
    public PeerKept Kept => new(_pin, _floor, _holdUntil > _now ? _holdUntilWall : null, _holdUntil > _now ? _holdRefusal : null, _boundId, _boundT);

    /// <summary>The node id this link is for: the one a verified link or the operator bound it to, else the settings', else the pin's.</summary>
    public string? NodeId => _boundId ?? _s.NodeId ?? _pin?.NodeId;

    /// <summary>The node kind this link is for: the one a verified link or the operator bound it to, else the settings'.</summary>
    public string? NodeKind => _boundT ?? _s.Kind;

    // ------------------------------------------------------------------ what happened

    /// <summary>
    /// The connector dials now, when its schedule lets it (<see cref="DialDue"/>): the scheme it dials, or null when a hold, a
    /// refusal, a conflict or its wait stops it. A dial with no connection within <see cref="PeerLink.ConnectTimeoutMs"/> is a failed dial.
    /// </summary>
    public string? Dial(PeerNow now)
    {
        Advance(now);
        if (_phase != Phase.Idle || DialDue(now) is not { } scheme) return null;
        _phase = Phase.Dialling;
        _phaseAt = _attemptAt = now.Mono;
        _scheme = scheme;
        return scheme;
    }

    /// <summary>A connection is up on the dial's scheme, nothing verified on it. On <c>line</c> the connector greets and asks LINK.</summary>
    public void Connected(PeerNow now)
    {
        Advance(now);
        UpOnDial(now, greet: true);
    }

    /// <summary>
    /// The dial's TLS found no TLS on offer. Where a pin or a floor says TLS was there it is refused (13.9.3) with nothing sent;
    /// otherwise a <c>line</c> link goes on plain (13.3.3), a local <c>ws</c> link goes on plain for LINK to prove (13.3.4), and a
    /// <c>ws</c> link to another machine reads the public view keyless, its key kept on this machine (13.3.2).
    /// </summary>
    public void NoTls(PeerNow now)
    {
        Advance(now);
        if (_phase is Phase.Handshake or Phase.Up) return;
        _noTls = true;
        if (_floor is { Tls: true } || _pin is { Trust: not PinTrust.First })
        {
            Refuse(now, "floor");
            return;
        }
        if (_s.Carrier == PeerCarrier.Line)
        {
            Up(now, tls: false, greet: true);
            return;
        }
        Up(now, tls: false, greet: false);
        if (_s.Local) return;
        Linked(now);
        _keyless = true;
    }

    /// <summary>
    /// The connection's TLS showed this certificate (64 hex). A pinned node showing another is CERTIFICATE CHANGED: nothing of the
    /// connector's is sent (13.8.3). On <c>line</c> the connector then greets and asks LINK.
    /// </summary>
    public void Certificate(PeerNow now, string fingerprint)
    {
        Advance(now);
        var up = _phase is not (Phase.Idle or Phase.Dialling) || Up(now, tls: true, greet: false);
        _cert = fingerprint;
        if (!up || _refused is not null) return;
        if (_pin is { Trust: not PinTrust.First } pinned && pinned.Fingerprint != fingerprint)
        {
            Refuse(now, "certificate");
            return;
        }
        if (_pin is { Trust: PinTrust.First } first && first.Fingerprint != fingerprint) _note = true;
        if (_s.Carrier == PeerCarrier.Line && _phase == Phase.Handshake) Greet(now);
    }

    /// <summary>
    /// The line that answered LINK (13.6.4). <c>OK @l LINK …</c> is the acceptor's offer: the connector proves once, and only to the
    /// id it meant over the certificate it was shown. <c>OK @l paired {…}</c> is the verdict: verified only after this connection's
    /// proof was written and <paramref name="acceptorProofVerifies"/> — the carrier's check of the verdict's <c>proof</c> with K over
    /// T — says the acceptor's proof verified; otherwise the connection ends with nothing taken, pinned or floored. At level 0 it is
    /// 1.1's pairing. <c>ERR @l LINK open</c> is an open acceptor (13.6.7); <c>ERR @l LINK &lt;class&gt;</c> a refusal; any other
    /// <c>ERR</c> to LINK an acceptor that does not speak it (13.6.8).
    /// </summary>
    public void LineAnswer(PeerNow now, string line, Func<string, bool>? acceptorProofVerifies = null)
    {
        Advance(now);
        if (_phase is Phase.Idle or Phase.Dialling && !UpOnDial(now, greet: true)) return;
        if (_refused is not null || _phase == Phase.Idle) return;
        var ok = line.StartsWith("OK ", StringComparison.Ordinal);
        if (!ok && !line.StartsWith("ERR ", StringComparison.Ordinal))
        {
            Malformed(now);
            return;
        }
        var rest = line[(ok ? 3 : 4)..];
        if (rest.StartsWith('@')) rest = rest.IndexOf(' ') is var sp and > 0 ? rest[(sp + 1)..] : "";
        if (ok && rest.StartsWith("LINK ", StringComparison.Ordinal))
        {
            Offered(now, PeerLink.ParseLinkAnswer(line));
            return;
        }
        if (ok && (rest == "paired" || rest.StartsWith("paired ", StringComparison.Ordinal)))
        {
            if (_legacy && !_proofWritten)
            {
                // Level 0: 1.1's pairing. Nothing proves the acceptor (13.6.8), and the link says so: legacy, amber.
                Linked(now);
                return;
            }
            TakeVerdict(now, rest.IndexOf('{') is var brace and >= 0 ? Parse(rest[brace..]) : null, acceptorProofVerifies);
            return;
        }
        if (ok)
        {
            Malformed(now);
            return;
        }
        if (rest.StartsWith("LINK ", StringComparison.Ordinal))
        {
            var cls = rest[5..].Split(' ')[0];
            if (cls == "open") Open(now);
            else TakeRefusal(now, cls, 0);
            return;
        }
        // An acceptor that answered LINK and then errs as one that does not speak it is read as neither.
        if (_offer is not null || _proofWritten) EndMalformed(now);
        else LevelZero(now);
    }

    /// <summary>
    /// On <c>ws</c>, the acceptor's first message is <c>link</c> (13.6.5), read by <see cref="PeerLink.ParseLinkOffer"/>; null when it
    /// could not be read. With a key the connector proves once, to the id it meant over the certificate it was shown; with none it
    /// reads the public view keyless.
    /// </summary>
    public void LinkOffered(PeerNow now, PeerOffer? offer)
    {
        Advance(now);
        if (_phase is Phase.Idle or Phase.Dialling && !UpOnDial(now, greet: false)) return;
        if (_refused is not null || _phase == Phase.Idle) return;
        Offered(now, offer);
    }

    /// <summary>
    /// On <c>ws</c>, the verdict (13.6.5): <c>{"type":"auth","ok":true,…,"proof",…}</c> is taken as <see cref="LineAnswer"/> takes
    /// <c>OK paired</c> — verified only after this connection's proof was written and the acceptor's proof verified by
    /// <paramref name="acceptorProofVerifies"/>; <c>"ok":false</c> refuses the key by its <c>class</c> and <c>retry</c>.
    /// </summary>
    public void Verdict(PeerNow now, JsonElement verdict, Func<string, bool>? acceptorProofVerifies = null)
    {
        Advance(now);
        if (_refused is not null || _phase is not (Phase.Handshake or Phase.Up)) return;
        if (PeerLink.Text(verdict, "type") != "auth" || !verdict.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            Malformed(now);
            return;
        }
        if (ok.ValueKind == JsonValueKind.True)
        {
            TakeVerdict(now, verdict, acceptorProofVerifies);
            return;
        }
        var retry = verdict.TryGetProperty("retry", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var seconds) ? Math.Max(0, seconds) : 0;
        TakeRefusal(now, PeerLink.Text(verdict, "class") ?? "refused", retry);
    }

    /// <summary>
    /// On <c>ws</c>, the acceptor's first message is a state frame, not <c>link</c>: a level-0 acceptor (13.6.8). After a <c>link</c>
    /// message it is a frame before the verdict: discarded and counted.
    /// </summary>
    public void FirstFrame(PeerNow now)
    {
        Advance(now);
        if (_phase is Phase.Idle or Phase.Dialling) UpOnDial(now, greet: false);
        if (_refused is not null) return;
        if (_offer is null && !_proofWritten) LevelZero(now);
        if (_refused is null) Frame(now);
    }

    /// <summary>
    /// A line acceptor's NONCE (13.6.8): a LINK connector keeps only whether it was exactly 32 lowercase hex, and answers it with
    /// 1.1's proof of a code only once the acceptor turns out not to speak LINK — one proof a connection, and nothing for any other
    /// NONCE.
    /// </summary>
    public void Nonce(PeerNow now, string nonce)
    {
        Advance(now);
        if (_phase != Phase.Handshake) return;
        if (!_legacy)
        {
            _nonceProvable = PeerLink.IsProvableNonce(nonce);
            return;
        }
        if (PeerLink.IsProvableNonce(nonce)) ProveLegacy(now);
    }

    /// <summary>
    /// LINK completed: both proofs checked (13.6.3) — by the carrier, or by <see cref="LineAnswer"/> and <see cref="Verdict"/> with
    /// the carrier's check. The record binds to the id and kind the acceptor answered LINK with (13.4.3); the certificate it ran
    /// over is pinned by that proof and the floor is recorded (13.9.3); a new inst empties the clock (13.11.7); written commands it
    /// left unconfirmed are asked for when the acceptor keeps receipts (13.12.6).
    /// </summary>
    public void Verified(PeerNow now, string? inst = null, string? scope = null, IReadOnlyList<string>? can = null)
    {
        Advance(now);
        if (_refused is not null) return;
        if (_phase is Phase.Idle or Phase.Dialling && !UpOnDial(now, greet: false)) return;
        Linked(now);
        _verified = true;
        _level = 1;
        _proof = "mutual";
        _keyless = _legacy = _open = false;
        _verifiedAt = now.Mono;
        _stableDone = false;
        _wentDown = false;
        _codePlain = _plain && _s.Key == "code";
        _lastClass = _why = null;
        _holdUntil = _reproveAt = long.MinValue;
        if (_offer is { } offer)
        {
            _boundId = offer.Id;
            _boundT = offer.T;
        }
        if (_tls && _cert is { } cert) _pin = new PeerPin(NodeId, cert, PinTrust.Proof);
        _floor = new PeerFloor(_tls || _floor is { Tls: true }, 1, "mutual");
        if (inst is not null)
        {
            if (_peerInst is not null && inst != _peerInst) _clock.Clear();
            _peerInst = inst;
        }
        if (scope is not null) _scope = scope;
        if (can is not null) _can = new HashSet<string>(can.Take(PeerLink.CanWords), StringComparer.Ordinal);
        if (!_can.Contains("receipts")) return;
        var ask = _commands.Values.Where(c => c.Outcome == PeerOutcome.Unconfirmed).OrderByDescending(c => c.WrittenAt)
            .Take(PeerLink.ReceiptsAskMax).Select(c => c.Id).ToList();
        if (ask.Count > 0) Write(now, _s.Carrier == PeerCarrier.Line ? "RECEIPTS" : "receipts", null, string.Join(' ', ask));
    }

    /// <summary>
    /// A LINK verdict refused the key (13.6.4): a refused, bound, locked-out or out-of-scope key is held for the longer of
    /// <see cref="PeerLink.HoldRefusedMs"/> and the acceptor's retry (13.15.3); <c>busy</c> waits its retry; <c>malformed</c> and
    /// <c>version</c> wait 13.15.1's. A refusal ends a <c>line</c> connection; a <c>ws</c> socket stays a public viewer, and the key
    /// is proved again on a fresh socket when its hold or wait ends.
    /// </summary>
    public void Refused(PeerNow now, string cls, int retrySeconds)
    {
        Advance(now);
        TakeRefusal(now, cls, retrySeconds);
    }

    /// <summary>The connection ended (or a dial failed): written commands become unconfirmed and the next dial waits 13.15.1's wait.</summary>
    public void Closed(PeerNow now)
    {
        Advance(now);
        if (_phase == Phase.Idle) return;
        _why = null;
        Close(now, failed: true);
    }

    /// <summary>A complete message from the acceptor, and nothing more (13.10.2).</summary>
    public void Heard(PeerNow now) => Hear(now);

    /// <summary>A state frame. On <c>ws</c> it, and only it, makes the show live (13.10.5).</summary>
    public void Frame(PeerNow now)
    {
        if (Hear(now) && _s.Carrier == PeerCarrier.Ws) _show = PeerShow.Live;
    }

    /// <summary>A <c>ws</c> beat; <c>"live":false</c> makes the show stuck, and a plain beat never clears it (13.10.5).</summary>
    public void Beat(PeerNow now, bool live)
    {
        if (Hear(now) && _s.Carrier == PeerCarrier.Ws && !live) _show = PeerShow.Stuck;
    }

    /// <summary>The connector wrote a ping with this t1 (its wall clock); the last <see cref="PeerLink.PingsMatched"/> are matched.</summary>
    public void Ping(PeerNow now, long t1)
    {
        Advance(now);
        _pings.Add(t1);
        if (_pings.Count > PeerLink.PingsMatched) _pings.RemoveAt(0);
    }

    /// <summary>
    /// A pong: its t1, t2 and t3 when given, else it answers the last ping. Only a pong that answers one of the last pings is a
    /// clock sample (13.10.2), t4 being now on the wall clock; an unmatched one is counted. On a <c>line</c> link with feature
    /// <c>stuck</c> its fifth word sets the show; on <c>ws</c> a pong never clears a stuck show.
    /// </summary>
    public void Pong(PeerNow now, long? t1, long? t2, long? t3, bool stuck)
    {
        if (!Hear(now)) return;
        long? answered = t1 ?? (_pings.Count > 0 ? _pings[^1] : null);
        if (answered is { } m && _pings.Remove(m))
        {
            if (t2 is { } a && t3 is { } b) _clock.Add(m, a, b, now.Wall);
        }
        else _unmatched++;
        if (_s.Carrier == PeerCarrier.Line && _can.Contains("stuck")) _show = stuck ? PeerShow.Stuck : PeerShow.Live;
    }

    /// <summary>
    /// One run of the evaluator (13.10.3–4). After a gap of more than <see cref="PeerLink.SelfGapMs"/> since its last run — this node
    /// paused — it pings every link and moves none to down until <see cref="PeerLink.SelfGapMs"/> later; otherwise a link silent
    /// past <see cref="PeerLink.DropMs"/> is closed and redialled.
    /// </summary>
    public PeerEvaluation Evaluate(PeerNow now)
    {
        Advance(now);
        long? gap = _lastEvaluate is { } last ? now.Mono - last : null;
        _lastEvaluate = now.Mono;
        if (gap > PeerLink.SelfGapMs)
        {
            _selfGapUntil = now.Mono + PeerLink.SelfGapMs;
            PingedAt = now.Mono;
            return new PeerEvaluation(true, false, gap);
        }
        if (now.Mono < _selfGapUntil || _phase != Phase.Up || PeerLink.Liveness(now.Mono - _lastHeard) != PeerLinkState.Down)
            return new PeerEvaluation(false, false, gap);
        _why = null;
        Close(now, failed: true);
        return new PeerEvaluation(false, true, gap);
    }

    /// <summary>The time the evaluator last pinged every link after its own gap; null when it never has.</summary>
    public long? PingedAt { get; private set; }

    /// <summary>
    /// A hello heard (§2) for this link's node. It may raise the guard — try TLS first — and never lowers it: for a pinned id the
    /// pin alone decides, and a hello that contradicts it is a note (13.4.2). An inst heard A, B, A within
    /// <see cref="PeerLink.PeerStaleMs"/> each (or two hosts from a node that sends no inst) marks the id in conflict until
    /// <see cref="PeerLink.ConflictClearMs"/> pass without the alternation.
    /// </summary>
    public void Hello(PeerNow now, string id, string? tls, string? inst = null, string? host = null)
    {
        Advance(now);
        if (NodeId is { } mine && mine != id) return;
        _addressHeard = true;
        if (_pin is { Trust: not PinTrust.First } p) _note = tls != "sha256:" + p.Fingerprint;
        else if (tls is not null) _tlsHint = true;
        var alternates = inst is not null
            ? _h1 is { } a && _h2 is { } b && now.Mono - a.At <= PeerLink.PeerStaleMs && a.At - b.At <= PeerLink.PeerStaleMs && inst == b.Inst && inst != a.Inst
            : host is not null && _h1 is { Inst: null, Host: { } h } c && h != host && now.Mono - c.At <= PeerLink.PeerStaleMs;
        if (alternates)
        {
            _markAt = now.Mono;
            _markUntil = now.Mono + PeerLink.ConflictClearMs;
        }
        _h2 = _h1;
        _h1 = (now.Mono, inst, host);
    }

    /// <summary>
    /// A command asked for, under <paramref name="id"/>: written when the link is live or stale, its show is not stuck and fewer than
    /// <see cref="PeerLink.InflightMax"/> wait (null); otherwise not-sent, with why (13.12.3, 13.12.8). Nothing is queued.
    /// </summary>
    public PeerOutcome? Send(PeerNow now, string id, string verb = "command")
    {
        Advance(now);
        var state = State(now);
        string? why = null;
        if (_phase != Phase.Up || state is not (PeerLinkState.Live or PeerLinkState.Stale))
            why = _s.Name + " is " + state.Word() + " — not sent; nothing is queued";
        else if (_keyless && !_open) why = "this link reads " + _s.Name + "'s public view — not sent";
        else if (_show == PeerShow.Stuck) why = _s.Name + " says its show is stuck — not sent";
        else if (InFlight >= PeerLink.InflightMax) why = PeerLink.InflightMax.ToString(Inv) + " commands are waiting on " + _s.Name + "'s answers";
        if (why is not null)
        {
            var c = new Command(id, now.Mono);
            _commands[id] = c;
            Settle(c, PeerOutcome.NotSent, why);
            return PeerOutcome.NotSent;
        }
        _commands[id] = new Command(id, now.Mono);
        Write(now, verb, null, id);
        return null;
    }

    /// <summary>The command with this id was written: it waits for its answer.</summary>
    public void Written(PeerNow now, string id)
    {
        Advance(now);
        _commands[id] = new Command(id, now.Mono);
    }

    /// <summary>
    /// An answer to one of its commands, read by 13.12.3. An outcome only moves forward: a waiting command takes any; an accepted
    /// one only a final applied or refused; an unconfirmed one only applied or refused (13.12.2).
    /// </summary>
    public void Answer(PeerNow now, string id, PeerReading reading)
    {
        Advance(now);
        if (!_commands.TryGetValue(id, out var c)) return;
        if (reading.Repeat) _replayed++;
        if (reading.LateMs is not null) _late++;
        Forward(c, reading);
    }

    /// <summary>A <c>RECEIPTS</c> answer (13.12.6): each id's receipt, or null for <c>unknown</c>, which leaves it unconfirmed.</summary>
    public void Receipts(PeerNow now, IReadOnlyDictionary<string, PeerReading?> receipts)
    {
        Advance(now);
        foreach (var (id, r) in receipts)
            if (r is { } reading && _commands.TryGetValue(id, out var c) && c.Outcome == PeerOutcome.Unconfirmed) Forward(c, reading);
    }

    /// <summary>
    /// A side call's answer (13.7.3). A 401 that asks for a relink ends S: the link closes and relinks on 13.15.1's wait, with no
    /// hold. A 401 without it holds the key, and the socket stays a public viewer until the hold ends. Anything else is the side
    /// call's own, and never moves the link.
    /// </summary>
    public void SideCall(PeerNow now, PeerReading reading)
    {
        Advance(now);
        if (reading.Relink)
        {
            if (_phase == Phase.Idle) return;
            _why = null;
            Close(now, failed: true);
            return;
        }
        if (!reading.Hold) return;
        Hold(now, "key", Math.Max(PeerLink.HoldRefusedMs, reading.RetryAfter * 1000L));
        if (_phase != Phase.Up) return;
        _verified = false;
        _keyless = true;
        _reproveAt = _holdUntil;
    }

    /// <summary>A message longer than the connector reads whole (13.14): counted and flagged, the link stays up.</summary>
    public void Oversize(PeerNow now)
    {
        Advance(now);
        _oversize++;
        _oversizeHere++;
    }

    /// <summary>A message the connector could not read: counted.</summary>
    public void Malformed(PeerNow now)
    {
        Advance(now);
        _malformed++;
        _malformedHere++;
    }

    /// <summary>This node's own wall clock stepped: the clock's window is emptied (13.11.7).</summary>
    public void WallStepped(PeerNow now)
    {
        Advance(now);
        _clock.Clear();
    }

    /// <summary>
    /// The operator's <i>Link again now</i>: the hold ends and the connector dials at once (13.15.3); a <c>ws</c> socket kept as a
    /// public viewer after its key was refused ends, so the key is proved again on a fresh one.
    /// </summary>
    public void LinkAgainNow(PeerNow now)
    {
        Advance(now);
        _holdUntil = long.MinValue;
        _nextDialAt = now.Mono;
        if (_phase == Phase.Up && _keyless && _reproveAt != long.MinValue) Relink(now);
    }

    /// <summary>The operator's <i>Trust certificate</i> on a <c>first</c> pin: pinned by a person (13.6.8).</summary>
    public void TrustCertificate()
    {
        if (_pin is { Trust: PinTrust.First } p) _pin = p with { Trust = PinTrust.Person };
    }

    /// <summary>
    /// The operator's <i>Accept certificate</i> after CERTIFICATE CHANGED, typing at least 8 hex of the certificate shown now (13.8.3),
    /// as the desk shows it or not — spaces dropped, either case: false, and nothing changed, unless it starts that certificate.
    /// </summary>
    public bool AcceptCertificate(string typedHex)
    {
        var hex = new string(typedHex.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());
        if (_refused != "certificate" || _cert is null || hex.Length < 8 || !PeerLink.IsHex(hex, hex.Length) || !_cert.StartsWith(hex, StringComparison.Ordinal))
            return false;
        _pin = new PeerPin(NodeId, _cert, PinTrust.Person);
        _refused = null;
        return true;
    }

    /// <summary>
    /// The operator's <i>Accept new id</i> (13.4.3, 13.16.6): the record is bound to the id and kind that answered LINK in its place,
    /// with no pin and no floor of the old node's; false, and nothing changed, unless the link is refused as another id.
    /// </summary>
    public bool AcceptNewId()
    {
        if (_refused != "other-id" || _other is not { } other) return false;
        _boundId = other.Id;
        _boundT = other.T;
        _pin = null;
        _floor = null;
        _note = false;
        _other = null;
        _refused = null;
        return true;
    }

    /// <summary>The operator's <i>Lower floor</i>, one press (13.9.3): the floor is gone, and a floor refusal with it.</summary>
    public void LowerFloor()
    {
        _floor = null;
        if (_refused == "floor") _refused = null;
    }

    // ------------------------------------------------------------------ what it is at a time

    /// <summary>13.16.1's state at <paramref name="now"/>.</summary>
    public PeerLinkState State(PeerNow now)
    {
        Advance(now);
        if (!_s.Enabled) return PeerLinkState.Off;
        if (_refused is not null) return PeerLinkState.Refused;
        if (_holdUntil > now.Mono) return PeerLinkState.Held;
        if (_markUntil > now.Mono && _phase is Phase.Idle or Phase.Dialling) return PeerLinkState.Held;
        return _phase switch
        {
            Phase.Idle when !AddressKnown => PeerLinkState.Finding,
            Phase.Idle => _everClosed ? PeerLinkState.Down : PeerLinkState.Connecting,
            Phase.Dialling or Phase.Handshake => PeerLinkState.Connecting,
            _ => Liveness(now.Mono),
        };
    }

    /// <summary>The show's liveness the link carries (13.10.5).</summary>
    public PeerShow Show => _phase == Phase.Up ? _show : PeerShow.Unknown;

    /// <summary>
    /// The dial the connector makes at <paramref name="now"/>: the dial out, or one its schedule lets it make now — no hold, no
    /// refusal, no conflict standing, its wait over (a wait or hold is over at its end); null when it makes none.
    /// </summary>
    public string? DialDue(PeerNow now)
    {
        Advance(now);
        if (_phase == Phase.Dialling) return _scheme;
        if (_phase != Phase.Idle || !_s.Enabled || _refused is not null || !AddressKnown) return null;
        if (_holdUntil > now.Mono || _markUntil > now.Mono || _nextDialAt > now.Mono) return null;
        return Scheme();
    }

    /// <summary>
    /// Whether the connector keeps a dial or a connection open at <paramref name="now"/>: false once it ended one — a refusal, a proof
    /// that did not verify, a deadline, a link gone down, a key proved again on a fresh socket — and the carrier closes its socket.
    /// </summary>
    public bool KeepsConnection(PeerNow now)
    {
        Advance(now);
        return _phase != Phase.Idle;
    }

    /// <summary>The wait 13.15.1 gave the last redial, in ms.</summary>
    public long WaitMs => _waitMs;

    /// <summary>Failed dials since the link was last stable (13.15.1's n).</summary>
    public int Attempt => _failed;

    /// <summary>The wall time a key's hold ends; null with none.</summary>
    public long? HeldUntilWall(PeerNow now) => _holdUntil > now.Mono ? _holdUntilWall : null;

    /// <summary>Whether the connector takes the link's facts — state, context — now: a link up and not refused.</summary>
    public bool Follows(PeerNow now) => State(now) != PeerLinkState.Refused && _phase == Phase.Up;

    /// <summary>How many commands wait for answers (13.12.8).</summary>
    public int InFlight => _commands.Values.Count(InFlightNow);

    /// <summary>A command's outcome by its id; null while it waits for its first answer or is not known here.</summary>
    public PeerOutcome? OutcomeOf(string id) => _commands.TryGetValue(id, out var c) ? c.Outcome : null;

    /// <summary>Whether the connector still knows the command with this id: every command in flight, and the last settled ones.</summary>
    public bool Knows(string id) => _commands.ContainsKey(id);

    /// <summary>A command's words: why it was not sent, what its answer said.</summary>
    public string WordsOf(string id) => _commands.TryGetValue(id, out var c) ? c.Words : "";

    /// <summary>
    /// 13.11.6: a timed command may use the offset only when it is fit for <paramref name="toleranceMs"/> and was measured over TLS
    /// or a local link; a clock measured over a plain link off this machine never schedules.
    /// </summary>
    public bool CanSchedule(PeerNow now, long toleranceMs)
        => _phase == Phase.Up && (_tls || _s.Local) && _clock.FitFor(toleranceMs, now.Wall);

    /// <summary>The next request id, <c>&lt;prefix&gt;&lt;n&gt;-&lt;inst&gt;</c> (13.12.1).</summary>
    public string NextId() => PeerLink.RequestId(_s.IdPrefix, ++_nextId, _s.Inst);

    /// <summary>What to write since the last drain, in order.</summary>
    public IReadOnlyList<PeerWrite> Drain()
    {
        var w = _writes.ToList();
        _writes.Clear();
        return w;
    }

    /// <summary>13.16.2's flags at <paramref name="now"/>, in its order.</summary>
    public IReadOnlyList<string> Flags(PeerNow now)
    {
        Advance(now);
        var up = _phase == Phase.Up;
        var f = new List<string>();
        if (up && _plain) f.Add("plain");
        if (up && _keyless) f.Add("keyless");
        if (_legacy && _phase is Phase.Handshake or Phase.Up) f.Add("legacy");
        if (up && _open) f.Add("open");
        if (up && _codePlain) f.Add("code-plain");
        if (up && _verified && ScopeBelow) f.Add("scope");
        if (Tls() == "first") f.Add("first");
        if (_note) f.Add("note");
        if (_clock.Apart) f.Add("apart");
        if (up && _s.TimedToleranceMs is { } tol && !CanSchedule(now, tol)) f.Add("unfit");
        if (_oversizeHere > 0) f.Add("oversize");
        if (_malformedHere > 0) f.Add("malformed");
        return f;
    }

    /// <summary>
    /// The link's light at <paramref name="now"/>: the worse of its state's and its flags' (13.16.1–2), amber while its show is stuck,
    /// its id is in conflict (§2) or its dial has gone unanswered past <see cref="PeerLink.HandshakeMs"/>, and red only when the show
    /// needs the link — a refused one included.
    /// </summary>
    public PeerLight Light(PeerNow now)
    {
        var state = State(now);
        var light = state switch
        {
            PeerLinkState.Off or PeerLinkState.Finding => PeerLight.Grey,
            PeerLinkState.Connecting => _phase != Phase.Idle && now.Mono - _attemptAt > PeerLink.HandshakeMs ? PeerLight.Amber : PeerLight.Grey,
            PeerLinkState.Live => PeerLight.Green,
            PeerLinkState.Stale => PeerLight.Amber,
            _ => PeerLight.Red,
        };
        var amber = Flags(now).Any(f => f switch
        {
            "plain" => !_s.Local,
            "keyless" or "open" => _s.Key is not null,
            "legacy" or "code-plain" or "scope" or "apart" or "oversize" => true,
            _ => false,
        }) || Show == PeerShow.Stuck || _markUntil > now.Mono;
        if (amber && light is PeerLight.Grey or PeerLight.Green) light = PeerLight.Amber;
        return light == PeerLight.Red && !_s.ShowNeeds ? PeerLight.Amber : light;
    }

    /// <summary>13.16.3's <c>refusal</c>: a held key's class, <c>conflict</c> while §2's mark holds the link, why it is refused, or a verdict's class.</summary>
    public string Refusal(PeerNow now)
    {
        var state = State(now);
        if (state == PeerLinkState.Refused) return _refused!;
        if (state == PeerLinkState.Held) return _holdUntil > now.Mono ? _holdRefusal! : "conflict";
        return _lastClass ?? "";
    }

    /// <summary>
    /// 13.16.3's <c>tls</c>: <c>local</c>, <c>pinned</c>, <c>first</c> or <c>none</c>. On a connection, pinned or first only while the
    /// certificate it shows is the pin's: a pin names its certificate, and no other is pinned by it.
    /// </summary>
    public string Tls()
    {
        if (_s.Local) return "local";
        if (_phase is Phase.Handshake or Phase.Up)
        {
            if (!_tls) return "none";
            return _pin is { } p && _cert is { } shown && p.Fingerprint == shown ? p.Trust == PinTrust.First ? "first" : "pinned" : "none";
        }
        return _pin is null ? "none" : _pin.Trust == PinTrust.First ? "first" : "pinned";
    }

    /// <summary>The samples, offset and error the link's clock holds.</summary>
    public PeerClock Clock => _clock;

    /// <summary>13.16.3's record at <paramref name="now"/>.</summary>
    public PeerLinkRecord Record(PeerNow now)
    {
        var state = State(now);
        var light = Light(now);
        var belief = _clock.Believed;
        long? next = _phase == Phase.Idle && _refused is null
            ? Math.Max(0, Math.Max(_nextDialAt, Math.Max(_holdUntil, _markUntil)) - now.Mono) : null;
        return new PeerLinkRecord(NodeKind, NodeId, _s.Name, _s.Carrier == PeerCarrier.Line ? "line" : "ws", Math.Max(0, _level), Tls(),
            _pin is { } pin ? pin.Fingerprint[..16] : "", _proof, _s.Key ?? "", _scope ?? "none",
            state.Word(), Refusal(now), Show.Word(), Flags(now), light.Word(), light.Mark(), Words(now, state),
            Since(now, state) + _wallOffset, _phase == Phase.Up ? now.Mono - _lastHeard : null, _failed, next,
            belief?.OffsetMs, belief?.ErrMs, _clock.AgeMs(now.Wall), _clock.Samples, _clock.Apart,
            new PeerCounts(_unconfirmed, _late, _replayed, _oversize, _malformed, _unmatched), _floor);
    }

    // ------------------------------------------------------------------ inside

    bool AddressKnown => _s.Address is not null || _addressHeard;

    bool ScopeBelow => _s.Uses is { } uses && Rank(_scope) < Rank(uses);

    static int Rank(string? scope) => scope switch
    {
        "follow" => 1,
        "post" => 2,
        "drive" => 3,
        "operate" => 4,
        _ => 0,
    };

    /// <summary>A dial's scheme: TLS on <c>ws</c>, and on <c>line</c> unless the acceptor offered none and nothing asks for it.</summary>
    string Scheme()
    {
        if (_s.Carrier == PeerCarrier.Ws) return "tls";
        if (_pin is not null || _floor is { Tls: true } || _tlsHint || !_noTls) return "tls";
        return "plain";
    }

    PeerLinkState Liveness(long now)
    {
        var l = PeerLink.Liveness(now - _lastHeard);
        return l == PeerLinkState.Down && now < _selfGapUntil ? PeerLinkState.Stale : l;
    }

    /// <summary>A command waits while it has no answer, or an accepted one from an acceptor that sends its final answer (feature <c>final</c>).</summary>
    bool InFlightNow(Command c) => c.Outcome is null || (c.Outcome == PeerOutcome.Accepted && _can.Contains("final"));

    /// <summary>A time on the monotonic clock as a now, its wall time by the last offset seen: a deadline acts at its due time.</summary>
    PeerNow At(long mono) => new(mono, mono + _wallOffset);

    /// <summary>
    /// The deadlines that fall due by <paramref name="now"/>, each acting at its own due time: a dial with no connection within
    /// <see cref="PeerLink.ConnectTimeoutMs"/> (13.15.1); a line acceptor that answers LINK nothing within
    /// <see cref="PeerLink.ReceiptDeadlineMs"/>, which does not speak it (13.6.8); a connection that has not finished LINK within
    /// <see cref="PeerLink.HandshakeMs"/> (13.6.3); 13.15.2's stable reset; a public viewer's key proved again once its hold or wait
    /// ends (13.15.3); and 13.12.4's final answer.
    /// </summary>
    void Advance(PeerNow now)
    {
        if (now.Mono < _now) throw new ArgumentOutOfRangeException(nameof(now), "a link's time only moves forward");
        _now = now.Mono;
        _wallOffset = now.Wall - now.Mono;
        if (_phase == Phase.Dialling && now.Mono - _phaseAt > PeerLink.ConnectTimeoutMs)
        {
            _why = "no connection in " + (PeerLink.ConnectTimeoutMs / 1000).ToString(Inv) + " s";
            Close(At(_phaseAt + PeerLink.ConnectTimeoutMs + 1), failed: true);
        }
        if (_phase == Phase.Handshake && _s.Carrier == PeerCarrier.Line && _greeted && _offer is null && !_legacy
            && now.Mono - _greetAt > PeerLink.ReceiptDeadlineMs)
            LevelZero(At(_greetAt + PeerLink.ReceiptDeadlineMs + 1));
        if (_phase == Phase.Handshake && now.Mono - _phaseAt > PeerLink.HandshakeMs)
        {
            _why = "no link in " + (PeerLink.HandshakeMs / 1000).ToString(Inv) + " s";
            Close(At(_phaseAt + PeerLink.HandshakeMs + 1), failed: true);
        }
        if (_phase == Phase.Up && _verified && !_stableDone && now.Mono >= _verifiedAt + PeerLink.StableMs)
        {
            if (!_wentDown && _verifiedAt + PeerLink.StableMs - _lastHeard <= PeerLink.DropMs) _failed = 0;
            _stableDone = true;
        }
        if (_phase == Phase.Up && _keyless && _reproveAt != long.MinValue && now.Mono >= _reproveAt) Relink(At(_reproveAt));
        foreach (var c in _commands.Values.Where(c => InFlightNow(c) && now.Mono - c.WrittenAt > PeerLink.ReceiptFinalMs).ToList())
            Settle(c, PeerOutcome.Unconfirmed, "no answer in " + (PeerLink.ReceiptFinalMs / 1000).ToString(Inv) + " s — it may have run");
    }

    /// <summary>A connection comes up on the dial's scheme, the one its schedule would dial when no dial was seen.</summary>
    bool UpOnDial(PeerNow now, bool greet)
    {
        if (_phase == Phase.Idle) _scheme = Scheme();
        return Up(now, _scheme != "plain", greet);
    }

    /// <summary>A connection comes up, with nothing of the last one's; false when the link is refused and nothing more is done.</summary>
    bool Up(PeerNow now, bool tls, bool greet)
    {
        if (_refused is not null) return false;
        if (_phase is Phase.Handshake or Phase.Up) return true;
        if (_phase == Phase.Idle) _attemptAt = now.Mono;
        _scheme = tls ? "tls" : "plain";
        _phase = Phase.Handshake;
        _phaseAt = now.Mono;
        _tls = tls;
        _plain = !tls && !_s.Local && _s.Carrier == PeerCarrier.Line;
        _cert = null;
        _offer = null;
        _greeted = _proofWritten = _nonceProvable = _authProofWritten = false;
        _verified = _keyless = _legacy = _open = _codePlain = false;
        _level = -1;
        _proof = "none";
        _show = PeerShow.Unknown;
        _lastHeard = now.Mono;
        if (greet && _s.Carrier == PeerCarrier.Line) Greet(now);
        return true;
    }

    /// <summary>On <c>line</c>, once per connection: HELLO, then LINK (13.6.4).</summary>
    void Greet(PeerNow now)
    {
        if (_greeted) return;
        _greeted = true;
        _greetAt = now.Mono;
        Write(now, "HELLO", null);
        Write(now, "LINK", null);
    }

    /// <summary>
    /// The acceptor's answer to LINK. One a connection, in a shape the connector reads, or the connection ends as malformed; a
    /// version it did not offer is refused (by a floor, or as <c>version</c>); another id than the one meant is refused, naming both,
    /// before anything is proved (13.4.3); a certificate other than the one this connection showed means another machine between
    /// us, and nothing is proved. Then the connector proves once (13.6.3), or with no key reads the public view on <c>ws</c>.
    /// </summary>
    void Offered(PeerNow now, PeerOffer? offer)
    {
        if (offer is null || _offer is not null || _proofWritten || _authProofWritten)
        {
            EndMalformed(now);
            return;
        }
        if (offer.Speaks != PeerLink.Version)
        {
            if (_floor is { Level: >= 1 })
            {
                Refuse(now, "floor");
                return;
            }
            _lastClass = "version";
            _why = null;
            Close(now, failed: true);
            return;
        }
        if (NodeId is { } meant && (offer.Id != meant || (NodeKind is { } kind && offer.T != kind)))
        {
            _other = offer;
            Refuse(now, "other-id");
            return;
        }
        _offer = offer;
        _legacy = false;
        _level = -1;
        _proof = "none";
        if (_s.Key is null)
        {
            if (_s.Carrier == PeerCarrier.Ws)
            {
                Linked(now);
                _keyless = true;
                return;
            }
            _why = "it asks for a key this desk does not hold";
            Close(now, failed: true);
            return;
        }
        var shown = _tls ? _cert is { } cert ? "tls:sha256:" + cert : null : "plain";
        if (shown is null || offer.Bind != shown)
        {
            _why = PeerLink.ProofWords;
            Close(now, failed: true);
            return;
        }
        _proofWritten = true;
        Write(now, _s.Carrier == PeerCarrier.Line ? "LINK" : "link", "proof");
    }

    /// <summary>
    /// A verdict that takes the key: one a connection, after this connection's proof was written, with the acceptor's proof checked by
    /// the carrier. Anything less ends the connection as a proof that did not verify: nothing taken, pinned or floored.
    /// </summary>
    void TakeVerdict(PeerNow now, JsonElement? verdict, Func<string, bool>? acceptorProofVerifies)
    {
        if (_verified)
        {
            EndMalformed(now);
            return;
        }
        if (!_proofWritten || verdict is not { } v || PeerLink.Text(v, "proof") is not { } proof || !PeerLink.IsHex(proof, 64)
            || acceptorProofVerifies is null || !acceptorProofVerifies(proof))
        {
            _lastClass = null;
            _why = PeerLink.ProofWords;
            Close(now, failed: true);
            return;
        }
        Verified(now, PeerLink.Text(v, "inst"), PeerLink.Text(v, "scope"),
            v.TryGetProperty("can", out var can) && can.ValueKind == JsonValueKind.Array
                ? can.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []);
    }

    /// <summary>The refusal a LINK verdict carries (see <see cref="Refused"/>).</summary>
    void TakeRefusal(PeerNow now, string cls, int retrySeconds)
    {
        if (cls == "open")
        {
            Open(now);
            return;
        }
        if (_phase is Phase.Idle or Phase.Dialling && !UpOnDial(now, greet: false)) return;
        _why = null;
        var word = cls == "refused" ? "key" : cls;
        var held = true;
        if (cls is "refused" or "binding" or "locked" or "scope") Hold(now, word, Math.Max(PeerLink.HoldRefusedMs, retrySeconds * 1000L));
        else if (cls == "busy" && retrySeconds > 0) Hold(now, word, retrySeconds * 1000L);
        else
        {
            _lastClass = word;
            held = false;
        }
        _verified = false;
        if (_s.Carrier == PeerCarrier.Line)
        {
            Close(now, failed: true);
            return;
        }
        Linked(now);
        _keyless = true;
        _level = 1;
        _proof = "none";
        if (held)
        {
            _reproveAt = _holdUntil;
            return;
        }
        _waitMs = PeerLink.RedialWait(_failed, _j());
        _failed++;
        _reproveAt = now.Mono + _waitMs;
    }

    /// <summary>The link is up for its facts from now; the silence counts from now.</summary>
    void Linked(PeerNow now)
    {
        if (_phase != Phase.Up) _recoveredAt = now.Mono;
        _phase = Phase.Up;
        _lastHeard = now.Mono;
    }

    void Open(PeerNow now)
    {
        if (_floor is not null)
        {
            Refuse(now, "floor");
            return;
        }
        Linked(now);
        _open = true;
        _lastClass = null;
        _verified = false;
        _level = 1;
        _proof = "open";
    }

    /// <summary>
    /// 13.6.8: an acceptor that does not speak LINK. Floors first; then on <c>line</c> the legacy proof, for a NONCE heard on this
    /// connection; on <c>ws</c> the bearer only over TLS to the certificate a proof or a person pinned for the node, else the
    /// keyless view, its certificate pinned <c>first</c>.
    /// </summary>
    void LevelZero(PeerNow now)
    {
        if (_floor is { Level: >= 1 })
        {
            Refuse(now, "floor");
            return;
        }
        _legacy = true;
        _level = 0;
        _proof = "legacy";
        if (_s.Carrier == PeerCarrier.Line)
        {
            if (_nonceProvable) ProveLegacy(now);
            return;
        }
        Linked(now);
        if (_s.Key is not null && _tls && _cert is { } shown && _pin is { Trust: not PinTrust.First } p && p.Fingerprint == shown)
        {
            _keyless = false;
            Write(now, null, "bearer");
            return;
        }
        _keyless = true;
        if (_tls && _cert is { } seen && (_pin is null || (_pin.Trust == PinTrust.First && _pin.Fingerprint != seen)))
            _pin = new PeerPin(NodeId, seen, PinTrust.First);
    }

    /// <summary>13.6.8's AUTH-PROOF for a code, once a connection: each nonce answers one proof.</summary>
    void ProveLegacy(PeerNow now)
    {
        if (_s.Key != "code" || _authProofWritten || _proofWritten) return;
        _authProofWritten = true;
        Write(now, "AUTH-PROOF", "auth-proof");
    }

    /// <summary>13.16.1's refused state: a person must act. The connection ends; nothing more is sent.</summary>
    void Refuse(PeerNow now, string why)
    {
        _refused = why;
        _refusedAt = now.Mono;
        _why = null;
        Close(now, failed: false);
    }

    /// <summary>An answer to LINK the connector cannot take: counted, and the connection ends as a failed dial.</summary>
    void EndMalformed(PeerNow now)
    {
        _malformed++;
        _lastClass = "malformed";
        _why = null;
        Close(now, failed: true);
    }

    /// <summary>A <c>ws</c> socket kept as a public viewer after its key was refused ends; the connector dials a fresh one at once (13.15.3).</summary>
    void Relink(PeerNow now)
    {
        _why = null;
        Close(now, failed: false);
    }

    /// <summary>
    /// The connection ends: written commands become unconfirmed (13.15.5) and nothing of it is kept. A failed dial waits 13.15.1's
    /// wait; an end the connector chose to dial again from dials at once; a refusal dials nothing until a person acts.
    /// </summary>
    void Close(PeerNow now, bool failed)
    {
        if (_phase == Phase.Idle) return;
        foreach (var c in _commands.Values.Where(InFlightNow).ToList())
            Settle(c, PeerOutcome.Unconfirmed, "the link dropped before " + _s.Name + " answered");
        _phase = Phase.Idle;
        _phaseAt = now.Mono;
        _everClosed = true;
        _closedAt = now.Mono;
        _verified = _keyless = _legacy = _open = _plain = _codePlain = false;
        _offer = null;
        _greeted = _proofWritten = _nonceProvable = _authProofWritten = false;
        _reproveAt = long.MinValue;
        _level = -1;
        _proof = "none";
        _show = PeerShow.Unknown;
        _oversizeHere = _malformedHere = 0;
        _pings.Clear();
        if (_refused is not null) return;
        if (!failed)
        {
            _nextDialAt = now.Mono;
            return;
        }
        _waitMs = PeerLink.RedialWait(_failed, _j());
        _failed++;
        _nextDialAt = now.Mono + _waitMs;
    }

    void Hold(PeerNow now, string word, long ms)
    {
        _holdAt = now.Mono;
        _holdUntil = now.Mono + ms;
        _holdUntilWall = now.Wall + ms;
        _holdRefusal = word;
    }

    /// <summary>
    /// A complete message heard. Before the link is up — the acceptor's proof not verified, a level-0 pairing not settled — it is
    /// discarded and counted (13.6.3), and nothing of it is taken.
    /// </summary>
    bool Hear(PeerNow now)
    {
        Advance(now);
        if (_phase == Phase.Handshake)
        {
            _malformed++;
            _malformedHere++;
            return false;
        }
        if (_phase != Phase.Up) return false;
        var silence = now.Mono - _lastHeard;
        if (silence > PeerLink.DropMs) _wentDown = true;
        if (silence > PeerLink.StaleMs) _recoveredAt = now.Mono;
        _lastHeard = now.Mono;
        return true;
    }

    void Forward(Command c, PeerReading r)
    {
        switch (c.Outcome)
        {
            case null:
                Settle(c, r.Outcome, r.Words);
                break;
            case PeerOutcome.Accepted or PeerOutcome.Unconfirmed when r.Outcome is PeerOutcome.Applied or PeerOutcome.Refused && (r.Final || c.Outcome == PeerOutcome.Unconfirmed || !_can.Contains("final")):
                Settle(c, r.Outcome, r.Words);
                break;
        }
    }

    void Settle(Command c, PeerOutcome outcome, string words)
    {
        if (outcome == PeerOutcome.Unconfirmed && c.Outcome != PeerOutcome.Unconfirmed) _unconfirmed++;
        c.Outcome = outcome;
        c.Words = words;
        if (InFlightNow(c)) return;
        c.Left = ++_leftSeq;
        Trim();
    }

    /// <summary>
    /// The commands kept: every one still in flight — nothing waiting is evicted (13.12.8) — and the last
    /// <see cref="PeerLink.ReceiptsKept"/> to stop waiting, for their outcomes and a RECEIPTS ask.
    /// </summary>
    void Trim()
    {
        if (_commands.Count <= PeerLink.ReceiptsKept + PeerLink.InflightMax) return;
        var done = _commands.Values.Where(x => !InFlightNow(x)).OrderBy(x => x.Left).ToList();
        foreach (var x in done.Take(done.Count - PeerLink.ReceiptsKept)) _commands.Remove(x.Id);
    }

    void Write(PeerNow now, string? verb, string? credential, string? detail = null) => _writes.Add(new PeerWrite(now.Mono, verb, credential, detail));

    static JsonElement? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    long Since(PeerNow now, PeerLinkState state) => state switch
    {
        PeerLinkState.Refused => _refusedAt,
        PeerLinkState.Held => _holdUntil > now.Mono ? _holdAt : Math.Max(_markAt, _closedAt),
        PeerLinkState.Live => _recoveredAt,
        PeerLinkState.Stale => Math.Max(_recoveredAt, _lastHeard + PeerLink.StaleMs + 1),
        PeerLinkState.Down when _phase == Phase.Up => _lastHeard + PeerLink.DropMs + 1,
        _ => _phaseAt,
    };

    /// <summary>A fingerprint as the desk shows it: its first 16 hex in fours.</summary>
    static string Short(string fingerprint) => string.Join(' ', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4)));

    /// <summary>An id as words name it: its first four characters, and an ellipsis.</summary>
    static string Brief(string? id) => id is null ? "?" : (id.Length > 4 ? id[..4] : id) + "…";

    /// <summary>Why a link up with a key reads the public view keyless: 13.3.2's no TLS, 13.6.8's unpinned level 0, or a refused key.</summary>
    string Keyless(string name)
    {
        if (_s.Key is null) return name + " · public view";
        if (!_tls && !_s.Local) return name + " offers no TLS — the key stays on this machine";
        if (_legacy)
            return name + " cannot prove itself yet (an older Stagewatch) — "
                + (_cert is { } shown ? "compare its fingerprint " + Short(shown) + " and press Trust to send the key" : "reading its public view");
        return name + " did not take this desk's key (" + (_lastClass ?? _holdRefusal ?? "key") + ") — public view until it proves it again";
    }

    string Words(PeerNow now, PeerLinkState state)
    {
        var name = _s.Name;
        string Seconds(long ms) => (ms / 1000.0).ToString("0.#", Inv) + " s";
        return state switch
        {
            PeerLinkState.Off => "link off",
            PeerLinkState.Finding => "listening for " + name + " on the show network",
            PeerLinkState.Connecting => "dialling " + name + (_scheme == "plain" ? "" : " over TLS") + " (attempt " + (_failed + 1).ToString(Inv) + ")",
            PeerLinkState.Live => (_proof switch
            {
                "mutual" => name + " · proved" + (_pin is { } pin ? " · pinned " + pin.Fingerprint[..4] + " " + pin.Fingerprint[4..8] : ""),
                "legacy" when _keyless => Keyless(name),
                "legacy" => name + " cannot prove itself yet — an older build; anything at its address could answer",
                "open" => name + " asks for no key now — anything at its address could answer",
                _ => _keyless ? Keyless(name) : name + " · public view",
            }) + (_show == PeerShow.Stuck ? " — says its show is stuck" : ""),
            PeerLinkState.Stale => name + " quiet for " + Seconds(now.Mono - _lastHeard),
            PeerLinkState.Down => name + (_why is { } why ? ": " + why + " —" : " not answering —") + " dialling again in "
                + Seconds(Math.Max(0, _nextDialAt - now.Mono)) + "; nothing is queued",
            PeerLinkState.Held when _holdUntil > now.Mono => name + " refused this desk's key (" + _holdRefusal + ") — next try in " + Seconds(_holdUntil - now.Mono),
            PeerLinkState.Held => "two machines say they are " + (NodeId ?? name) + " — no new link until that clears",
            _ => _refused switch
            {
                "certificate" => "CERTIFICATE CHANGED: " + name + " now shows " + (_cert is { Length: >= 8 } c ? c[..4] + " " + c[4..8] : "another") + " … — nothing sent; accept it on the Nodes page",
                "floor" => name + " used to prove itself with PeerLink " + PeerLink.Version.ToString(Inv) + (_floor is { Tls: true } ? " over TLS" : "") + "; it now offers less — not linking",
                _ => (_s.Address ?? name) + " now answers as " + (_other is { } o ? o.T + " " + Brief(o.Id) : "another node") + " — not " + name + " "
                    + Brief(NodeId) + "; accept it on the Nodes page",
            },
        };
    }
}
