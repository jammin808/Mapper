using PlayMap.Core.Model;

namespace PlayMap.Core.Playback;

/// <summary>How a playlist item comes in over the item before it, as pure functions of the schedule.
///
/// <para>An item with a <see cref="TransitionKind.Fade"/> or <see cref="TransitionKind.Dip"/> transition of
/// T overlaps the end of the item it follows: the window is [S − T, S), where S is the instant the outgoing
/// item reaches its out point (the switch). A fade starts the incoming item at S − T and mixes the two in
/// linear light; a dip takes the outgoing item to black over the first half and brings the incoming one up
/// from black over the second, starting it at S − T/2. T is cut to what the outgoing item has played since
/// its in point and to the incoming item's own length, so the window is a function of the two anchors alone
/// and every node holding them mixes the same pictures at the same instant.</para></summary>
public static class Transitions
{
    /// <summary>The display gamma the mix decodes through: pictures are gamma-encoded, light adds linearly.</summary>
    public const double Gamma = 2.2;

    /// <summary>True when the kind draws two pictures (a cut never does).</summary>
    public static bool Mixes(TransitionKind kind, long transitionNs) => kind is TransitionKind.Fade or TransitionKind.Dip && transitionNs > 0;

    /// <summary>The window's length for a switch at <paramref name="switchAtNs"/>: the item's own transition
    /// cut to what the outgoing item has played since <paramref name="outgoingStartNs"/> and to the incoming
    /// item's length on the leader's clock (0 = unknown, no cut). 0 = a cut.</summary>
    public static long WindowNs(TransitionKind kind, long transitionNs, long switchAtNs, long outgoingStartNs, long incomingLengthNs)
    {
        if (!Mixes(kind, transitionNs) || switchAtNs == long.MaxValue)
            return 0;
        var t = transitionNs;
        if (outgoingStartNs != long.MaxValue)
            t = Math.Min(t, Math.Max(0, switchAtNs - outgoingStartNs));
        if (incomingLengthNs > 0)
            t = Math.Min(t, incomingLengthNs);
        return Math.Max(0, t);
    }

    /// <summary>How long before the switch the incoming item starts playing: the whole window for a fade, its
    /// second half for a dip (the first half is the outgoing item going to black).</summary>
    public static long IncomingLeadNs(TransitionKind kind, long windowNs) => kind == TransitionKind.Dip ? windowNs / 2 : windowNs;

    /// <summary>The weight of each picture in linear light at a point of the window (0 = its start, 1 = the
    /// switch): a fade hands one over to the other; a dip passes through black at the middle.</summary>
    public static (double From, double To) Weights(TransitionKind kind, double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        return kind switch
        {
            TransitionKind.Fade => (1 - p, p),
            TransitionKind.Dip => p < 0.5 ? (1 - 2 * p, 0) : (0, 2 * p - 1),
            _ => p < 1 ? (1, 0) : (0, 1),
        };
    }

    /// <summary>One 8-bit channel of the mix, as the renderer draws it: both decoded to linear light, weighted,
    /// summed and encoded again.</summary>
    public static double Mix8(double from, double to, TransitionKind kind, double progress)
    {
        var (wf, wt) = Weights(kind, progress);
        var light = wf * Math.Pow(from / 255.0, Gamma) + wt * Math.Pow(to / 255.0, Gamma);
        return 255.0 * Math.Pow(Math.Clamp(light, 0, 1), 1 / Gamma);
    }
}
