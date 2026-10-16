using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Scan;

/// <summary>The scan lines in Show Mode, as docs/SHOW.md classes them: reading a scan's views and asking for suggestions are queries;
/// a scan (a new one, or a view INTO one), a joint solve and a drop are heavy work — refused armed for everyone but the node, and never
/// into a draft; SCAN NEXT puts a pattern up again (set-up); a surface made from a scan, a four-corner surface and a folder camera's
/// fields are mapping — refused armed for a person, proposed by an assistant, run into the owner's blind draft.</summary>
public sealed class MultiscanShowClassTests
{
    private static (ShowClass Class, VerbSpec Spec, WireLine Line) Of(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        var spec = VerbCatalog.Match(line!)!.Value.Spec;
        return (ShowPolicy.ClassOf(spec, line!), spec, line!);
    }

    private static ShowVerdict Armed(string text, ShowRole role, bool blindOwner = false)
    {
        var (_, spec, line) = Of(text);
        return ShowPolicy.Decide(spec, line, role, new ShowFacts(true, blindOwner, false, false, "paired")).Verdict;
    }

    [Theory]
    [InlineData("SCAN VIEWS sc00000001", ShowClass.Query)]
    [InlineData("SCAN SUGGEST sc00000001", ShowClass.Query)]
    [InlineData("SCAN SUGGEST sc00000001 ASSISTANT", ShowClass.Query)]
    [InlineData("SCAN START cm00000001 pj00000001 INTO sc00000001", ShowClass.Heavy)]
    [InlineData("SCAN START phone:ab12cd34 pj00000001", ShowClass.Heavy)]
    [InlineData("SCAN SOLVE sc00000001 HOLD pj00000001", ShowClass.Heavy)]
    [InlineData("SCAN DROP sc00000001 v2", ShowClass.Heavy)]
    [InlineData("SCAN NEXT", ShowClass.Setup)]
    [InlineData("SCAN SURFACE sc00000001 REGION rg1 ON pj00000001 Wall", ShowClass.Mapping)]
    [InlineData("SCAN SURFACE sc00000001 0.5 0.5", ShowClass.Mapping)]
    [InlineData("SURFACE QUAD pj00000001 0 0 1 0 1 1 0 1 RASTER AS Wall", ShowClass.Mapping)]
    [InlineData("SET cm00000001 folder.advance \"manual\"", ShowClass.Mapping)]
    public void EveryScanLineHasTheClassItsWorkIs(string text, ShowClass expected) => Assert.Equal(expected, Of(text).Class);

    [Theory]
    [InlineData("SCAN START cm00000001 pj00000001 INTO sc00000001")]
    [InlineData("SCAN SOLVE sc00000001")]
    [InlineData("SCAN DROP sc00000001 v2")]
    [InlineData("SCAN NEXT")]
    public void ArmedAScanThatLightsOutputsOrSolvesIsRefusedForEveryoneButTheNodeAndNoDraftTakesIt(string text)
    {
        foreach (var role in new[] { ShowRole.Operator, ShowRole.Remote, ShowRole.Assistant, ShowRole.Approved, ShowRole.Peer })
            Assert.Equal(ShowVerdict.Refuse, Armed(text, role));
        Assert.Equal(ShowVerdict.Refuse, Armed(text, ShowRole.Operator, blindOwner: true));
        Assert.Equal(ShowVerdict.Allow, Armed(text, ShowRole.System));
        var (cls, spec, _) = Of(text);
        Assert.False(ShowPolicy.Draftable(spec, cls));
    }

    [Theory]
    [InlineData("SCAN SURFACE sc00000001 REGION rg1 ON pj00000001 Wall")]
    [InlineData("SURFACE QUAD pj00000001 0 0 1 0 1 1 0 1 RASTER AS Wall")]
    public void ArmedASurfaceFromAScanIsRefusedForAPersonProposedByAnAssistantAndRunsIntoTheOwnersDraft(string text)
    {
        Assert.Equal(ShowVerdict.Refuse, Armed(text, ShowRole.Operator));
        Assert.Equal(ShowVerdict.Refuse, Armed(text, ShowRole.Remote));
        // A person's approval of a suggestion's line runs as approved: refused while armed, as every approved mapping line is.
        Assert.Equal(ShowVerdict.Refuse, Armed(text, ShowRole.Approved));
        Assert.Equal(ShowVerdict.Propose, Armed(text, ShowRole.Assistant));
        Assert.Equal(ShowVerdict.BlindOnly, Armed(text, ShowRole.Operator, blindOwner: true));
    }

    [Fact]
    public void ReadingAScanAndAskingForSuggestionsRunForEveryone()
    {
        foreach (var query in new[] { "SCAN VIEWS sc00000001", "SCAN SUGGEST sc00000001" })
            foreach (var role in new[] { ShowRole.Operator, ShowRole.Remote, ShowRole.Assistant, ShowRole.Approved, ShowRole.Peer })
                Assert.Equal(ShowVerdict.Allow, Armed(query, role));
    }
}
