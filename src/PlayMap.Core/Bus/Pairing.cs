using System.Security.Cryptography;
using System.Text;

namespace PlayMap.Core.Bus;

/// <summary>Pairing tokens and proofs (Show Bus §7), byte-for-byte Patterns' scheme so one pairing token
/// format and one proof serve every app: a 12-symbol token over an alphabet without look-alikes, shown
/// "K7QM-3XWD-P9RA"; on connect the server sends "NONCE &lt;32 hex&gt;"; the client answers
/// "AUTH-PROOF &lt;hex&gt;" = HMAC-SHA256(key = UTF-8 of the token as §7's 1.1 desks key it, message = UTF-8 of nonce + name),
/// where name is what the client said in HELLO ("" when it said nothing). The token never crosses the wire. A token is compared in
/// Show Bus 1.3 §13.5.1's canonical form (<see cref="Normalise"/>), and one typed for a peer is taken only when it is letters and
/// digits once that form drops its dashes and spaces, with nothing a 1.1 desk reads apart (<see cref="Refusal"/>).</summary>
public static class Pairing
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int TokenLength = 12;

    public static string NewToken()
    {
        var chars = new char[TokenLength];
        for (var i = 0; i < TokenLength; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Display(new string(chars));
    }

    /// <summary>"K7QM-3XWD-P9RA" from "K7QM3XWDP9RA".</summary>
    public static string Display(string token)
    {
        var n = Normalise(token);
        var sb = new StringBuilder();
        for (var i = 0; i < n.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
                sb.Append('-');
            sb.Append(n[i]);
        }
        return sb.ToString();
    }

    /// <summary>Show Bus 1.3 §13.5.1's canonical form of a token: '-' and every space and separator 13.5.1 names removed (U+0009–U+000D,
    /// U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000 and U+FEFF) and a–z upper-cased; every
    /// other character is kept as it is, so a token that is not letters and digits stays one (<see cref="Canon"/> refuses it). "" for
    /// none.</summary>
    public static string Normalise(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return "";
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
            if (!PeerLink.CanonRemoves(c))
                sb.Append(c is >= 'a' and <= 'z' ? (char)(c - 'a' + 'A') : c);
        return sb.ToString();
    }

    /// <summary>13.5.1: the canonical form when it is <c>[A-Z0-9]{1,64}</c>, else null — a token PeerLink never proves.</summary>
    public static string? Canon(string? token) => token is null ? null : PeerLink.Canon(token);

    /// <summary>13.5.1's words for a token that is not letters and digits once its dashes and spaces are dropped.</summary>
    public const string NotLettersAndDigits = "PeerLink pairs a token of letters and digits — this one reaches only an older build";

    /// <summary>13.6.8's words for a token that still holds a character 13.5.1 removes once a 1.1 desk's dashes and spaces are
    /// dropped (another space, a line or paragraph separator, a byte order mark): 1.1 desks read it apart, so it is never proved.</summary>
    public const string InvisibleCharacter = "this token holds an invisible character — retype it";

    /// <summary>Why a pairing token typed for a peer is not taken where it is typed (Show Bus 1.3 §13.5.1, §13.6.8), or null when it
    /// is: one that holds an invisible character a 1.1 desk keys apart is never proved by §7's proof, and one that is not letters and
    /// digits once its dashes and spaces are dropped is never proved by PeerLink. A token is never echoed in the words. "" (no token)
    /// is taken.</summary>
    public static string? Refusal(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        if (PeerLink.LegacyKey(token) is null && Normalise(token).Length > 0)
            return InvisibleCharacter;
        return Canon(token) is null ? NotLettersAndDigits : null;
    }

    /// <summary>16 random bytes as 32 lowercase hex.</summary>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>A NONCE a proof may answer: exactly 32 lowercase hex, as <see cref="NewNonce"/> makes and Patterns sends. A client proves
    /// no other shape: a proof of a word the peer chose is a proof the peer can replay or guess against.</summary>
    public static bool IsNonce(string? nonce) =>
        nonce is { Length: 32 } n && n.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>The key of §7's proof: the token as 1.1 desks key it (Show Bus 1.3 §13.6.8: '-', space, tab, CR and LF removed, every
    /// other character upper-cased alone), or — for a token 13.6.8 does not prove, which a client never proves (<see cref="Refusal"/>)
    /// — its canonical form, so this node's own check of one stays the same on both ends. Both are the same for every token of letters,
    /// digits, dashes and spaces.</summary>
    private static byte[] Key(string token) => Encoding.UTF8.GetBytes(PeerLink.LegacyKey(token) ?? Normalise(token));

    /// <summary>The proof a client sends: lowercase hex of HMAC-SHA256(the token's key, nonce + name).</summary>
    public static string Proof(string token, string nonce, string? name)
    {
        var msg = Encoding.UTF8.GetBytes(nonce + (name ?? ""));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Key(token), msg));
    }

    /// <summary>Checks a proof in constant time; either hex case is accepted.</summary>
    public static bool Verify(string token, string nonce, string? name, string? proofHex)
    {
        if (string.IsNullOrEmpty(proofHex) || proofHex.Length != 64)
            return false;
        byte[] given;
        try
        {
            given = Convert.FromHexString(proofHex);
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = HMACSHA256.HashData(Key(token), Encoding.UTF8.GetBytes(nonce + (name ?? "")));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>The name a HELLO carries, without a trailing " module=…" (Patterns' rule).</summary>
    public static string HelloName(string helloArgs)
    {
        ArgumentNullException.ThrowIfNull(helloArgs);
        var t = helloArgs.Trim();
        var m = t.IndexOf(" module=", StringComparison.Ordinal);
        return m >= 0 ? t[..m].Trim() : t;
    }
}

/// <summary>A receipt (Show Bus §3): a command with an id is answered with what happened to it.
/// Stage "accepted" = queued, not yet confirmed; "applied" = ran, and <see cref="Effect"/> says what changed.</summary>
public sealed record Receipt(string Id, bool Ok, string Stage, string Effect, string? Error, IReadOnlyDictionary<string, string>? Refs)
{
    public static Receipt Applied(string id, string effect, IReadOnlyDictionary<string, string>? refs = null) => new(id, true, "applied", effect, null, refs);
    public static Receipt Accepted(string id, string effect) => new(id, true, "accepted", effect, null, null);
    public static Receipt Refused(string id, string error) => new(id, false, "applied", "", error, null);
}

/// <summary>The node's own settings — machine-local, never in a project file (a project travels; a pairing
/// token and a bind address do not). Each round-2 package keeps its own settings in NodeSettings.&lt;Word&gt;.cs; an older file reads
/// their defaults. The node's id, name and token are written first, so a file cut short still says who the node is.</summary>
public sealed partial record NodeSettings(
    /// <summary>Stable per install, 8 lowercase hex.</summary>
    [property: System.Text.Json.Serialization.JsonPropertyOrder(-3)] string NodeId,
    [property: System.Text.Json.Serialization.JsonPropertyOrder(-2)] string Name,
    /// <summary>"" = all interfaces; an address = that interface only (fails closed when invalid).</summary>
    string Bind,
    int HttpPort,
    int WirePort,
    int SyncPort,
    /// <summary>The pairing token ("" = open: this node asks for no token).</summary>
    [property: System.Text.Json.Serialization.JsonPropertyOrder(-1)] string Token,
    bool TrustLoopback,
    /// <summary>Addresses whose lines count as paired without a proof — a Patterns desk driving this node as
    /// an Interactive "Lines" device cannot answer a NONCE (Patterns' OSC ALLOW list, the same idea).</summary>
    System.Collections.Immutable.ImmutableArray<string> TrustedAddresses,
    bool RequireHttps,
    bool Announce,
    /// <summary>Patterns to follow (host[:port]) and its token; empty = none.</summary>
    string PatternsHost,
    string PatternsToken,
    /// <summary>Stagewatch to follow (host[:port], "" = the first heard).</summary>
    string StagewatchHost,
    /// <summary>"": the Stagewatch key lives in the node's credential store, never in this file. A file an older node wrote may still
    /// hold it here; the network host moves it into the store at start and only then writes "" (while the store cannot be written, it
    /// stays here and the node says so).</summary>
    string StagewatchKey,
    bool StagewatchEnabled)
{
    public static NodeSettings CreateDefault(string name) => new(
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), name, "",
        ShowBusConstants.PlayMapHttpPort, ShowBusConstants.PlayMapWirePort, ShowBusConstants.PlayMapSyncPort,
        Pairing.NewToken(), true, [], false, true, "", "", "", "", false);
}
