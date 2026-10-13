namespace PlayMap.Core.Show;

/// <summary>Who a session is to Show Mode, from its transport (never from words a client sends).</summary>
public enum ShowRole { Operator, Remote, Observer, Assistant, Approved, Peer, System }

/// <summary>What Show Mode makes of a line: run it, refuse it, queue it for a person, or take it into the blind draft only.</summary>
public enum ShowVerdict { Allow, Refuse, Propose, BlindOnly }

/// <summary>The facts a decision reads; a value, no allocation.</summary>
public readonly record struct ShowFacts(bool Armed, bool BlindOwner, bool AirDark, bool RemoteViewOnly, string DisarmFrom);

/// <summary>A decision and, when it is not Allow, its words.</summary>
public readonly record struct ShowDecision(ShowVerdict Verdict, string? Words);

// wp2-show implements (pure, allocation-free, every catalogue row × role × armed has a decision):
//   public static class ShowPolicy
//   {
//       public static ShowRole RoleOf(SessionInfo session, IReadOnlyCollection<string> operators);
//       public static ShowClass ClassOf(VerbSpec spec, WireLine line);   // Target verbs classed by the id's prefix
//       public static ShowDecision Decide(VerbSpec spec, WireLine line, ShowRole role, in ShowFacts facts);
//   }
