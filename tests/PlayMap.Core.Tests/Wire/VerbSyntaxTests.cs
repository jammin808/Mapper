using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Wire;

/// <summary>The one reader of a row's syntax: its forms, each form's required words, and whether a line fits one.</summary>
public sealed class VerbSyntaxTests
{
    private static IReadOnlyList<string> Forms(string verb) => VerbSyntax.Alternatives(VerbCatalog.Find(verb)!);

    [Fact]
    public void ARowSplitsIntoItsFormsEachStartingWithTheVerb()
    {
        Assert.Equal(["GENERATOR SET <generator> <param> <value>", "GENERATOR SET <generator> <param> BY <delta>"], Forms("GENERATOR SET"));
        Assert.Equal(["UNLOCK <id>", "UNLOCK AREA <area>", "UNLOCK ALL"], Forms("UNLOCK"));
        Assert.Equal(["MEDIA OPTIMISE <media>…|ALL [TO hap|hap-alpha|hap-q|hap-q-alpha] [QUALITY FAST|SHOW]", "MEDIA OPTIMISE CANCEL|PAUSE|RESUME [<job>|ALL]"], Forms("MEDIA OPTIMISE"));
        Assert.Equal(
        [
            "VIDEOIO SET NDI ON|OFF", "VIDEOIO SET NDI-GROUPS <groups>", "VIDEOIO SET NDI-DISCOVERY <host,…>|OFF", "VIDEOIO SET OMT ON|OFF",
            "VIDEOIO SET OMT-DISCOVERY <host[:port]>|OFF", "VIDEOIO SET SPOUT ON|OFF", "VIDEOIO SET MAX-SEND-MBPS <0-100000>",
        ], Forms("VIDEOIO SET"));
        Assert.Equal(["BIND ADD <input words> TO <target line>", "BIND ADD <json>"], Forms("BIND ADD"));
        Assert.Equal(["FX SET <slot> <param>|mix|blend|scale|name <value>", "FX SET <slot> <param> BY <delta>"], Forms("FX SET"));
        Assert.Equal(["ALIGN CORNERS <group> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4>", "ALIGN CORNERS <group> CLEAR"], Forms("ALIGN CORNERS"));
        Assert.Equal(["HANDLE SELECT <surface> TL|TR|BR|BL|<col>,<row>", "HANDLE SELECT NONE"], Forms("HANDLE SELECT"));
        Assert.Equal(
        [
            "SCAN SURFACE <scan> <x> <y> [ON <projector>] [<name>]", "SCAN SURFACE <scan> REGION <id> [ON <projector>] [<name>]",
        ], Forms("SCAN SURFACE"));
        Assert.Equal(["LINK PATTERNS <host[:port]>|OFF [<token>]", "LINK PATTERNS FOLLOW-BLACKOUT ON|OFF"], Forms("LINK PATTERNS"));
        Assert.Equal(["FLEET ROUTE <output> DEVICE <device> <input> <backup input>", "FLEET ROUTE <output> NONE", "FLEET ROUTE TEST"], Forms("FLEET ROUTE"));
        Assert.Equal(["MIRROR HELLO|PROJECT|AIR|HANDBACK <json>", "MIRROR ANCHOR <playlist> <json>"], Forms("MIRROR"));
        Assert.Equal(6, Forms("SHOW SET").Count);
        Assert.Equal("SHOW SET COMMIT-LEAD <ms>", Forms("SHOW SET")[4]);
        // A pipe inside <…> or […] is a choice, not a form.
        Assert.Equal(["VIDEO SEEK <83|83.5|2.5s|1:23|1:02:03|top|END [s]> [ON <playlist>]"], Forms("VIDEO SEEK"));
        Assert.Single(Forms("PANIC"));
    }

    [Fact]
    public void AFormRequiresItsWordsUpToItsFirstOptionalPart()
    {
        var spec = VerbCatalog.Find("SURFACE CORNER")!;
        Assert.Equal(["<surface>", "TL|TR|BR|BL|0|1|2|3", "TO", "<x>", "<y>"], VerbSyntax.RequiredTokens(spec, Forms("SURFACE CORNER")[0]));
        Assert.Equal(["<surface>", "<corner>", "BY", "<dx>", "<dy>"], VerbSyntax.RequiredTokens(spec, Forms("SURFACE CORNER")[1]));
        Assert.Empty(VerbSyntax.RequiredTokens(VerbCatalog.Find("BIND LEARN")!, Forms("BIND LEARN")[0]));
        Assert.Equal(["OFF"], VerbSyntax.RequiredTokens(VerbCatalog.Find("BIND LEARN")!, Forms("BIND LEARN")[1]));
        Assert.True(VerbSyntax.IsPlaceholder("<source>[,<backup>…]|OFF"));
        Assert.False(VerbSyntax.IsPlaceholder("ON|OFF|TOGGLE"));
    }

    [Theory]
    [InlineData("GENERATOR SET gn1 zoom 2")]
    [InlineData("GENERATOR SET gn1 zoom BY -0.1")]
    [InlineData("UNLOCK ALL")]
    [InlineData("UNLOCK AREA mapping")]
    [InlineData("SHOW SET REMOTE-VIEW-ONLY ON")]
    [InlineData("SHOW SET MINGO 300 ARMED")]
    [InlineData("FLEET ROUTE op_1 NONE")]
    [InlineData("EYE FOCUS op_1")]
    [InlineData("FX BIND sf1 opacity audio:bass")]
    [InlineData("TC CHASE pl1 tc1")]
    [InlineData("TIMECODE SET pl1 FREEWHEEL 500")]
    [InlineData("TIMECODE SET pl1 ONLOSS stop")]
    [InlineData("TIMECODE SET pl1 GAP HOLD")]
    [InlineData("TIMECODE SOURCE ADD Desk MTC \"IAC Bus 1\"")]
    [InlineData("TIMECODE SOURCE ADD Desk ARTNET")]
    [InlineData("TIMECODE INTERNAL tc1 PLAY")]
    [InlineData("TIMECODE INTERNAL tc1 LOCATE 01:00:00:00")]
    [InlineData("AUDIO LISTEN DEFAULT")]
    [InlineData("AUDIO LISTEN TEST CLICK")]
    [InlineData("PROJECTOR PRIOR pj1 AT cp1")]
    [InlineData("PROJECTOR PRIOR pj1 AT 1 2 3")]
    [InlineData("SURFACE CORNER sf1 2 TO 0.1 0.2")]
    [InlineData("SURFACE CORNER sf1 TL BY 3 -2 PX")]
    [InlineData("SUBSCRIBE HEALTH")]
    [InlineData("SUBSCRIBE THUMBS")]
    [InlineData("LINK PATTERNS FOLLOW-BLACKOUT OFF")]
    [InlineData("MEDIA OPTIMISE CANCEL")]
    [InlineData("BIND LEARN OFF")]
    public void ARealLineFitsOneOfItsVerbsForms(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out _));
        var spec = VerbCatalog.Match(line)!.Value.Spec;
        Assert.True(VerbSyntax.Fits(spec, line, out var why), why);
        Assert.Null(why);
    }

    [Theory]
    [InlineData("SHOW SET LOUDNESS 3", "SHOW SET takes MINGO there, not 'LOUDNESS'")]
    [InlineData("UNLOCK", "UNLOCK needs <id>")]
    [InlineData("TIMECODE SET pl1 SPEED 2", "takes DELAY there, not 'SPEED'")]
    [InlineData("SURFACE CORNER sf1 TL TO 0.1", "SURFACE CORNER needs <y>")]
    [InlineData("BLACKOUT MAYBE", "BLACKOUT takes ON|OFF|TOGGLE there, not 'MAYBE'")]
    public void ALineThatFitsNoFormSaysWhatTheClosestLacks(string text, string words)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out _));
        var spec = VerbCatalog.Match(line)!.Value.Spec;
        Assert.False(VerbSyntax.Fits(spec, line, out var why));
        Assert.Contains(words, why, StringComparison.Ordinal);
        Assert.EndsWith(spec.Syntax, why, StringComparison.Ordinal);
    }

    [Fact]
    public void TcReadsAsTimecodeAndTheOldAliasesStillRead()
    {
        Assert.True(WireGrammar.TryParse("tc status", out var line, out _));
        Assert.Equal(("TIMECODE STATUS", 2), (VerbCatalog.Match(line)!.Value.Spec.Name, VerbCatalog.Match(line)!.Value.Words));
        Assert.True(WireGrammar.TryParse("VT PLAY", out var vt, out _));
        Assert.Equal("VIDEO PLAY", VerbCatalog.Match(vt)!.Value.Spec.Name);
        Assert.True(WireGrammar.TryParse("EYE", out var eye, out _));
        Assert.Equal("FLEET", VerbCatalog.Match(eye)!.Value.Spec.Name);
    }
}
