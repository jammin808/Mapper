using PlayMap.Core.Time;

namespace PlayMap.Core.Playback;

/// <summary>Where a timeline is, as a pure function of the leader's clock: at leader time
/// <see cref="LeaderNs"/> the media time was <see cref="MediaNs"/>, advancing at <see cref="Rate"/> when
/// <see cref="Running"/>. Every node holding the same anchor computes the same media time for the same
/// leader instant — no node integrates a frame delta, so no node drifts. A start in the future is simply an
/// anchor whose leader time has not come yet: the media time holds until it does.</summary>
public readonly record struct TimelineAnchor(long MediaNs, long LeaderNs, double Rate, bool Running)
{
    public static readonly TimelineAnchor Zero = new(0, 0, 1, false);

    /// <summary>The media time at a leader instant (held before a scheduled start).</summary>
    public long MediaAt(long leaderNs)
    {
        if (!Running || leaderNs <= LeaderNs)
            return MediaNs;
        return MediaNs + (long)Math.Round((leaderNs - LeaderNs) * Rate);
    }

    /// <summary>The leader instant the timeline reaches a media time (for scheduling an end, a loop).</summary>
    public long LeaderAt(long mediaNs) =>
        !Running || Rate <= 0 ? long.MaxValue : LeaderNs + (long)Math.Round((mediaNs - MediaNs) / Rate);

    public TimelineAnchor Pause(long atLeaderNs) => new(MediaAt(atLeaderNs), atLeaderNs, Rate, false);

    public TimelineAnchor Resume(long atLeaderNs) => new(MediaAt(atLeaderNs), atLeaderNs, Rate, true);

    public TimelineAnchor Seek(long mediaNs, long atLeaderNs) => this with { MediaNs = mediaNs, LeaderNs = atLeaderNs };

    public TimelineAnchor WithRate(double rate, long atLeaderNs) => new(MediaAt(atLeaderNs), atLeaderNs, rate, Running);
}

/// <summary>Stateless frame selection: which frame of a clip an output shows at a presentation instant.
/// Every node runs the same function of the leader's clock at its predicted vblank (plus its display's
/// measured latency), so a dropped render frame heals itself and two machines never accumulate drift.</summary>
public static class FrameSelector
{
    /// <summary>The frame index of a clip at the given leader instant, clamped to [0, frameCount − 1]
    /// (frameCount ≤ 0 = unknown length, no upper clamp). A small epsilon keeps a vblank that lands
    /// exactly on a frame boundary on the frame that starts there.</summary>
    public static long FrameAt(TimelineAnchor anchor, long presentLeaderNs, FrameRate rate, long frameCount = 0)
    {
        var media = anchor.MediaAt(presentLeaderNs);
        if (!rate.IsValid)
            return 0;
        var f = (long)Math.Floor(media * (double)rate.Numerator / (rate.Denominator * 1e9) + 1e-6);
        if (f < 0)
            return 0;
        return frameCount > 0 ? Math.Min(f, frameCount - 1) : f;
    }

    /// <summary>The media time to show for a looping range [inNs, outNs) at a leader instant.</summary>
    public static long LoopedMedia(TimelineAnchor anchor, long presentLeaderNs, long inNs, long outNs)
    {
        var m = anchor.MediaAt(presentLeaderNs);
        var len = outNs - inNs;
        if (len <= 0 || m < outNs)
            return m;
        return inNs + (m - inNs) % len;
    }

    /// <summary>The local instant to present for: the predicted vblank plus the display's measured latency,
    /// mapped onto the leader's clock.</summary>
    public static long PresentLeaderNs(NetworkClock clock, long predictedVblankLocalNs, long displayLatencyNs)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.ToLeader(predictedVblankLocalNs + displayLatencyNs);
    }
}
