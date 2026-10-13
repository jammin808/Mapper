using PlayMap.Net;

namespace PlayMap.Engine;

/// <summary>What the transports read beside the endpoint (Net's <see cref="INodeActivity"/>): whether anything plays
/// (STATE then reaches every listener at least once a second) and whether the show thread turns over (a WebSocket
/// screen hears <c>{"type":"beat","live":false}</c> instead of a frozen time while it does not). The show thread is
/// the control lane: every verb, edit and tick runs there, so a lane that has not run a probe for two seconds is a
/// node whose STATE is a picture of the past. A frozen air (FREEZE) is not a stuck thread: its STATE is still a fact.</summary>
public sealed partial class EngineCore : INodeActivity
{
    /// <summary>How long a probe may wait on the lane before the node counts as stuck.</summary>
    private const long LaneSilentNs = 2_000_000_000;

    private long _probePostedNs;
    private int _probing;

    /// <summary>True while the control lane turns over: the last probe came back, or the one out is younger than two
    /// seconds. Each read with no probe out posts one, so the answer is never older than the reader's own last look.</summary>
    public bool IsLive
    {
        get
        {
            var now = Services.Mono.NowNs;
            if (Volatile.Read(ref _probing) == 0)
            {
                // The last probe came back: the lane is turning over. Send the next one to ask again.
                Volatile.Write(ref _probePostedNs, now);
                if (Interlocked.CompareExchange(ref _probing, 1, 0) == 0)
                    _lane.Post(() => Volatile.Write(ref _probing, 0));
                return true;
            }
            return now - Volatile.Read(ref _probePostedNs) < LaneSilentNs;
        }
    }
}
