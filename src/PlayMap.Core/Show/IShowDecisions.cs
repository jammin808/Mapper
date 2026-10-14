using PlayMap.Core.Wire;

namespace PlayMap.Core.Show;

/// <summary>A node's Show Mode as a caller outside the engine asks it before it runs or queues a line (the MCP tools, the
/// assistant's tools): what the show's table says about this line from this session now, from published state only. A
/// <see cref="ShowVerdict.Propose"/> means the line waits for a person rather than being refused.</summary>
public interface IShowDecisions
{
    ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session);
}
