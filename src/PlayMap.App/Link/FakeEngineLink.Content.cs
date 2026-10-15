using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

public sealed partial class FakeEngineLink
{
    partial void TryContentVerbs(WireLine line, ref WireReply? reply)
    {
        // The Content desk package answers its verbs here in the demo; until it lands it answers none.
    }
}
