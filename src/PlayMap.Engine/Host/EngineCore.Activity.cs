using PlayMap.Net;

namespace PlayMap.Engine;

/// <summary>What the transports read beside the endpoint (Net's <see cref="INodeActivity"/>): whether anything moves — a
/// playlist plays, or STATE showed something moving on its own a moment ago, such as a rolling timecode (STATE then
/// reaches every listener at least once a second) — and whether the show thread turns over (a WebSocket
/// screen hears <c>{"type":"beat","live":false}</c> instead of a frozen time while it does not). The show thread is
/// the control lane: every verb, edit and tick runs there, so a lane that has not run a probe for two seconds is a
/// node whose STATE is a picture of the past. A frozen air (FREEZE) is not a stuck thread: its STATE is still a fact.</summary>
public sealed partial class EngineCore : INodeActivity
{
    /// <summary>How long a probe may wait on the lane before the node counts as stuck.</summary>
    private const long LaneSilentNs = 2_000_000_000;

    /// <summary>How long after STATE last showed something moving on its own the node still counts as animating: longer than the
    /// transports' once-a-second look, so a move between two looks is pushed by the next one.</summary>
    private const long MovedHoldsNs = 2_000_000_000;

    /// <summary>The transports' "anything moves": a playlist plays (<see cref="IsAnimating"/>, which stays "a playlist plays" for
    /// the show's light and the media pull rate), or STATE showed something moving on its own within <see cref="MovedHoldsNs"/>
    /// (<see cref="Moved"/>: a rolling timecode with nothing playing).</summary>
    bool INodeActivity.IsAnimating
    {
        get
        {
            if (IsAnimating)
                return true;
            var moved = Volatile.Read(ref _movedAtNs);
            return moved != long.MinValue && Services.Mono.NowNs - moved < MovedHoldsNs;
        }
    }

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
