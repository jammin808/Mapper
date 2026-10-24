using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Show;

/// <summary>The show's gate on UNDO TO and FX RELEASE: UNDO TO is decided exactly as UNDO for every role, armed or not, the blind
/// owner's included (an operator's is allowed while armed; the blind owner's goes to the draft); FX RELEASE is refused while armed
/// and never goes to a draft (the quarantine is the node's, not the project's).</summary>
public sealed class UndoGateTests
{
    private static readonly ShowRole[] Roles =
        [ShowRole.Operator, ShowRole.Remote, ShowRole.Observer, ShowRole.Assistant, ShowRole.Approved, ShowRole.Peer, ShowRole.System];

    private static (VerbSpec Spec, WireLine Line) Parse(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        var match = VerbCatalog.Match(line!);
        Assert.NotNull(match);
        return (match.Value.Spec, line!);
    }

    private static ShowDecision Decide(string text, ShowRole role, in ShowFacts facts)
    {
        var (spec, line) = Parse(text);
        return ShowPolicy.Decide(spec, line, role, in facts);
    }

    public static TheoryData<bool, bool, bool> Facts => new()
    {
        { false, false, false }, { true, false, false }, { false, true, false }, { true, true, false }, { true, false, true },
    };

    [Theory]
    [MemberData(nameof(Facts))]
    public void UndoToIsDecidedExactlyAsUndoForEveryRole(bool armed, bool blindOwner, bool remoteViewOnly)
    {
        var facts = new ShowFacts(armed, blindOwner, false, remoteViewOnly, "paired");
        foreach (var role in Roles)
        {
            var undo = Decide("UNDO", role, facts);
            var to = Decide("UNDO TO 3", role, facts);
            Assert.True(undo.Verdict == to.Verdict, $"{role} armed={armed} blind={blindOwner}: UNDO {undo.Verdict}, UNDO TO {to.Verdict}");
            // The words are UNDO's with the verb's own name.
            Assert.Equal(undo.Words?.Replace("UNDO ", "UNDO TO ", StringComparison.Ordinal), to.Words);
        }
    }

    [Fact]
    public void AnOperatorUndoesToAStepWhileArmedAndTheBlindOwnerUndoesItsDraft()
    {
        var (spec, line) = Parse("UNDO TO 3");
        Assert.Equal(ShowClass.Project, ShowPolicy.ClassOf(spec, line));
        Assert.True(ShowPolicy.Draftable(spec, ShowClass.Project));

        Assert.Equal(ShowVerdict.Allow, Decide("UNDO TO 3", ShowRole.Operator, new ShowFacts(true, false, false, false, "paired")).Verdict);
        Assert.Equal(ShowVerdict.BlindOnly, Decide("UNDO TO 3", ShowRole.Operator, new ShowFacts(true, true, false, false, "paired")).Verdict);
        Assert.Equal(ShowVerdict.BlindOnly, Decide("UNDO TO 3", ShowRole.Operator, new ShowFacts(false, true, false, false, "paired")).Verdict);
        // Another project line is not taken while armed: UNDO TO is UNDO, not the class's rule.
        Assert.Equal(ShowVerdict.Refuse, Decide("PROJECT OPEN show.playmap", ShowRole.Operator, new ShowFacts(true, false, false, false, "paired")).Verdict);
        // An assistant's UNDO TO waits for a person while armed, as its UNDO does.
        Assert.Equal(ShowVerdict.Propose, Decide("UNDO TO 3", ShowRole.Assistant, new ShowFacts(true, false, false, false, "paired")).Verdict);
    }

    [Fact]
    public void FxReleaseIsRefusedArmedAndNeverGoesToADraft()
    {
        var (spec, line) = Parse("FX RELEASE ALL");
        Assert.Equal(ShowClass.Setup, ShowPolicy.ClassOf(spec, line));
        Assert.False(ShowPolicy.Draftable(spec, ShowClass.Setup));

        foreach (var role in new[] { ShowRole.Operator, ShowRole.Remote, ShowRole.Assistant, ShowRole.Approved, ShowRole.Peer })
        {
            var armed = Decide("FX RELEASE fx.blur", role, new ShowFacts(true, false, false, false, "paired"));
            Assert.Equal(ShowVerdict.Refuse, armed.Verdict);
            Assert.StartsWith("Not while armed: FX RELEASE", armed.Words, StringComparison.Ordinal);
            // The blind owner's FX RELEASE is not drafted: armed it is refused as anyone's, disarmed it runs live.
            Assert.Equal(ShowVerdict.Refuse, Decide("FX RELEASE fx.blur", role, new ShowFacts(true, true, false, false, "paired")).Verdict);
        }
        Assert.Equal(ShowVerdict.Allow, Decide("FX RELEASE ALL", ShowRole.Operator, new ShowFacts(false, true, false, false, "paired")).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("FX RELEASE ALL", ShowRole.System, new ShowFacts(true, false, false, false, "paired")).Verdict);
    }
}
