using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests;

/// <summary>The line grammar, the vocabulary, the hello, the context and pairing — each checked against
/// what Patterns and Stagewatch send and accept.</summary>
public class WireAndBusTests
{
    [Fact]
    public void ARequestIdRidesInFrontOfTheLine()
    {
        Assert.True(WireGrammar.TryParse("@a1 VIDEO END 10", out var line, out _));
        Assert.Equal("a1", line.RequestId);
        Assert.Equal("VIDEO", line.Verb);
        Assert.Equal(["VIDEO", "END", "10"], line.Words);
        Assert.Equal("OK @a1", WireGrammar.Ok("a1"));
        Assert.Equal("ERR @a1 no clip", WireGrammar.Err("a1", "no clip"));
    }

    [Fact]
    public void ALinesSecretWordsNeverReachWhatIsWrittenDown()
    {
        string Shown(string text)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out _));
            return VerbCatalog.Redacted(line);
        }

        Assert.Equal("LINK PATTERNS 10.0.0.5 \u2039secret\u203a", Shown("LINK PATTERNS 10.0.0.5 s3cr3t-token"));
        Assert.Equal("LINK STAGEWATCH \"\" \u2039secret\u203a", Shown("LINK STAGEWATCH \"\" key-123"));
        Assert.Equal("CREDENTIAL SET pj1 \u2039secret\u203a", Shown("CREDENTIAL SET pj1 admin hunter2"));
        Assert.Equal("AUTH \u2039secret\u203a", Shown("AUTH abcdef"));
        Assert.DoesNotContain("hunter2", Shown("@r1 CREDENTIAL SET pj1 admin hunter2"), StringComparison.Ordinal);
        // A line without its secret, and every other verb, is written as it came.
        Assert.Equal("LINK PATTERNS 10.0.0.5", Shown("LINK PATTERNS 10.0.0.5"));
        Assert.Equal("BLACKOUT ON", Shown("BLACKOUT ON"));
        Assert.Equal("LINK PATTERNS 10.0.0.5 \u2039secret\u203a", VerbCatalog.Redacted("LINK PATTERNS 10.0.0.5 s3cr3t-token"));

        // A line holding its secret is told apart from the copy shown in its place, which must never run.
        Assert.True(WireGrammar.TryParse("LINK PATTERNS 10.0.0.5 s3cr3t-token", out var whole, out _));
        Assert.True(WireGrammar.TryParse(Shown("LINK PATTERNS 10.0.0.5 s3cr3t-token"), out var copy, out _));
        Assert.True(VerbCatalog.HoldsSecret(whole));
        Assert.False(VerbCatalog.IsShownCopy(whole));
        Assert.True(VerbCatalog.IsShownCopy(copy));
    }

    [Fact]
    public void AMalformedIdIsRefusedWithPatternsWords()
    {
        Assert.False(WireGrammar.TryParse("@bad!id VIDEO PLAY", out _, out var error));
        Assert.Contains("a request id is @ and 1-32 letters", error!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotedPhrasesAndJsonStayWhole()
    {
        Assert.True(WireGrammar.TryParse("PLAYLIST NEW \"Main \\\"show\\\" reel\"", out var line, out _));
        Assert.Equal("Main \"show\" reel", line.Word(2));
        Assert.True(WireGrammar.TryParse("SET sf1 content {\"mode\":\"uv\", \"source\":{}}", out var json, out _));
        Assert.Equal(4, json.Count);
        Assert.Equal("{\"mode\":\"uv\", \"source\":{}}", json.Word(3));
        Assert.Equal("{\"mode\":\"uv\", \"source\":{}}", json.RestAfter(3));
        Assert.False(WireGrammar.TryParse("SAY \"unclosed", out _, out _));
    }

    [Fact]
    public void AWordThatOpensLikeJsonIsQuotedAndStaysOneWordInTheMiddleOfALine()
    {
        // The grammar reads a bare "{" or "[" as JSON to the end of the line: such a word is quoted wherever it stands.
        foreach (var word in new[] { "[L]", "{W}", "[", "{\"a\":1}", "[ left ]" })
        {
            var text = "DEVICE ADD " + WireGrammar.Quote(word) + " pjlink 10.0.0.20";
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), $"{text}: {error?.Reason}");
            Assert.Equal(["DEVICE", "ADD", word, "pjlink", "10.0.0.20"], line.Words);
        }
        Assert.Equal("\"[L]\"", WireGrammar.Quote("[L]"));
        Assert.Equal("L[1]", WireGrammar.Quote("L[1]"));
    }

    [Fact]
    public void PatternsClipVerbsAndTheirAliasesMatch()
    {
        foreach (var text in new[] { "VIDEO PLAY", "VT PLAY", "CLIP RESUME", "video resume" })
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out _));
            Assert.Equal("VIDEO PLAY", VerbCatalog.Match(line)!.Value.Spec.Name);
        }
        Assert.True(WireGrammar.TryParse("STOP", out var stop, out _));
        Assert.Null(VerbCatalog.Match(stop));
        Assert.True(WireGrammar.TryParse("STATUS", out var status, out _));
        Assert.Equal("STATE", VerbCatalog.Match(status)!.Value.Spec.Name);
    }

    [Fact]
    public void EveryVerbIsUniqueAndQueriesNeverMutate()
    {
        var names = VerbCatalog.All.SelectMany(v => v.Aliases.Prepend(v.Name)).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var v in VerbCatalog.All.Where(v => v.Name is "STATE" or "CONTEXT" or "PING" or "GET" or "NODES"))
            Assert.False(v.Mutates);
        Assert.All(VerbCatalog.All.Where(v => v.Category == VerbCategory.Transport), v => Assert.Equal(VerbRisk.High, v.Risk));
    }

    [Fact]
    public void OurHelloIsReadBackAndCarriesTheLegacyFields()
    {
        var hello = new ShowBusHello("playmap", 1, 1, "a1b2c3d4", "Stage left", "RENDER-01",
            new Dictionary<string, int> { ["http"] = 9940, ["ws"] = 9940, ["wire"] = 9941 },
            ShowBusHello.PlayMapCaps, 1760101234567, "0badc0de", "sha256:" + new string('a', 64));
        var bytes = hello.ToJson();
        Assert.True(bytes.Length < ShowBusConstants.HelloMaxBytes);
        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal(9941, doc.RootElement.GetProperty("wire").GetInt32());
        Assert.Equal(9940, doc.RootElement.GetProperty("web").GetInt32());
        var back = ShowBusHello.Parse(bytes)!;
        Assert.Equal("playmap", back.Kind);
        Assert.Equal(9941, back.Port("wire"));
        Assert.Equal("0badc0de", back.Instance);
        Assert.True(back.Caps.Count <= 16);
    }

    [Fact]
    public void AStagewatchAndAPatternsHelloAreUnderstood()
    {
        const string sw = "{\"t\":\"main\",\"v\":1,\"proto\":1,\"id\":\"a1b2c3d4\",\"name\":\"Hall A\",\"host\":\"FOH-LAPTOP\",\"ports\":{\"http\":9924,\"ws\":9924},\"caps\":[\"timer\",\"cues\",\"messages\",\"context\",\"receipts\"],\"clock\":1760101234567}";
        var s = ShowBusHello.Parse(Encoding.UTF8.GetBytes(sw))!;
        Assert.Equal("main", s.Kind);
        Assert.Equal(9924, s.Port("ws"));
        // An old Patterns hello: only top-level web and wire.
        const string pt = "{\"t\":\"patterns\",\"v\":1,\"id\":\"ab12cd34\",\"name\":\"FOH\",\"machine\":\"FOH-PC\",\"web\":9696,\"wire\":9697,\"role\":\"desk\",\"caps\":[\"video.clock\"],\"proto\":1}";
        var p = ShowBusHello.Parse(Encoding.UTF8.GetBytes(pt))!;
        Assert.Equal(9697, p.Port("wire"));
        Assert.Equal("FOH-PC", p.Host);
        Assert.Null(ShowBusHello.Parse(Encoding.UTF8.GetBytes(pt.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal))));
        Assert.Null(ShowBusHello.Parse(Encoding.UTF8.GetBytes("{\"t\":\"bad kind!\",\"v\":1,\"id\":\"x\"}")));
    }

    [Fact]
    public void TheContextKeepsTheContractsKeyOrderAndReadsBack()
    {
        var ctx = new ShowContext("playmap", "a1b2c3d4", "Stage left", 1760101234567, "Playing Intro · 4 outputs green",
            [new ContextItem("output:op1", "output", "Projector 1", Light.Green, "60 fps"), new ContextItem("link:patterns", "link", "Patterns", Light.Amber, "not paired")],
            [new ContextLink("link:patterns", "output:op1", "drives", Light.Amber)],
            [new ContextRecent(1760101230000, "PLAYLIST GO Show")],
            ShowContext.CommandsFromCatalog(), []);
        var json = ctx.ToJson();
        var keys = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["v", "node", "at", "light", "headline", "items", "links", "recent", "commands", "peers"], keys);
        Assert.Contains("\"light\":\"amber\"", json, StringComparison.Ordinal);
        var back = ShowContext.Parse(json)!;
        Assert.Equal(Light.Amber, back.Light);
        Assert.Equal(2, back.Items.Count);
        Assert.Contains(back.Commands, c => c.Verb.StartsWith("VIDEO PLAY", StringComparison.Ordinal) && c.Risk == VerbRisk.High);
    }

    [Fact]
    public void ThePairingProofIsPatternsArithmetic()
    {
        // HMAC-SHA256(key = "K7QM3XWDP9RA", msg = nonce + "Stagewatch"), computed independently.
        const string nonce = "00112233445566778899aabbccddeeff";
        var proof = Pairing.Proof("k7qm-3xwd p9ra", nonce, "Stagewatch");
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes("K7QM3XWDP9RA"));
        var expected = Convert.ToHexStringLower(h.ComputeHash(Encoding.UTF8.GetBytes(nonce + "Stagewatch")));
        Assert.Equal(expected, proof);
        Assert.True(Pairing.Verify("K7QM-3XWD-P9RA", nonce, "Stagewatch", proof.ToUpperInvariant()));
        Assert.False(Pairing.Verify("K7QM-3XWD-P9RB", nonce, "Stagewatch", proof));
        Assert.Equal("Companion", Pairing.HelloName("Companion module=2.1.0"));
        var token = Pairing.NewToken();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", token);
        Assert.DoesNotContain('O', token);
        Assert.DoesNotContain('1', token);
    }

    [Fact]
    public void LivenessAndRedialFollowTheContract()
    {
        Assert.Equal(PeerLiveness.Live, Liveness.Of(5999));
        Assert.Equal(PeerLiveness.Stale, Liveness.Of(6000));
        Assert.Equal(PeerLiveness.Gone, Liveness.Of(12000));
        Assert.InRange(Liveness.RedialMs(0, 0.5), 400, 600);
        Assert.InRange(Liveness.RedialMs(10, 1), 5000, 6000);
    }
}
