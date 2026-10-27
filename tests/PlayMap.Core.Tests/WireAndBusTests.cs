using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Control;
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
        // A two-word name quoted as one word is still the verb, its arguments start one word sooner, and so does its secret.
        Assert.Equal("\"LINK PATTERNS\" 10.0.0.5 \u2039secret\u203a", Shown("\"LINK PATTERNS\" 10.0.0.5 s3cr3t-token"));
        Assert.Equal("\"credential set\" pj1 \u2039secret\u203a", Shown("\"credential set\" pj1 admin hunter2"));
        Assert.True(WireGrammar.TryParse("\"LINK PATTERNS\" 10.0.0.5 s3cr3t-token", out var quoted, out _));
        Assert.True(VerbCatalog.HoldsSecret(quoted));
        Assert.True(WireGrammar.TryParse("\"LINK PATTERNS\" 10.0.0.5", out var unkeyed, out _));
        Assert.False(VerbCatalog.HoldsSecret(unkeyed));
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
    public void ACarriedLinesSecretWordsNeverReachWhatIsWrittenDown()
    {
        string Shown(string text)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
            return VerbCatalog.Redacted(line);
        }

        // A binding's target after TO, BIND LEARN's own words, a JSON argument's target, a SET's quoted value: each line they carry is
        // shown with its own secret words kept back, the carrying words as they came.
        Assert.Equal("BIND ADD MIDI NOTE 60 TO LINK PATTERNS 10.0.0.5 \u2039secret\u203a", Shown("BIND ADD MIDI NOTE 60 TO LINK PATTERNS 10.0.0.5 s3cr3t-token"));
        Assert.Equal("BIND LEARN FROM MIDI CREDENTIAL SET pj1 \u2039secret\u203a", Shown("BIND LEARN FROM MIDI CREDENTIAL SET pj1 admin hunter2"));
        Assert.Equal("BIND LEARN AUTH \u2039secret\u203a", Shown("BIND LEARN AUTH abcdef"));
        var json = Shown("BIND ADD {\"input\":\"midi:note:60\",\"target\":\"LINK STAGEWATCH 10.0.0.6 key-123\"}");
        Assert.DoesNotContain("key-123", json, StringComparison.Ordinal);
        Assert.True(WireGrammar.TryParse(json, out var shownJson, out _));
        using (var shownArgument = System.Text.Json.JsonDocument.Parse(shownJson.Word(2)))
        {
            Assert.Equal("midi:note:60", shownArgument.RootElement.GetProperty("input").GetString());
            Assert.Equal("LINK STAGEWATCH 10.0.0.6 \u2039secret\u203a", shownArgument.RootElement.GetProperty("target").GetString());
        }
        var set = Shown("SET bd00000001 target \"LINK PATTERNS 10.0.0.5 s3cr3t-token\"");
        Assert.Equal("SET bd00000001 target \"LINK PATTERNS 10.0.0.5 \u2039secret\u203a\"", set);
        Assert.True(WireGrammar.TryParse(set, out var shownSet, out _));
        Assert.Equal(4, shownSet.Count);
        // A line carried inside a carried line is kept back too; a request id stays where it was.
        Assert.DoesNotContain("hunter2", Shown("@b1 BIND ADD OSC /x TO \"CREDENTIAL SET pj1 admin hunter2\""), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", Shown("SET bd00000001 target {\"a\":[\"LINK PATTERNS h s3cr3t\"]}"), StringComparison.Ordinal);

        // A hot backup is given its primary's token by CREDENTIAL SET node.<id>: the ref stays, the token does not, carried or not.
        Assert.Equal("CREDENTIAL SET node.0badc0de \u2039secret\u203a", Shown("CREDENTIAL SET node.0badc0de K7QM-3XWD-P9RA"));
        Assert.Equal("BIND ADD OSC /x TO CREDENTIAL SET node.0badc0de \u2039secret\u203a", Shown("BIND ADD OSC /x TO CREDENTIAL SET node.0badc0de K7QM-3XWD-P9RA"));

        // A carried line holding no secret, and a carrying line with none, are written as they came.
        Assert.Equal("BIND ADD MIDI NOTE 60 TO PLAYLIST GO pl00000001", Shown("BIND ADD MIDI NOTE 60 TO PLAYLIST GO pl00000001"));
        Assert.Equal("SET pl00000001 name \"Show two\"", Shown("SET pl00000001 name \"Show two\""));
        Assert.Equal("BIND ADD {\"input\":\"midi:note:60\",\"target\":\"BLACKOUT ON\"}", Shown("BIND ADD {\"input\":\"midi:note:60\",\"target\":\"BLACKOUT ON\"}"));
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
    public void InsideQuotesABackslashEscapesTheMarkAfterItAsPatternsReadsIt()
    {
        // \" a quote, \\ a backslash, any other mark read alone; a bare word keeps every backslash.
        Assert.True(WireGrammar.TryParse(@"MEDIA IMPORT ""C:\Users\me\clip.mp4"" ""a\""b"" ""a\\b"" C:\bare\path.mp4", out var line, out _));
        Assert.Equal(["MEDIA", "IMPORT", "C:Usersmeclip.mp4", "a\"b", @"a\b", @"C:\bare\path.mp4"], line.Words);
        // The phrase as the line spells it between its quotes, its escapes not undone; none for a bare word, JSON or no word at all.
        Assert.Equal(@"C:\Users\me\clip.mp4", line.QuotedText(2));
        Assert.Equal(@"a\""b", line.QuotedText(3));
        Assert.Equal(@"a\\b", line.QuotedText(4));
        Assert.Null(line.QuotedText(5));
        Assert.Null(line.QuotedText(0));
        Assert.Null(line.QuotedText(6));
        Assert.Null(WireGrammar.QuotedText(@"SET sf1 label {""a"":""\u00e9""}", 3));
        Assert.Null(WireGrammar.QuotedText(@"MEDIA IMPORT ""C:\open", 2));
        // A backslash is lost only before a mark that needs no escape.
        Assert.True(WireGrammar.DropsBackslash(@"C:\Users\me\clip.mp4"));
        Assert.True(WireGrammar.DropsBackslash(@"\\server\share"));
        Assert.False(WireGrammar.DropsBackslash(@"a\""b"));
        Assert.False(WireGrammar.DropsBackslash(@"\\\\server\\share"));
        Assert.False(WireGrammar.DropsBackslash("plain words"));
    }

    [Fact]
    public void EveryPathQuotedAsProducersQuoteItReadsBackAsTyped()
    {
        // The desk's pickers and drops, the Companion module, the assistant's recipes and the node's own lines write a word through
        // Quote: bare when it holds no space or quote, else quoted with each backslash and quote escaped.
        foreach (var path in new[]
        {
            @"C:\Users\me\clip.mp4", @"C:\My clips\take 1.mp4", @"\\server\share\a b.mp4", @"C:\My clips\", @"D:\""quoted""\x.mp4",
            "/home/me/My clips/a.mp4", @"/home/me/a\b c.mp4", "",
        })
        {
            var text = "MEDIA IMPORT " + WireGrammar.Quote(path) + " Take";
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), $"{text}: {error?.Reason}");
            Assert.Equal(["MEDIA", "IMPORT", path, "Take"], line.Words);
            Assert.False(line.QuotedText(2) is { } quoted && WireGrammar.DropsBackslash(quoted), text);
            Assert.Null(VerbSyntax.DroppedBackslash(VerbCatalog.Find("MEDIA IMPORT")!, line, 2));
        }
        Assert.Equal(@"C:\Users\me\clip.mp4", WireGrammar.Quote(@"C:\Users\me\clip.mp4"));
        Assert.Equal(@"""C:\\My clips\\take 1.mp4""", WireGrammar.Quote(@"C:\My clips\take 1.mp4"));
    }

    [Fact]
    public void TheRowsThatTakeAPathAreTheFileVerbsAndOnlyTheirPathIsHeldToItsSpelling()
    {
        Assert.Equal(["MEDIA IMPORT", "PROJECT SAVE", "PROJECT OPEN", "FX IMPORT", "LED IMPORT", "SITE POINTS", "SURVEY IMPORT"],
            VerbCatalog.All.Where(VerbSyntax.TakesPath).Select(v => v.Name));
        var import = VerbCatalog.Find("MEDIA IMPORT")!;
        Assert.True(WireGrammar.TryParse(@"MEDIA IMPORT ""C:\My clips\a.mp4"" ""Act\1""", out var typed, out _));
        Assert.Equal(@"MEDIA IMPORT: inside quotes a backslash escapes the mark after it, so ""C:\My clips\a.mp4"" reads as C:My clipsa.mp4 — write the path ""C:\\My clips\\a.mp4"", each \ doubled inside the quotes, or with / in place of each \",
            VerbSyntax.DroppedBackslash(import, typed, 2));
        // The name after the path is the operator's own word, and a row that takes no path reads its quoted words as the grammar does.
        Assert.True(WireGrammar.TryParse(@"MEDIA IMPORT C:\clips\a.mp4 ""Act\1""", out var named, out _));
        Assert.Null(VerbSyntax.DroppedBackslash(import, named, 2));
        Assert.True(WireGrammar.TryParse(@"PLAYLIST NEW ""AC\DC""", out var playlist, out _));
        Assert.Null(VerbSyntax.DroppedBackslash(VerbCatalog.Find("PLAYLIST NEW")!, playlist, 2));
        // A JSON value is read raw by its verb, whatever escapes its string holds.
        Assert.True(WireGrammar.TryParse(@"SET sf1 label ""Caf\u00e9\n""", out var set, out _));
        Assert.Null(VerbSyntax.DroppedBackslash(VerbCatalog.Find("SET")!, set, 1));
        Assert.Equal(@"""Caf\u00e9\n""", set.RestAfter(3));
    }

    [Fact]
    public void ABindingsTargetIsHeldToItsPathsSpellingAsTheLineItself()
    {
        // A control fires its target later, rebuilt from the words the grammar read, so a target whose path lost its backslashes is no
        // target; the line that carries one is refused as that path's own verb refuses it, however it carries it.
        const string Typed = @"C:\leds\a.csv";
        const string Refusal = @"LED IMPORT: inside quotes a backslash escapes the mark after it, so ""C:\leds\a.csv"" reads as C:ledsa.csv — write the path bare, C:\leds\a.csv, or with / in place of each \";
        Assert.Null(TargetLine.Parse($"LED IMPORT \"{Typed}\" SCALE *", out var why));
        Assert.Equal(Refusal, why);
        Assert.Null(TargetLine.Parse($"PROJECT SAVE \"{Typed}\"", out why));
        Assert.StartsWith("PROJECT SAVE: inside quotes a backslash escapes the mark after it", why, StringComparison.Ordinal);
        foreach (var text in new[]
        {
            $"LED IMPORT \"{Typed}\" SCALE *",
            $"BIND ADD MIDI CC 1 7 TO LED IMPORT \"{Typed}\" SCALE *",
            $"BIND LEARN LED IMPORT \"{Typed}\" SCALE *",
            $"BIND LEARN FROM MIDI LED IMPORT \"{Typed}\"",
            """BIND ADD {"input":"MIDI CC 1 7","target":"LED IMPORT \"C:\\leds\\a.csv\" SCALE *"}""",
        })
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
            Assert.Equal(Refusal, VerbSyntax.DroppedBackslash(line));
        }

        // Written as a producer writes it, the target is bound and the line it fires reads the path as typed.
        const string Spaced = @"C:\My leds\a.csv";
        var written = TargetLine.Parse("LED IMPORT " + WireGrammar.Quote(Spaced) + " SCALE *", out why);
        Assert.True(written is not null, why);
        Assert.True(WireGrammar.TryParse(written.Fill(0.5), out var fired, out _));
        Assert.Equal(["LED", "IMPORT", Spaced, "SCALE", "0.5"], fired.Words);
        Assert.True(WireGrammar.TryParse("BIND ADD MIDI CC 1 7 TO LED IMPORT " + WireGrammar.Quote(Typed) + " SCALE *", out var bound, out _));
        Assert.Null(VerbSyntax.DroppedBackslash(bound));
        // A carried line of a row that takes no path reads its quoted words as the grammar does.
        Assert.True(WireGrammar.TryParse(@"BIND ADD MIDI CC 1 8 TO PLAYLIST NEW ""AC\DC""", out var named, out _));
        Assert.Null(VerbSyntax.DroppedBackslash(named));
    }

    [Fact]
    public void ARefusedPathNeverRepeatsAPhraseThatReadsAsALineHoldingASecret()
    {
        // The journal keeps such a phrase back (VerbCatalog.Redacted); the refusal, which rides the reply and the row's words, does too.
        var import = VerbCatalog.Find("MEDIA IMPORT")!;
        Assert.True(WireGrammar.TryParse(@"MEDIA IMPORT ""AUTH s3cret\x"" Take", out var line, out _));
        Assert.NotEqual(line.Raw, VerbCatalog.Redacted(line));
        Assert.Equal(@"MEDIA IMPORT: inside quotes a backslash escapes the mark after it, so the quoted path loses a backslash — write the path with each \ doubled inside the quotes, bare when it holds no space or quote, or with / in place of each \",
            VerbSyntax.DroppedBackslash(import, line, 2));
        Assert.Null(TargetLine.Parse(@"MEDIA IMPORT ""AUTH s3cret\x""", out var why));
        Assert.DoesNotContain("s3cret", why, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotedJsonThatLosesABackslashIsToldThatJsonRidesBare()
    {
        // FX IMPORT takes a path or JSON; JSON quoted as a phrase loses the escapes its strings hold, and is never a path to respell.
        Assert.True(WireGrammar.TryParse(@"FX IMPORT ""{\""fragment\"":\""a\nb\""}""", out var line, out _));
        Assert.Equal("""{"fragment":"anb"}""", line.Word(2));
        Assert.Equal("FX IMPORT: inside quotes a backslash escapes the mark after it, so quoted JSON loses the escapes its strings hold — a JSON argument rides bare, unquoted, to the end of the line",
            VerbSyntax.DroppedBackslash(VerbCatalog.Find("FX IMPORT")!, line, 2));
        // MEDIA IMPORT takes no JSON: a phrase that opens like JSON there is a path like any other.
        Assert.True(WireGrammar.TryParse(@"MEDIA IMPORT ""[old]\a.mp4""", out var path, out _));
        Assert.Contains(@"write the path ""[old]\\a.mp4"", each \ doubled inside the quotes,", VerbSyntax.DroppedBackslash(VerbCatalog.Find("MEDIA IMPORT")!, path, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteThatDoesNotCloseBecauseABackslashEscapedItSaysHowAPhraseEndsInABackslash()
    {
        // A folder typed as Explorer gives it ends in a backslash, which escapes the closing quote.
        Assert.False(WireGrammar.TryParse(@"PROJECT SAVE ""C:\shows\""", out _, out var error));
        Assert.Equal(@"a quote that does not close — inside quotes \"" is a quote mark, never the phrase's end, so a phrase that ends in a backslash doubles it (""C:\\shows\\"")",
            error!.Reason);
        Assert.False(WireGrammar.TryParse(@"PROJECT SAVE ""C:\shows\"" NOW", out _, out error));
        Assert.StartsWith(@"a quote that does not close — inside quotes \"" is a quote mark", error!.Reason, StringComparison.Ordinal);
        // An open quote with no escaped quote in it says only that.
        Assert.False(WireGrammar.TryParse(@"SAY ""unclosed \\", out _, out error));
        Assert.Equal("a quote that does not close", error!.Reason);
        Assert.True(WireGrammar.TryParse(@"PROJECT SAVE ""C:\\shows\\""", out var closed, out _));
        Assert.Equal(@"C:\shows\", closed.Word(2));
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

    /// <summary>Show Bus 1.3 §13.5.1: a token is compared in its canonical form — '-' and every listed space and separator dropped, a–z
    /// upper-cased, nothing else touched — and one typed for a peer is taken only when that form is letters and digits with nothing a 1.1
    /// desk reads apart (§13.6.8), refused in the contract's own words otherwise, the token never in them.</summary>
    [Fact]
    public void ATokenIsComparedInItsCanonicalFormAndOneTypedIsTakenOnlyAsLettersAndDigits()
    {
        Assert.Equal("K7QM3XWDP9RA", Pairing.Normalise("k7qm-3xwd p9ra"));
        Assert.Equal("K7QM3XWDP9RA", Pairing.Normalise("k7qm\u00a03xwd\u2009p9ra"));
        Assert.Equal("K7QM3XWDP9RA", Pairing.Normalise("\ufeffK7QM-3XWD-P9RA\u0085\u3000"));
        Assert.Equal("K7QM3XWDP9RÄ", Pairing.Normalise("k7qm-3xwd-p9rÄ"));   // only a–z are upper-cased; anything else stays itself
        Assert.Equal("K7QMä", Pairing.Normalise("k7qmä"));
        Assert.Equal("", Pairing.Normalise(null));
        Assert.Equal("K7QM3XWDP9RA", Pairing.Canon("k7qm-3xwd p9ra"));
        Assert.Null(Pairing.Canon("k7qm\u20113xwd"));
        Assert.Null(Pairing.Canon("Bühne-7_a!"));
        Assert.Null(Pairing.Canon(new string('A', 65)));

        Assert.Null(Pairing.Refusal("k7qm-3xwd p9ra"));
        Assert.Null(Pairing.Refusal(Pairing.NewToken()));
        Assert.Null(Pairing.Refusal(""));
        Assert.Null(Pairing.Refusal(null));
        Assert.Equal(Pairing.InvisibleCharacter, Pairing.Refusal("k7qm\u00a03xwd-p9ra"));
        Assert.Equal(Pairing.InvisibleCharacter, Pairing.Refusal("\ufeffK7QM-3XWD-P9RA"));
        Assert.Equal(Pairing.NotLettersAndDigits, Pairing.Refusal("Bühne-7_a!"));
        Assert.Equal(Pairing.NotLettersAndDigits, Pairing.Refusal("k7qm\u20113xwd"));
        Assert.Equal(Pairing.NotLettersAndDigits, Pairing.Refusal("---"));
        Assert.DoesNotContain("K7QM", Pairing.Refusal("K7QM\u00a03XWD")!, StringComparison.OrdinalIgnoreCase);

        // §7's proof keys a token as 1.1 desks do: the same for every token of letters, digits, dashes and spaces.
        const string nonce = "00112233445566778899aabbccddeeff";
        Assert.Equal(PeerLink.LegacyProof("k7qm-3xwd p9ra", nonce, "Stagewatch"), Pairing.Proof("k7qm-3xwd p9ra", nonce, "Stagewatch"));
        Assert.Equal(PeerLink.LegacyProof("Bühne-7_a!", nonce, "Stagewatch"), Pairing.Proof("Bühne-7_a!", nonce, "Stagewatch"));
    }

    [Fact]
    public void ThePairingProofHasAKnownAnswer()
    {
        // The vector the Companion module pins too: HMAC-SHA256(key = "K7QM3XWDP9RA", message = nonce + name), lowercase hex.
        Assert.Equal("b07c009ef4196cc819f65ae7b624f6f7eff03ea67f0d73b99a634e8579fa1d3d",
            Pairing.Proof("K7QM-3XWD-P9RA", "00112233445566778899aabbccddeeff", "Companion:FOH deck"));
        Assert.True(Pairing.Verify("K7QM-3XWD-P9RA", "00112233445566778899aabbccddeeff", "Companion:FOH deck",
            "b07c009ef4196cc819f65ae7b624f6f7eff03ea67f0d73b99a634e8579fa1d3d"));
        // The order is nonce then name: the other order is another proof.
        Assert.NotEqual(Pairing.Proof("K7QM-3XWD-P9RA", "00112233445566778899aabbccddeeff", "Companion:FOH deck"),
            Pairing.Proof("K7QM-3XWD-P9RA", "Companion:FOH deck", "00112233445566778899aabbccddeeff"));
    }

    /// <summary>A limit includes itself (Show Bus 1.3 §13): a peer silent 6000 ms is live, 6001 stale, 12000 stale, 12001 gone; and the
    /// redial waits 13.15.1's wait, the PeerLink core's.</summary>
    [Fact]
    public void LivenessAndRedialFollowTheContract()
    {
        Assert.Equal(PeerLiveness.Live, Liveness.Of(5999));
        Assert.Equal(PeerLiveness.Live, Liveness.Of(6000));
        Assert.Equal(PeerLiveness.Stale, Liveness.Of(6001));
        Assert.Equal(PeerLiveness.Stale, Liveness.Of(12000));
        Assert.Equal(PeerLiveness.Gone, Liveness.Of(12001));
        Assert.InRange(Liveness.RedialMs(0, 0.5), 400, 600);
        Assert.InRange(Liveness.RedialMs(10, 1), 5000, 6000);
        foreach (var (n, j) in new[] { (0, 0.0), (0, 0.5), (3, 0.25), (10, 0.9999999) })
            Assert.Equal(PeerLink.RedialWait(n, j), Liveness.RedialMs(n, j));
    }
}
