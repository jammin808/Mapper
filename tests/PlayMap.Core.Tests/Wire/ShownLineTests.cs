using System.Text;
using PlayMap.Core.Media;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Wire;

/// <summary>A line as it is written down (<see cref="VerbCatalog.Redacted(WireLine)"/>) and a line's risk (<see cref="VerbCatalog.RiskOf"/>)
/// on what a sender may put in one line: lines carried inside lines however deep, JSON a reader takes with a key given twice, words parted by
/// any white space, addresses that carry a pass (<see cref="PrivateAddress"/>, on Patterns' own table); each read in time a 64 KiB line
/// cannot run away with.</summary>
public sealed class ShownLineTests
{
    private const string Token = "s3cr3t-7f2a";

    private static WireLine Parsed(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return line;
    }

    private static string Shown(string text) => VerbCatalog.Redacted(Parsed(text));

    /// <summary>A line as long as the wire reads, made of <paramref name="piece"/> again and again, ending with <paramref name="end"/>.</summary>
    private static string Longest(string piece, string end)
    {
        var sb = new StringBuilder();
        while (sb.Length + piece.Length + end.Length < WireGrammar.MaxLineBytes)
            sb.Append(piece);
        return sb.Append(end).ToString();
    }

    [Fact]
    public async Task ALineOfBindingsCarryingBindingsIsWrittenDownInOnePassOverItsWords()
    {
        // Each TO of a binding starts a line it may carry, and each of those may carry the rest again: written down, every such start is
        // read once, so the longest line the wire reads is shown at once, with its secret kept back however deep it rides.
        var plain = Longest("BIND ADD OSC /x TO ", "PLAYLIST GO pl0a1b2c3d");
        var keyed = Longest("BIND ADD OSC /x TO ", "LINK PATTERNS 10.0.0.5 " + Token);
        var learned = Longest("BIND LEARN ", "AUTH " + Token);
        var shown = await Task.Run(() => (VerbCatalog.Redacted(Parsed(plain)), VerbCatalog.Redacted(Parsed(keyed)), VerbCatalog.Redacted(Parsed(learned))))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(plain, shown.Item1);
        Assert.EndsWith("LINK PATTERNS 10.0.0.5 " + VerbCatalog.SecretMarker, shown.Item2, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, shown.Item2, StringComparison.Ordinal);
        Assert.EndsWith("BIND LEARN AUTH " + VerbCatalog.SecretMarker, shown.Item3, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, shown.Item3, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinesRiskIsReadInWorkTheLongestLineCannotRunAwayWith()
    {
        // The risk of a line carrying lines is the highest of every line it could be read to carry; past what is worth reading, a line is
        // high (it then waits for a person), never read on and on.
        var plain = Longest("BIND ADD OSC /x TO ", "PLAYLIST GO pl0a1b2c3d");
        var risk = await Task.Run(() => VerbCatalog.RiskOf(Parsed(plain))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(VerbRisk.High, risk);
        // A binding of an ordinary depth keeps its carried line's risk.
        Assert.Equal(VerbRisk.Review, VerbCatalog.RiskOf(Parsed("BIND ADD OSC /a TO BIND ADD OSC /b TO SET sf0a0b0c01 opacity *")));
    }

    [Fact]
    public void ALineCarriedFiveDeepInBindLearnsWordsKeepsItsSecretBack()
    {
        Assert.Equal("BIND LEARN BIND LEARN BIND LEARN BIND LEARN BIND LEARN AUTH " + VerbCatalog.SecretMarker,
            Shown($"BIND LEARN BIND LEARN BIND LEARN BIND LEARN BIND LEARN AUTH {Token}"));
        Assert.Equal("BIND LEARN FROM MIDI BIND LEARN FROM OSC BIND LEARN LINK STAGEWATCH 10.0.0.6 " + VerbCatalog.SecretMarker,
            Shown($"BIND LEARN FROM MIDI BIND LEARN FROM OSC BIND LEARN LINK STAGEWATCH 10.0.0.6 {Token}"));
    }

    [Fact]
    public void AJsonArgumentGivingAKeyTwiceIsWrittenDownAsAReaderTakesIt()
    {
        // The node's JSON reader takes a key given twice; so does what writes the line down: each of its strings is read, none faults.
        var twice = Shown($"BIND ADD {{\"input\":\"OSC /x\",\"target\":\"BLACKOUT ON\",\"target\":\"LINK PATTERNS 10.0.0.5 {Token}\"}}");
        Assert.DoesNotContain(Token, twice, StringComparison.Ordinal);
        Assert.StartsWith("BIND ADD {\"input\":\"OSC /x\",\"target\":\"BLACKOUT ON\",\"target\":\"LINK PATTERNS 10.0.0.5 ", twice, StringComparison.Ordinal);
        Assert.Equal("ADD surface {\"name\":\"a\",\"name\":\"b\"}", Shown("ADD surface {\"name\":\"a\",\"name\":\"b\"}"));
        // JSON cut short, or with comments, is read as the reader reads its strings: a secret in one is kept back, the rest as it came.
        var cut = Shown($"SET bd0a1b2c3d target {{\"a\": /* \" */ \"AUTH {Token}");
        Assert.DoesNotContain(Token, cut, StringComparison.Ordinal);
        Assert.StartsWith("SET bd0a1b2c3d target {\"a\": /* \" */ ", cut, StringComparison.Ordinal);
        Assert.Equal("SET bd0a1b2c3d target {\"a\": /* AUTH x */ 1}", Shown("SET bd0a1b2c3d target {\"a\": /* AUTH x */ 1}"));
    }

    [Theory]
    [InlineData("SET bd0a1b2c3d target \"AUTH\t" + Token + "\"")]
    [InlineData("SET bd0a1b2c3d target \"AUTH " + Token + "\"")]
    [InlineData("SET bd0a1b2c3d target \"\\\"AUTH\\\"" + Token + "\"")]
    [InlineData("BIND ADD OSC /x TO SET bd0a1b2c3d target \"LINK PATTERNS 0.0.0.0 " + Token + "\"")]
    public void AQuotedWordPartedByAnyWhiteSpaceOrAQuoteIsReadAsTheLineItIs(string text)
    {
        // The grammar parts words on any white space, and a quoted phrase ends where its quote closes: a word that reads as a line holding
        // a secret, whatever parts its words, is written down without it.
        var shown = Shown(text);
        Assert.DoesNotContain(Token, shown, StringComparison.Ordinal);
        Assert.Contains(VerbCatalog.SecretMarker, shown, StringComparison.Ordinal);
    }

    /// <summary>26 of Stagewatch's window alphabet — a window's pass (Patterns' table's own).</summary>
    private const string Window = "q3x7m2k9p4abcdefghjk5n6r8t";

    /// <summary>The private rule, row for row Patterns' table (<c>WindowAddressesTests.ThePrivateRuleReadsTheAddressAlone</c>): what
    /// PlayMap refuses to keep is what Patterns refuses to publish.</summary>
    [Theory]
    [InlineData("http://10.0.0.5:9924/w/" + Window, true)]
    [InlineData("http://10.0.0.5:9924/w/" + Window + "/", true)]
    [InlineData("https://hall-a.local/w/" + Window + "?embed=1", true)]
    [InlineData("10.0.0.5:9924/w/" + Window, true)]                                  // a bare host, as a browser opens it
    [InlineData("http://10.0.0.5:9924/W/Q3X7M2K9P4ABCDEFGHJK5N6R8T", true)]          // case-blind, failing closed
    [InlineData("http://10.0.0.5:9924/w/q3x7m2k9p4abcdefghjk5n6r8", false)]         // 25 symbols
    [InlineData("http://10.0.0.5:9924/w/q3x7m2k9p4abcdefghjk5n6rl", false)]         // an l is not in the alphabet
    [InlineData("http://10.0.0.5:9924/w/q3x7m2k9p4abcdefghjk5n6r8tx", false)]       // 27: another path
    [InlineData("https://hall-a.local/stage?view=k2m4", true)]
    [InlineData("https://x/feed?pin=4321", true)]
    [InlineData("https://x/feed?a=1&PIN=4321", true)]
    [InlineData("https://x/feed?%70in=4321", true)]                                  // a percent-encoded name
    [InlineData("https://x/app#token=abc", true)]
    [InlineData("https://x/app#/route?access_token=abc", true)]
    [InlineData("https://user:pass@x/page", true)]
    [InlineData("https://x/feed?api_key=abc", true)]
    [InlineData("https://www.example.com/schedule?day=2", false)]
    [InlineData("https://x/feed?viewport=1&keyboard=on", false)]                     // whole words only
    [InlineData("https://x/feed?pin", false)]                                        // a word with no value carries nothing
    [InlineData("https://x/@speaker", false)]                                        // an @ in the path is not a user
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData("file:///C:/show/w/" + Window, false)]
    [InlineData("C:\\show\\schedule.html?pin=1", false)]
    [InlineData("kept://10.0.0.5:9924/abcdefghijkl", false)]
    [InlineData("unkept://10.0.0.5:9924", false)]
    [InlineData("https://h:99999/w/" + Window, true)]                                 // a port no browser opens still fails closed
    [InlineData("ndi:MEDIA-PC (OBS@Main)", false)]                                   // a source's name: "Main)" is no host
    [InlineData("cap:Cam (USB@2)", false)]
    [InlineData("Timer @ FOH", false)]                                               // words: white space in the authority
    [InlineData("https://Timer @ FOH/w/" + Window, false)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42", false)]
    [InlineData("https://bbc.co.uk/news/w/live", false)]
    public void ThePrivateRuleReadsTheAddressAloneAsPatternsDoes(string url, bool expected)
    {
        Assert.Equal(expected, PrivateAddress.Looks(url));
    }

    /// <summary>A token written without a scheme, row for row Patterns' table (<c>WindowAddressesTests.ABareTokenIsPrivateOnlyBeyondDoubt</c>).</summary>
    [Theory]
    [InlineData("10.0.0.5:9924/w/" + Window, false, true)]
    [InlineData("hall-b.local/stage?view=k2m4&lang=en", false, true)]
    [InlineData("[fe80::1]:9924/w/" + Window, false, true)]
    [InlineData("admin:pw@10.0.0.5", false, false)]                                // a user is not enough in words…
    [InlineData("admin:pw@10.0.0.5", true, true)]                                  // …where the token can be nothing but a page, a password is
    [InlineData("Lobby@FOH", true, false)]                                         // a nickname: a user with no password
    [InlineData("19:30@foyer", false, false)]
    [InlineData("ops@venue.example", true, false)]
    [InlineData("Timer @ FOH", true, false)]
    [InlineData("https://x/feed?pin=1", true, false)]                              // a scheme: not bare
    [InlineData("C:\\show\\schedule.html?pin=1", true, false)]                     // a file
    [InlineData("/w/" + Window, true, false)]                                       // no host
    [InlineData("web:10.0.0.5:9924/w/" + Window, true, false)]
    [InlineData("example.com/schedule?day=2", true, false)]
    public void ABareTokenIsPrivateOnlyBeyondDoubtAsPatternsReadsIt(string token, bool userWithPassword, bool expected)
    {
        Assert.Equal(expected, PrivateAddress.LooksBare(token, userWithPassword));
    }

    [Theory]
    [InlineData("MEDIA IMPORT https://10.0.0.5:9924/w/" + Window + " Stage", "MEDIA IMPORT \"‹private address›\" Stage")]
    [InlineData("MEDIA IMPORT 10.0.0.5:9924/w/" + Window, "MEDIA IMPORT \"‹private address›\"")]
    [InlineData("MEDIA IMPORT https://hall-a.local/stage?view=k2m4&lang=en", "MEDIA IMPORT \"‹private address›\"")]
    [InlineData("INPUT ADD rtsp://admin:pw@10.0.0.9/stream1 Cam", "INPUT ADD \"‹private address›\" Cam")]
    [InlineData("BIND ADD OSC /x TO MEDIA IMPORT https://h/w/" + Window, "BIND ADD OSC /x TO MEDIA IMPORT \"‹private address›\"")]
    [InlineData("SET md0a1b2c3d name \"Stage https://h/w/" + Window + "\"", "SET md0a1b2c3d name \"Stage \\\"‹private address›\\\"\"")]
    [InlineData("ADD media {\"name\":\"Stage\",\"location\":\"https://x/feed?pin=4321\"}", "ADD media {\"name\":\"Stage\",\"location\":\"‹private address›\"}")]
    [InlineData("WHATEVER https://x/feed?pin=4321", "WHATEVER \"‹private address›\"")]
    [InlineData("LINK STAGEWATCH 10.0.0.5:9924/w/" + Window + " " + Token, "LINK STAGEWATCH \"‹private address›\" ‹secret›")]
    [InlineData("MEDIA IMPORT (https://10.0.0.5:9924/w/" + Window + ")", "MEDIA IMPORT \"‹private address›\"")]
    [InlineData("MEDIA IMPORT <https://hall-a.local/w/" + Window + ">", "MEDIA IMPORT \"‹private address›\"")]
    [InlineData("MEDIA IMPORT Stage:https://hall-a.local/stage?view=k2m4", "MEDIA IMPORT \"‹private address›\"")]
    [InlineData("MEDIA IMPORT (10.0.0.5:9924/w/" + Window + ")", "MEDIA IMPORT \"‹private address›\"")]
    [InlineData("MEDIA IMPORT [fe80::1]:9924/w/" + Window, "MEDIA IMPORT \"‹private address›\"")]
    [InlineData("MEDIA IMPORT /shows/a.mov \"Lobby (https://h/w/" + Window + ")\"", "MEDIA IMPORT /shows/a.mov \"Lobby \\\"‹private address›\\\"\"")]
    [InlineData("SET md0a1b2c3d name \"Stage:10.0.0.5:9924/w/" + Window + "\"", "SET md0a1b2c3d name \"‹private address›\"")]
    [InlineData("WHATEVER \"Lobby (https://x/feed?pin=4321)\"", "WHATEVER \"‹private address›\"")]
    [InlineData("MEDIA IMPORT https://x/login?next=https://admin:pw@10.0.0.9/", "MEDIA IMPORT \"‹private address›\"")]
    public void AnAddressThatCarriesAPassIsWrittenDownAsTheMarkerWhereverItRides(string text, string expected)
    {
        // A window's token, a PIN, a viewing code or a user: never in a journal row, a log, a proposal or a refusal's line, whether the
        // address is the line's own word, a word of a line it carries, a word of a phrase or a string of its JSON, and whatever
        // punctuation is glued to it (brackets, a label and its colon, a sentence's full stop) or rides in its query.
        var shown = Shown(text);
        Assert.Equal(expected, shown);
        Assert.DoesNotContain(Window, shown, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("k2m4", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(":pw@", shown, StringComparison.Ordinal);
        Assert.True(WireGrammar.TryParse(shown, out _, out _), shown);
    }

    [Theory]
    [InlineData("MEDIA IMPORT https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42 ops@venue")]
    [InlineData("MEDIA IMPORT https://bbc.co.uk/news/w/live \"Doors 19:30@foyer\"")]
    [InlineData("INPUT ADD \"ndi:MEDIA-PC (OBS@Main)\" Lobby@FOH")]
    [InlineData("MEDIA IMPORT /shows/w/" + Window + ".mov")]
    [InlineData("SET md0a1b2c3d name \"Timer @ FOH\"")]
    public void AWordThatCarriesNoPassIsWrittenDownAsItCame(string text)
    {
        // A name with an @, a time, a page with no pass, a file whose folder is named w: the rule reads the marks of a pass, nothing else.
        Assert.Equal(text, Shown(text));
    }

    /// <summary>A name as the scrub keeps it: each address that carries a pass found inside its word as Patterns finds one in free text
    /// (the scheme read back from its <c>://</c>, a bare host from past leading punctuation or a glued label, trailing punctuation
    /// dropped), shown as its host and the marker, every other character kept.</summary>
    [Theory]
    [InlineData("Lobby (https://10.0.0.5:9924/w/" + Window + ")", "Lobby (10.0.0.5 ‹private address›)")]
    [InlineData("<https://hall-a.local/stage?view=k2m4>", "<hall-a.local ‹private address›>")]
    [InlineData("Stage:https://h/w/" + Window + ".", "Stage:h ‹private address›.")]
    [InlineData("Cam (10.0.0.5:9924/w/" + Window + "),", "Cam (10.0.0.5 ‹private address›),")]
    [InlineData("Stage:10.0.0.5:9924/w/" + Window, "Stage:10.0.0.5 ‹private address›")]
    [InlineData("x[fe80::1]:9924/w/" + Window, "x[fe80::1] ‹private address›")]
    [InlineData("https://x/login?next=https://admin:pw@10.0.0.9/", "https://x/login?next=10.0.0.9 ‹private address›")]
    [InlineData("10.0.0.5:9924/w/" + Window + ",https://x/", "10.0.0.5 ‹private address›,https://x/")]
    [InlineData("ops@venue, Doors 19:30@foyer (Main)", "ops@venue, Doors 19:30@foyer (Main)")]
    [InlineData("(https://www.example.com/schedule)", "(https://www.example.com/schedule)")]
    [InlineData("Doors:19:30@foyer", "Doors:19:30@foyer")]
    [InlineData("/shows/w/" + Window + ".mov", "/shows/w/" + Window + ".mov")]
    [InlineData("(/shows/w/" + Window + ".mov)", "(/shows/w/" + Window + ".mov)")]
    [InlineData("C:\\show\\w\\" + Window + ".mov", "C:\\show\\w\\" + Window + ".mov")]
    public void AnAddressGluedToPunctuationIsFoundInsideItsWord(string text, string expected)
    {
        Assert.Equal(expected, PrivateAddress.ShownText(text));
        var changed = !string.Equals(expected, text, StringComparison.Ordinal);
        Assert.Equal(changed, PrivateAddress.InText(text));
        Assert.Equal(changed, PrivateAddress.IsWord(text));
        if (!changed)
            Assert.Same(text, PrivateAddress.ShownText(text));
    }

    [Theory]
    [InlineData("(https://10.0.0.5:9924/w/" + Window + ")", true, true)]
    [InlineData("Stage:10.0.0.5:9924/w/" + Window, true, true)]
    [InlineData("<https://hall-a.local/stage?view=k2m4>", false, true)]
    [InlineData("rtsp://admin:pw@10.0.0.9/stream1", false, false)]                // a user is a stream's only pass: it plays from it
    [InlineData("(rtsp://admin:pw@10.0.0.9/stream1)", false, false)]
    [InlineData("rtsp://admin:pw@10.0.0.9/stream1?token=4321", false, true)]
    [InlineData("https://www.example.com/schedule", false, false)]
    public void AWindowAndAPassBeyondAUserAreReadInsideTheWord(string word, bool window, bool beyondUser)
    {
        Assert.Equal(window, PrivateAddress.IsStagewatchWindow(word));
        Assert.Equal(beyondUser, PrivateAddress.PassBeyondUser(word));
    }

    [Fact]
    public async Task AWordOfManySchemesIsReadInTimeAndPrivateWholePastTheBoundOnlyWithAPasssMark()
    {
        // Each scheme's address runs to its token's end, so a token is read for at most MaxSchemesInToken of them; past that, a token that
        // bears a pass's mark (an @, a window's /w/, a pass word before an =) is private whole, unread, and one bearing none is a word.
        var under = string.Concat(Enumerable.Repeat("a://b/", PrivateAddress.MaxSchemesInToken)) + "@";
        var over = string.Concat(Enumerable.Repeat("a://b/", PrivateAddress.MaxSchemesInToken + 1)) + "@";
        Assert.False(PrivateAddress.IsWord(under));
        Assert.True(PrivateAddress.IsWord(over));
        Assert.False(PrivateAddress.IsWord(over[..^1]));
        Assert.True(PrivateAddress.PassBeyondUser(over));

        var marked = "MEDIA IMPORT " + Longest("a://", "b/x@h")[13..];
        var plain = "MEDIA IMPORT " + Longest("a://", "b/x.h")[13..];
        var shown = await Task.Run(() => (VerbCatalog.Redacted(Parsed(marked)), VerbCatalog.Redacted(Parsed(plain)))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("MEDIA IMPORT \"" + PrivateAddress.Marker + "\"", shown.Item1);
        Assert.Equal(plain, shown.Item2);
    }

    [Fact]
    public async Task ALineOfBindingsCarryingAPrivateAddressIsWrittenDownInTime()
    {
        var keyed = Longest("BIND ADD OSC /x TO ", "MEDIA IMPORT https://10.0.0.5:9924/w/" + Window);
        var shown = await Task.Run(() => VerbCatalog.Redacted(Parsed(keyed))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.EndsWith("MEDIA IMPORT \"" + PrivateAddress.Marker + "\"", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(Window, shown, StringComparison.Ordinal);
    }
}
