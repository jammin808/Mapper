using PlayMap.Core.Bus;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Wire;

/// <summary>A reply carries what became of its command (Show Bus 1.3 §13.12.2): Ok exactly for an applied or accepted one; one written
/// that no answer settled is unconfirmed — it may have run — Ok false and never refused, its text opening "unconfirmed: " and saying
/// so; one never written is not-sent; and a reply read from OK or ERR alone is applied or refused, as every reply was before.</summary>
public sealed class WireReplyTests
{
    [Fact]
    public void AReplyCarriesItsOutcomeAndItsOkNeverDisagreesWithIt()
    {
        Assert.Equal((true, PeerOutcome.Applied), Read(WireReply.Parse("OK @d1-a41f09c2 {\"id\":\"q1\"}")));
        Assert.Equal((false, PeerOutcome.Refused), Read(WireReply.Parse("ERR @d2-a41f09c2 no such playlist")));
        Assert.Equal((true, PeerOutcome.Applied), Read(new WireReply(true, "")));
        Assert.Equal((false, PeerOutcome.Refused), Read(new WireReply(false, "no such playlist")));
        Assert.Equal((true, PeerOutcome.Accepted), Read(new WireReply(true, "", PeerOutcome.Accepted)));

        var unconfirmed = WireReply.Unconfirmed("no answer from 127.0.0.1:9700 in 30 s — " + WireReply.MayHaveRunWords);
        Assert.Equal((false, PeerOutcome.Unconfirmed, true), (unconfirmed.Ok, unconfirmed.Outcome, unconfirmed.IsUnconfirmed));
        Assert.Equal("unconfirmed: no answer from 127.0.0.1:9700 in 30 s — it may have run; look at its state", unconfirmed.Text);
        // The same words read from ERR alone would be a refusal: the outcome, not the words, is what a reader takes.
        Assert.NotEqual(new WireReply(false, unconfirmed.Text), unconfirmed);
        var notSent = WireReply.NotSent("the link is down — nothing was sent");
        Assert.Equal((false, PeerOutcome.NotSent, false), (notSent.Ok, notSent.Outcome, notSent.IsUnconfirmed));

        foreach (var outcome in Enum.GetValues<PeerOutcome>())
        {
            var ok = outcome is PeerOutcome.Applied or PeerOutcome.Accepted;
            Assert.Equal((ok, outcome), Read(new WireReply(ok, "words", outcome)));
            Assert.Throws<ArgumentException>(() => new WireReply(!ok, "words", outcome));
        }

        // Only a constructor sets Ok and the outcome: neither has a setter, so no copy (`with`) can make them disagree; a copy that
        // moves the text keeps both.
        Assert.All(new[] { nameof(WireReply.Ok), nameof(WireReply.Outcome) },
            name => Assert.Null(typeof(WireReply).GetProperty(name)!.SetMethod));
        Assert.Equal((false, PeerOutcome.Unconfirmed), Read(unconfirmed with { Text = "other words" }));
    }

    private static (bool Ok, PeerOutcome Outcome) Read(WireReply reply) => (reply.Ok, reply.Outcome);
}
