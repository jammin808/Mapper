using System.Text;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Wire;

/// <summary>A line as it is written down (<see cref="VerbCatalog.Redacted(WireLine)"/>) and a line's risk (<see cref="VerbCatalog.RiskOf"/>)
/// on what a sender may put in one line: lines carried inside lines however deep, JSON a reader takes with a key given twice, words parted by
/// any white space; each read in time a 64 KiB line cannot run away with.</summary>
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
    public void AFormTheSyntaxSpellsOutIsWrittenDownWholeAndATokenBesideItIsNot()
    {
        // LINK PATTERNS FOLLOW-BLACKOUT ON|OFF is a keyword and a fixed choice: no word of it is one a secret could be, so it is written
        // down as it came, ON or OFF, in any case and with the verb's name quoted as one word; it holds no secret and is no shown copy.
        foreach (var text in new[]
        {
            "LINK PATTERNS FOLLOW-BLACKOUT ON", "LINK PATTERNS FOLLOW-BLACKOUT OFF", "link patterns follow-blackout off", "\"LINK PATTERNS\" FOLLOW-BLACKOUT ON",
        })
        {
            Assert.Equal(text, Shown(text));
            Assert.False(VerbCatalog.HoldsSecret(Parsed(text)), text);
            Assert.False(VerbCatalog.IsShownCopy(Parsed(text)), text);
        }

        // The same row's token form keeps its token back, and so does a word that is not the switch's own, where ON|OFF stands or after it.
        var keyed = "LINK PATTERNS 10.0.0.5 " + Token;
        Assert.Equal("LINK PATTERNS 10.0.0.5 " + VerbCatalog.SecretMarker, Shown(keyed));
        Assert.Equal("\"LINK PATTERNS\" 10.0.0.5 " + VerbCatalog.SecretMarker, Shown("\"LINK PATTERNS\" 10.0.0.5 " + Token));
        Assert.Equal("LINK PATTERNS FOLLOW-BLACKOUT " + VerbCatalog.SecretMarker, Shown("LINK PATTERNS FOLLOW-BLACKOUT " + Token));
        Assert.Equal("LINK PATTERNS FOLLOW-BLACKOUT " + VerbCatalog.SecretMarker, Shown("LINK PATTERNS FOLLOW-BLACKOUT ON " + Token));

        // Carried inside a line, the switch is written down whole too, and a token beside it is not.
        Assert.Equal("BIND ADD OSC /x TO LINK PATTERNS FOLLOW-BLACKOUT ON", Shown("BIND ADD OSC /x TO LINK PATTERNS FOLLOW-BLACKOUT ON"));
        Assert.Equal("SET bd0a1b2c3d target \"LINK PATTERNS FOLLOW-BLACKOUT OFF\"", Shown("SET bd0a1b2c3d target \"LINK PATTERNS FOLLOW-BLACKOUT OFF\""));
        Assert.Equal("BIND ADD OSC /x TO LINK PATTERNS 10.0.0.5 " + VerbCatalog.SecretMarker, Shown("BIND ADD OSC /x TO " + keyed));

        // Only a line whose token was kept back is a shown copy: the line holding its token runs, the copy never does. A switch's copy
        // with the marker where ON|OFF stands says neither, so it is a copy too.
        Assert.True(VerbCatalog.HoldsSecret(Parsed(keyed)));
        Assert.False(VerbCatalog.IsShownCopy(Parsed(keyed)));
        Assert.True(VerbCatalog.IsShownCopy(Parsed(Shown(keyed))));
        Assert.True(VerbCatalog.IsShownCopy(Parsed("LINK PATTERNS FOLLOW-BLACKOUT " + VerbCatalog.SecretMarker)));
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
}
