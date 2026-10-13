using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>The network verbs that change the node itself rather than a link: whether a followed Patterns desk's
/// blackout blacks this node out, and a new pairing token.</summary>
public sealed partial class EngineCore
{
    /// <summary>LINK PATTERNS FOLLOW-BLACKOUT ON|OFF: the air's "Follow Patterns' blackout" switch. The desk's blackout
    /// fact is kept either way, so switching it back on blacks out at once when the desk is blacked out.</summary>
    private CommandReply FollowPatternsBlackout(List<string> args)
    {
        if (args.Count < 2 || !TryOnOff(args[1], out var on))
            return CommandReply.Fail("LINK PATTERNS FOLLOW-BLACKOUT ON|OFF");
        if (_air.FollowPatternsBlackout == on)
            return CommandReply.Fail(on ? "already following Patterns' blackout" : "already ignoring Patterns' blackout");
        SetAir(a => a with { FollowPatternsBlackout = on });
        var now = _air.PatternsBlackout ? (on ? " — Patterns is blacked out, so this node is too" : " — Patterns is blacked out, this node is not") : "";
        return CommandReply.Success(effect: (on ? "following Patterns' blackout" : "ignoring Patterns' blackout") + now);
    }

    /// <summary>PAIRING NEW: a new token, written where the node keeps its settings and taken by every transport at
    /// once. The reply never carries it.</summary>
    private CommandReply NewPairing()
    {
        if (Services.Pairing is not { } pairing)
            return CommandReply.Fail("this node keeps no pairing token of its own (it runs without a host)");
        var words = pairing.NewToken();
        FactsChanged();
        return CommandReply.Success(words, words);
    }
}
