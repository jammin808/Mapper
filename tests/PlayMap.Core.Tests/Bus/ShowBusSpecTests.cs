using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlayMap.Core.Bus;

namespace PlayMap.Core.Tests.Bus;

/// <summary>This tree's copy of Show Bus 1.3 (<c>docs/SHOWBUS.md</c>) and its scripted peer (<c>tools/showbus/peerlink-peer.mjs</c>),
/// read from this tree alone and held to the two hashes the master pins (§14.5): the body's, which the header's Body line prints, and the
/// peer's, §14's <c>peerlink.peer.sha256</c>. Inside the hashed body, the <c>```showbus</c> block's rows are held to the Core code that
/// keeps each, and the <c>```peerlink</c> vectors Core's code answers today are held to it; every other vector is named as the PeerLink
/// core's, so a copy that brings a case no one has placed fails here.</summary>
public sealed partial class ShowBusSpecTests
{
    /// <summary>The body's SHA-256 the master's header prints (Stagewatch's <c>docs/SHOWBUS.md</c>, Show Bus 1.3 final). A new master is
    /// copied whole, byte for byte, with its scripted peer, and both numbers here follow it.</summary>
    public const string BodySha256 = "9ca83889a3735eba9dd948a53be3045b9cd1855c68a3682d5e734adf8f901fd5";

    /// <summary>The scripted peer's SHA-256 the master's §14 pins (<c>peerlink.peer.sha256</c>).</summary>
    public const string PeerSha256 = "f36847cfebeee05e9e76fa4f2071a4c205a544fddb0231c247c6975893dabc28";

    private const string Start = "## 1. Rules every node follows";
    private const string Follow = " — copy the master's docs/SHOWBUS.md and tools/showbus/peerlink-peer.mjs byte for byte, and pin its numbers here";

    // The master's tools/showbus-copies.sh reads the Body line, the Landed line and §14's peer row by these same patterns.
    [GeneratedRegex(@"^\*Body SHA-256 \(§14\): ([0-9a-f]{64})\.\*$", RegexOptions.CultureInvariant)]
    private static partial Regex BodyLine();

    [GeneratedRegex(@"^\*Landed \(§14\): (.+)\.\*$", RegexOptions.CultureInvariant)]
    private static partial Regex LandedLine();

    [GeneratedRegex(@"^peerlink\.peer\.sha256 ([0-9a-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex PeerPin();

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RowName();

    /// <summary>The rows that take a row per word (§14.1).</summary>
    private static readonly string[] ManyRows = ["kind", "port", "cap", "reserved", "peerlink.feature"];

    /// <summary>The rows whose value is a word, not a whole number (§14.1, §14.2).</summary>
    private static readonly string[] WordRows = ["revision", "peerlink.label", "window.token.alphabet", "peerlink.peer.sha256"];

    [Fact]
    public void TheCopyIsTheMastersBodyAndItsHeaderPrintsTheMastersHash()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "docs", "SHOWBUS.md"));
        Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), "docs/SHOWBUS.md starts with a byte order mark");
        var (header, body) = Contract();

        Assert.EndsWith("\n", body, StringComparison.Ordinal);
        Assert.False(body.EndsWith("\n\n", StringComparison.Ordinal), "the body ends with more than one line end");
        Assert.True(Sha(body) == BodySha256, $"the body hashes to {Sha(body)}, the master's to {BodySha256}{Follow}");

        var printed = header.Split('\n').Select(l => BodyLine().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        Assert.True(printed is [BodySha256], $"the header prints [{string.Join(", ", printed)}], once and only {BodySha256} (§14.5)");
        Assert.Single(header.Split('\n'), l => LandedLine().IsMatch(l));
        Assert.DoesNotContain(body.Split('\n'), l => BodyLine().IsMatch(l) || LandedLine().IsMatch(l));
    }

    [Fact]
    public void TheScriptedPeerIsTheOneSection14Pins()
    {
        var (_, body) = Contract();
        var pins = body.Split('\n').Select(l => PeerPin().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        Assert.True(pins is [PeerSha256], $"§14 pins [{string.Join(", ", pins)}], once and only {PeerSha256}{Follow}");

        var peer = File.ReadAllBytes(Path.Combine(Root(), "tools", "showbus", "peerlink-peer.mjs"));
        var got = Convert.ToHexStringLower(SHA256.HashData(peer));
        Assert.True(got == PeerSha256, $"tools/showbus/peerlink-peer.mjs hashes to {got}, §14 pins {PeerSha256}{Follow}");
    }

    [Fact]
    public void TheNumbersBlockIsWellFormed()
    {
        var rows = Rows();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.True(row.Length > 0 && row.All(char.IsAscii) && !row.Contains("  ", StringComparison.Ordinal) && row == row.Trim(),
                $"the row '{row}' is not one line of ASCII words separated by one space (§14.1)");
            var words = row.Split(' ');
            Assert.True(words.Length >= 2 && RowName().IsMatch(words[0]), $"the row '{row}' names no row");
        }
        var single = rows.Select(r => r.Split(' ')[0]).Where(n => !ManyRows.Contains(n)).ToList();
        Assert.Equal(single.Count, single.Distinct(StringComparer.Ordinal).Count());
        foreach (var row in rows.Select(r => r.Split(' ')).Where(w => !ManyRows.Contains(w[0]) && !WordRows.Contains(w[0])))
            Assert.True(row.Length == 2 && long.TryParse(row[1], NumberStyles.None, CultureInfo.InvariantCulture, out _),
                $"the row '{string.Join(' ', row)}' is not a name and a whole number (§14.1)");
        Assert.Equal("1.3", Number("revision"));
    }

    /// <summary>Every row Core keeps, held to its constant: the hello's, the hearing's, the link's beat and redial, the clock's, and the
    /// context's peer rows.</summary>
    [Fact]
    public void CoresNumbersAreTheBlocks()
    {
        Assert.Equal(ShowBusConstants.Proto, Whole("proto"));
        Assert.Equal(ShowBusConstants.HelloPort, Whole("hello.port"));
        Assert.Equal(ShowBusConstants.HelloEveryMs, Whole("hello.every.ms"));
        Assert.Equal(ShowBusConstants.HelloMaxBytes, Whole("hello.bytes"));
        Assert.True(ShowBusConstants.HelloReadBytes >= Whole("hello.read.bytes"), "the reader takes less than hello.read.bytes");
        Assert.Equal(ShowBusConstants.HelloCaps, Whole("hello.caps"));
        Assert.Equal(ShowBusConstants.HelloCapChars, Whole("hello.cap.chars"));
        Assert.Equal(ShowBusConstants.HelloPorts, Whole("hello.ports"));
        Assert.Equal(ShowBusConstants.HelloNameChars, Whole("hello.name.chars"));
        Assert.Equal(ShowBusConstants.HelloKindChars, Whole("hello.kind.chars"));
        Assert.Equal(ShowBusConstants.HelloIdChars, Whole("hello.id.max"));
        Assert.Equal(ShowBusConstants.HelloInstChars, Whole("hello.inst.chars"));
        Assert.Equal(ShowBusConstants.PeersMax, Whole("peers.max"));
        Assert.Equal(ShowBusConstants.PeerAddresses, Whole("peer.addresses"));
        Assert.Equal(ShowBusConstants.StaleMs, Whole("peer.stale.ms"));
        Assert.Equal(ShowBusConstants.GoneMs, Whole("peer.gone.ms"));
        Assert.Equal(ShowBusConstants.ForgetMs, Whole("peer.forget.ms"));
        Assert.Equal(ShowBusConstants.ConflictClearMs, Whole("peer.conflict.clear.ms"));
        Assert.Equal(ShowBusConstants.BeatMs, Whole("link.beat.ms"));
        Assert.Equal(ShowBusConstants.RedialMinMs, Whole("link.redial.first.ms"));
        Assert.Equal(ShowBusConstants.RedialMaxMs, Whole("link.redial.max.ms"));
        Assert.Equal(ShowBusConstants.OffsetWindow, Whole("clock.window"));
        Assert.Equal(ShowBusConstants.ClocksApartMs, Whole("clock.apart.ms"));
        Assert.Equal(ShowBusConstants.ContextPeers, Whole("context.peers"));

        // The redial's jitter moves a wait that far either way (13.15.1).
        var pct = Whole("peerlink.redial.jitter.pct") / 100.0;
        Assert.Equal((int)Math.Round(ShowBusConstants.RedialMinMs * (1 - pct)), Liveness.RedialMs(0, 0));
        Assert.Equal((int)Math.Round(ShowBusConstants.RedialMinMs * (1 + pct)), Liveness.RedialMs(0, 1));
        Assert.Equal((int)Math.Round(ShowBusConstants.RedialMaxMs * (1 + pct)), Liveness.RedialMs(30, 1));
    }

    [Fact]
    public void TheKindsAndPlayMapsCapsAreTheBlocksRows()
    {
        var kinds = Many("kind").ToDictionary(w => w[0], w => string.Join(' ', w.Skip(1)), StringComparer.Ordinal);
        Assert.Equal("PlayMap", kinds[ShowBusConstants.KindPlayMap]);
        Assert.Equal("Patterns", kinds[ShowBusConstants.KindPatterns]);
        Assert.Equal("Stagewatch", kinds[ShowBusConstants.KindStagewatch]);

        var playmapCaps = Many("cap").Where(w => w.Skip(1).Contains(ShowBusConstants.KindPlayMap)).Select(w => w[0]).ToHashSet(StringComparer.Ordinal);
        Assert.All(ShowBusHello.PlayMapCaps, c => Assert.True(playmapCaps.Contains(c), $"'{c}' is no cap row for playmap"));
        Assert.All(ShowBusHello.PlayMapCaps, c => Assert.True(ShowBusHello.CapShape(c), $"'{c}' is not of §2's cap shape"));
        Assert.True(ShowBusHello.PlayMapCaps.Count <= ShowBusConstants.HelloCaps);
        // PlayMap offers neither LINK nor a granted window's drawing yet (§8.3), so it never says either.
        Assert.DoesNotContain("peerlink", ShowBusHello.PlayMapCaps);
        Assert.DoesNotContain("window-draw", ShowBusHello.PlayMapCaps);
    }

    /// <summary>§2's reader, as Core parses a hello: the first 32 caps of the cap shape, at most 8 doors, a name and a host kept whole up
    /// to 64 characters, an inst only of its shape, and a datagram of <c>hello.read.bytes</c> taken.</summary>
    [Fact]
    public void TheReaderKeepsWhatSection2Says()
    {
        var caps = Enumerable.Range(0, 40).Select(i => "cap" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        var name = string.Concat(Enumerable.Repeat("😀", 32));   // 64 characters, each pair whole
        var json = Hello(caps.Prepend("Not.A.Cap").Prepend(new string('a', 25)), name: name, host: name + "x", inst: "a41f09c2",
            ports: Enumerable.Range(1, 10).ToDictionary(i => "door" + i.ToString(CultureInfo.InvariantCulture), i => 9000 + i));
        var hello = ShowBusHello.Parse(json)!;
        Assert.Equal(caps.Take(Whole("hello.caps")), hello.Caps);
        Assert.Equal(Whole("hello.ports"), hello.Ports.Count);
        Assert.Equal(name, hello.Name);
        Assert.Equal(name, hello.Host);
        Assert.Equal("a41f09c2", hello.Instance);
        foreach (var inst in new[] { "A41F09C2", "a41f09c", "a41f09c2e", "g41f09c2" })
            Assert.Null(ShowBusHello.Parse(Hello(["video"], inst: inst))!.Instance);
        Assert.Equal("Hall A", ShowBusHello.Parse(Hello(["video"], name: "Hall\u0007 A"))!.Name);

        // A hello padded to exactly hello.read.bytes with a field no reader knows is taken.
        var bare = Hello(["video"], extra: "");
        var padded = Hello(["video"], extra: new string('x', Whole("hello.read.bytes") - bare.Length));
        Assert.Equal(Whole("hello.read.bytes"), padded.Length);
        Assert.NotNull(ShowBusHello.Parse(padded));
    }

    /// <summary>A hello is <c>hello.bytes</c> at most as sent, whatever its names: names that escape to six bytes a character are cut
    /// until it fits; an ordinary one is sent whole, its characters as UTF-8.</summary>
    [Fact]
    public void AFittedHelloIsWithinHelloBytesWhateverItsNames()
    {
        string[] names = [new string('\u20ac', 80), string.Concat(Enumerable.Repeat("😀", 40)), new string('"', 80), new string('\u0378', 80), new string('\u2028', 80)];
        foreach (var n in names)
        {
            var hello = Worst(ShowBusHello.NameOf(n)).Fitted();
            Assert.InRange(hello.ToJson().Length, 1, Whole("hello.bytes"));
            Assert.NotNull(ShowBusHello.Parse(hello.ToJson()));
        }
        var plain = Worst("Bühne links");
        Assert.Same(plain, plain.Fitted());
        Assert.Contains("\"name\":\"Bühne links\"", Encoding.UTF8.GetString(plain.ToJson()), StringComparison.Ordinal);
    }

    /// <summary>The ```peerlink vectors Core answers today — the redial's wait and the 1.1 legacy proof with its nonce shape — held to
    /// its code; every other case is the PeerLink core's (fix-peerlink-core), named so a new case is placed before it lands.</summary>
    [Fact]
    public void TheVectorsCoreAnswersHold()
    {
        string[] cases = ["canon", "key", "proof", "refuse", "legacy", "parse", "liveness", "redial", "errms", "clock", "outcome", "machine"];
        var vectors = Block("peerlink");
        Assert.NotEmpty(vectors);
        var held = 0;
        var codeForms = 0;
        foreach (var line in vectors)
        {
            using var doc = JsonDocument.Parse(line);
            var v = doc.RootElement;
            var kind = v.GetProperty("case").GetString()!;
            Assert.True(cases.Contains(kind), $"the vector case '{kind}' is placed nowhere: {line}");
            if (kind == "redial")
            {
                var ms = Liveness.RedialMs(v.GetProperty("n").GetInt32(), v.GetProperty("j").GetDouble());
                Assert.InRange(ms, v.GetProperty("ms").GetInt32() - v.GetProperty("tol").GetInt32(), v.GetProperty("ms").GetInt32() + v.GetProperty("tol").GetInt32());
                held++;
            }
            else if (kind == "legacy" && v.TryGetProperty("proof", out var proof))
            {
                Assert.Equal(proof.GetString(), Pairing.Proof(v.GetProperty("code").GetString()!, v.GetProperty("nonce").GetString()!, v.GetProperty("name").GetString()));
                Assert.True(Pairing.IsNonce(v.GetProperty("nonce").GetString()));
                held++;
            }
            else if (kind == "legacy" && !Pairing.IsNonce(v.GetProperty("nonce").GetString()))
            {
                held++;   // no proof for a nonce of another shape: the link proves only 32 lowercase hex
            }
            else if (kind == "legacy")
            {
                codeForms++;   // a code whose canonical form refuses it: the PeerLink core's canonical form (fix-peerlink-core)
            }
        }
        Assert.Equal(7 + 3 + 2, held);
        Assert.Equal(2, codeForms);
    }

    private static ShowBusHello Worst(string name) =>
        new(ShowBusConstants.KindPlayMap, 1, ShowBusConstants.Proto, new string('a', ShowBusConstants.HelloIdChars), name, name,
            new Dictionary<string, int> { ["http"] = 65535, ["https"] = 65535, ["ws"] = 65535, ["wire"] = 65534, ["sync"] = 65533 },
            [.. ShowBusHello.PlayMapCaps, "eye", "show"], long.MaxValue, "0badf00d", "sha256:" + new string('f', 64))
        { Build = new string('1', ShowBusHello.MaxBuildChars) };

    private static byte[] Hello(IEnumerable<string> caps, string name = "FOH", string host = "FOH-PC", string? inst = null,
        Dictionary<string, int>? ports = null, string? extra = null)
    {
        var o = new Dictionary<string, object?>
        {
            ["t"] = "playmap", ["v"] = 1, ["proto"] = 3, ["id"] = "cccc0003", ["name"] = name, ["host"] = host,
            ["ports"] = ports ?? new Dictionary<string, int> { ["wire"] = 9941 }, ["caps"] = caps.ToArray(), ["clock"] = 1,
        };
        if (inst is not null)
            o["inst"] = inst;
        if (extra is not null)
            o["zz"] = extra;
        return JsonSerializer.SerializeToUtf8Bytes(o);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The copy's header and body, CRLF read as LF (a checkout may convert them): the body from its first line to the end.</summary>
    private static (string Header, string Body) Contract()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "docs", "SHOWBUS.md"), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        Assert.True(lines.Count(l => l == Start) == 1, $"the line '{Start}' is not in docs/SHOWBUS.md once");
        var at = text.StartsWith(Start + "\n", StringComparison.Ordinal) ? 0 : text.IndexOf("\n" + Start + "\n", StringComparison.Ordinal) + 1;
        return (text[..at], text[at..]);
    }

    /// <summary>A fenced block of the body (```<paramref name="name"/> to the next ```), once.</summary>
    private static List<string> Block(string name)
    {
        var lines = Contract().Body.Split('\n');
        var opens = lines.Select((l, i) => (l, i)).Where(x => x.l == "```" + name).Select(x => x.i).ToList();
        Assert.True(opens.Count == 1, $"the body has {opens.Count} ```{name} blocks, not one");
        return lines.Skip(opens[0] + 1).TakeWhile(l => l != "```").ToList();
    }

    private static List<string> Rows() => Block("showbus");

    private static string Number(string name) =>
        Rows().Select(r => r.Split(' ')).Single(w => w[0] == name)[1];

    private static int Whole(string name) => int.Parse(Number(name), NumberStyles.None, CultureInfo.InvariantCulture);

    private static IEnumerable<string[]> Many(string name) =>
        Rows().Select(r => r.Split(' ')).Where(w => w[0] == name).Select(w => w[1..]);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above " + AppContext.BaseDirectory);
    }
}
