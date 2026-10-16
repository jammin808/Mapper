using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Show;

/// <summary>The present path and the decode policy in Show Mode, as docs/SHOW.md classes them: PRESENT PATH and DECODE POLICY (with
/// or without FORCE) are set-up — refused armed for a person, a remote client, an assistant and a peer, run by the node itself, never
/// into a blind draft (the owner's line is refused armed like anyone's, and runs live disarmed); PRESENT RESET is performance (zeroed
/// counters change no picture) and runs armed; DECODE STATUS is a query for everyone.</summary>
public sealed class GpuShowClassTests
{
    private static (ShowClass Class, VerbSpec Spec, WireLine Line) Of(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        var spec = VerbCatalog.Match(line!)!.Value.Spec;
        return (ShowPolicy.ClassOf(spec, line!), spec, line!);
    }

    private static ShowVerdict Decide(string text, ShowRole role, bool armed, bool blindOwner = false)
    {
        var (_, spec, line) = Of(text);
        return ShowPolicy.Decide(spec, line, role, new ShowFacts(armed, blindOwner, false, false, "paired")).Verdict;
    }

    [Theory]
    [InlineData("PRESENT PATH DXGI", ShowClass.Setup)]
    [InlineData("PRESENT PATH AUTO", ShowClass.Setup)]
    [InlineData("PRESENT RESET ALL", ShowClass.Perform)]
    [InlineData("DECODE POLICY SOFTWARE", ShowClass.Setup)]
    [InlineData("DECODE POLICY DIRECT FORCE", ShowClass.Setup)]
    [InlineData("DECODE STATUS", ShowClass.Query)]
    public void EachLineHasTheClassItsWorkIs(string text, ShowClass expected) => Assert.Equal(expected, Of(text).Class);

    [Theory]
    [InlineData("PRESENT PATH GL")]
    [InlineData("DECODE POLICY COPY-BACK")]
    [InlineData("DECODE POLICY DIRECT FORCE")]
    public void ArmedASetUpLineIsRefusedForEveryoneButTheNodeAndNoDraftTakesIt(string text)
    {
        foreach (var role in new[] { ShowRole.Operator, ShowRole.Remote, ShowRole.Assistant, ShowRole.Approved, ShowRole.Peer })
            Assert.Equal(ShowVerdict.Refuse, Decide(text, role, armed: true));
        Assert.Equal(ShowVerdict.Refuse, Decide(text, ShowRole.Operator, armed: true, blindOwner: true));
        Assert.Equal(ShowVerdict.Allow, Decide(text, ShowRole.System, armed: true));
        var (cls, spec, _) = Of(text);
        Assert.False(ShowPolicy.Draftable(spec, cls));
        // Disarmed, the draft's owner runs it live: a blind draft stages only the show's document.
        Assert.Equal(ShowVerdict.Allow, Decide(text, ShowRole.Operator, armed: false, blindOwner: true));
    }

    [Fact]
    public void ArmedPresentResetRunsForAPersonAndDecodeStatusForEveryone()
    {
        Assert.Equal(ShowVerdict.Allow, Decide("PRESENT RESET ALL", ShowRole.Operator, armed: true));
        Assert.Equal(ShowVerdict.Allow, Decide("PRESENT RESET op_1", ShowRole.Remote, armed: true));
        Assert.Equal(ShowVerdict.Propose, Decide("PRESENT RESET ALL", ShowRole.Assistant, armed: true));
        foreach (var role in new[] { ShowRole.Operator, ShowRole.Remote, ShowRole.Assistant, ShowRole.Approved, ShowRole.Peer })
            Assert.Equal(ShowVerdict.Allow, Decide("DECODE STATUS", role, armed: true));
    }
}
