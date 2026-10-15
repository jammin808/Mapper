using PlayMap.Core.Wire;
using PlayMap.Engine.Phone;
using PlayMap.Net.Phone;

namespace PlayMap.Engine;

/// <summary>wp2-phonescan's services: set by its extension's Configure (null on a node without it; tests hand in their own).</summary>
public sealed partial record EngineServices
{
    /// <summary>The node's phones: invites, keys, sessions; a Phone camera rig scans through it.</summary>
    public PhoneHub? Phones { get; init; }

    /// <summary>Where an invite's link points for the session asking, and the certificate's short fingerprint (null: no phones).</summary>
    public Func<SessionInfo, CancellationToken, Task<PhoneDoor>>? PhoneDoor { get; init; }
}
