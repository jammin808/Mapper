using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Wire;

/// <summary>A line's risk from its words: who must approve an assistant's line. Words may raise a line that moves the air, a
/// transport, a panic or a projector's power, and never lower it.</summary>
public sealed class RiskOfTests
{
    private static VerbRisk Risk(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return VerbCatalog.RiskOf(line);
    }

    [Theory]
    [InlineData("BLACKOUT ON", VerbRisk.High)]
    [InlineData("BLACKOUT OFF", VerbRisk.High)]
    [InlineData("FREEZE ON", VerbRisk.High)]
    [InlineData("DEVICE SHUTTER dv0a0b0c01 CLOSE", VerbRisk.High)]
    [InlineData("DEVICE SHUTTER dv0a0b0c01 OPEN", VerbRisk.High)]
    [InlineData("DEVICE POWER dv0a0b0c01 OFF CONFIRM", VerbRisk.High)]
    [InlineData("MASTER 0", VerbRisk.High)]
    [InlineData("MASTER 100 FADE 500", VerbRisk.High)]
    [InlineData("PANIC", VerbRisk.High)]
    [InlineData("PANIC SAFE ON op_1", VerbRisk.High)]
    [InlineData("PANIC OFF", VerbRisk.High)]
    [InlineData("LED LEVEL 0", VerbRisk.High)]
    [InlineData("LED OUTPUT OFF", VerbRisk.High)]
    [InlineData("BIND ADD MIDI APC40 NOTE 1 53 TO BLACKOUT OFF", VerbRisk.High)]
    [InlineData("BIND ADD OSC /fader/1 TO SET sf0a0b0c01 opacity *", VerbRisk.Review)]
    [InlineData("BIND ADD OSC /go TO PLAYLIST GO pl0a0b0c01", VerbRisk.High)]
    [InlineData("BIND ADD {\"input\":\"OSC /x\",\"target\":\"BLACKOUT ON\"}", VerbRisk.High)]
    [InlineData("BIND ADD {\"input\":\"OSC /x\",\"target\":\"TEMPO TAP\"}", VerbRisk.Review)]
    [InlineData("BIND ADD OSC /x TO BIND ADD OSC /y TO BLACKOUT ON", VerbRisk.High)]
    [InlineData("BIND ADD OSC /x TO NOT A VERB", VerbRisk.Review)]
    // Every reading a binder could take counts: a TO among the input's words, a target key in another case or given twice, a
    // JSON argument with comments; an argument no reader takes is high.
    [InlineData("BIND ADD OSC TO TO BLACKOUT ON", VerbRisk.High)]
    [InlineData("BIND ADD MIDI APC40 NOTE 1 53 TO SURFACE CORNER sf1 TL TO 0.1 0.2", VerbRisk.Review)]
    [InlineData("BIND ADD {\"input\":\"OSC /x\",\"Target\":\"BLACKOUT ON\"}", VerbRisk.High)]
    [InlineData("BIND ADD {\"target\":\"TEMPO TAP\",\"target\":\"BLACKOUT ON\"}", VerbRisk.High)]
    [InlineData("BIND ADD {\"target\":\"BLACKOUT ON\",\"TARGET\":\"TEMPO TAP\"}", VerbRisk.High)]
    [InlineData("BIND ADD {/* a fader */ \"input\":\"OSC /x\",\"target\":\"BLACKOUT ON\",}", VerbRisk.High)]
    [InlineData("BIND ADD {\"input\":\"OSC /x\",\"target\":}", VerbRisk.High)]
    [InlineData("BIND LEARN BLACKOUT ON", VerbRisk.High)]
    [InlineData("BIND LEARN FROM MIDI PANIC", VerbRisk.High)]
    [InlineData("BIND LEARN FROM APC40 TEMPO TAP", VerbRisk.Review)]
    [InlineData("BIND LEARN OFF", VerbRisk.Low)]
    [InlineData("DECODE POLICY DIRECT FORCE", VerbRisk.High)]
    [InlineData("DECODE POLICY DIRECT", VerbRisk.Review)]
    [InlineData("MEDIA OPTIMISE CANCEL", VerbRisk.Low)]
    [InlineData("MEDIA OPTIMISE PAUSE oj0a0b0c01", VerbRisk.Low)]
    [InlineData("MEDIA OPTIMIZE ALL", VerbRisk.Review)]
    [InlineData("TIMECODE SOURCE RESET tc0a0b0c01", VerbRisk.Low)]
    [InlineData("TC SOURCE RESET tc0a0b0c01", VerbRisk.Low)]
    [InlineData("TIMECODE SOURCE REMOVE tc0a0b0c01", VerbRisk.Review)]
    [InlineData("SEND ENABLE sd0a0b0c01 ON", VerbRisk.Review)]
    [InlineData("SEND ENABLE sd0a0b0c01 OFF", VerbRisk.High)]
    [InlineData("ASSISTANT SET enabled true", VerbRisk.High)]
    [InlineData("ASSISTANT SET model x", VerbRisk.Review)]
    [InlineData("PLAYLIST NEW Show", VerbRisk.Low)]
    [InlineData("LAUNCH ROCKETS", VerbRisk.High)]
    [InlineData("SHOWTIME", VerbRisk.High)]
    public void ALinesRiskComesFromItsWords(string line, VerbRisk risk) => Assert.Equal(risk, Risk(line));

    [Fact]
    public void ACarriedLineIsReadWhereItsVerbPutsIt()
    {
        static string? Carried(string text)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out _));
            var (spec, words) = VerbCatalog.Match(line)!.Value;
            return VerbCatalog.EmbeddedLine(spec, line, words) is { } c ? string.Join(' ', c.Words) : null;
        }

        Assert.Equal("BLACKOUT OFF", Carried("BIND ADD MIDI APC40 NOTE 1 53 TO BLACKOUT OFF"));
        Assert.Equal("SURFACE CORNER sf1 TL TO 0.1 0.2", Carried("BIND LEARN FROM MIDI SURFACE CORNER sf1 TL TO 0.1 0.2"));
        Assert.Equal("OFF", Carried("BIND LEARN OFF"));
        Assert.Equal("PANIC", Carried("BIND ADD {\"input\":\"OSC /p\",\"target\":\"PANIC\"}"));
        Assert.Equal("PANIC", Carried("BIND ADD {\"Target\":\"TEMPO TAP\", \"target\":\"PANIC\"}"));
        Assert.Null(Carried("BIND ADD MIDI APC40 NOTE 1 53"));
        Assert.Null(Carried("BIND ADD {\"input\":\"OSC /p\"}"));
        Assert.Null(Carried("BLACKOUT ON"));
    }
}
