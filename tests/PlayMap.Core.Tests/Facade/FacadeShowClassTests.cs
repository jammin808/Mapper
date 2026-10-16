using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Facade;

/// <summary>The site's lines in Show Mode, as docs/SHOW.md classes them: the queries run for everyone; the edits of the site, its
/// points and the priors are mapping — refused armed for a person, proposed by an assistant, run into the owner's blind draft; a generic
/// edit of a control point or a survey is mapping too; a survey import is heavy work — refused armed for everyone but the node, and
/// never into a draft.</summary>
public sealed class FacadeShowClassTests
{
    private static WireLine Line(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return line!;
    }

    private static (ShowClass Class, VerbSpec Spec, WireLine Line) Of(string text)
    {
        var line = Line(text);
        var spec = VerbCatalog.Match(line)!.Value.Spec;
        return (ShowPolicy.ClassOf(spec, line), spec, line);
    }

    private static ShowVerdict Armed(string text, ShowRole role, bool blindOwner = false)
    {
        var (_, spec, line) = Of(text);
        return ShowPolicy.Decide(spec, line, role, new ShowFacts(true, blindOwner, false, false, "paired")).Verdict;
    }

    [Theory]
    [InlineData("SITE", ShowClass.Query)]
    [InlineData("SITE CHECK", ShowClass.Query)]
    [InlineData("SURVEY LIST", ShowClass.Query)]
    [InlineData("SITE ORIGIN 512300 5401200 100 CRS EPSG:25832", ShowClass.Mapping)]
    [InlineData("SITE MAINS 50", ShowClass.Mapping)]
    [InlineData("SITE POINTS /survey/points.csv", ShowClass.Mapping)]
    [InlineData("PROJECTOR PRIOR pj00000001 THROW 3.02 ±2", ShowClass.Mapping)]
    [InlineData("SURVEY IMPORT /survey/front.las CRS EPSG:25832", ShowClass.Heavy)]
    [InlineData("SET cp00000001 name \"Corner\"", ShowClass.Mapping)]
    [InlineData("SET sv00000001 name \"Front\"", ShowClass.Mapping)]
    [InlineData("REMOVE cp00000001", ShowClass.Mapping)]
    [InlineData("ADD controlpoint {\"name\":\"CP9\"}", ShowClass.Mapping)]
    public void EverySiteLineHasTheClassItsWorkIs(string text, ShowClass expected) => Assert.Equal(expected, Of(text).Class);

    [Theory]
    [InlineData("SITE MAINS 50")]
    [InlineData("SITE POINTS /survey/points.csv")]
    [InlineData("PROJECTOR PRIOR pj00000001 AT CP7 ±0.05")]
    [InlineData("SET cp00000001 name \"Corner\"")]
    public void ArmedASiteEditIsRefusedForAPersonProposedByAnAssistantAndRunsIntoTheOwnersDraft(string text)
    {
        Assert.Equal(ShowVerdict.Refuse, Armed(text, ShowRole.Operator));
        Assert.Equal(ShowVerdict.Refuse, Armed(text, ShowRole.Remote));
        Assert.Equal(ShowVerdict.Propose, Armed(text, ShowRole.Assistant));
        Assert.Equal(ShowVerdict.Allow, Armed(text, ShowRole.Peer));
        Assert.Equal(ShowVerdict.BlindOnly, Armed(text, ShowRole.Operator, blindOwner: true));
        Assert.Equal(ShowVerdict.BlindOnly, Armed(text, ShowRole.Assistant, blindOwner: true));
    }

    [Fact]
    public void ASurveyImportIsHeavyWorkThatNoDraftTakes()
    {
        const string Import = "SURVEY IMPORT /survey/front.e57";
        foreach (var role in new[] { ShowRole.Operator, ShowRole.Remote, ShowRole.Assistant, ShowRole.Approved, ShowRole.Peer })
            Assert.Equal(ShowVerdict.Refuse, Armed(Import, role));
        Assert.Equal(ShowVerdict.Refuse, Armed(Import, ShowRole.Operator, blindOwner: true));
        Assert.Equal(ShowVerdict.Allow, Armed(Import, ShowRole.System));
        var (cls, spec, _) = Of(Import);
        Assert.False(ShowPolicy.Draftable(spec, cls));
        foreach (var query in new[] { "SITE", "SITE CHECK", "SURVEY LIST" })
            foreach (var role in new[] { ShowRole.Operator, ShowRole.Remote, ShowRole.Assistant, ShowRole.Approved })
                Assert.Equal(ShowVerdict.Allow, Armed(query, role));
    }
}
