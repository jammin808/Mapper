using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

public sealed partial class FakeEngineLink
{
    partial void TryMapVerbs(WireLine line, ref WireReply? reply)
    {
        // The Map desk package answers its verbs here in the demo; until it lands it answers none.
    }
}
