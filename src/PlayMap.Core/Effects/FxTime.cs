namespace PlayMap.Core.Effects;

/// <summary>An effect's clock for one frame, as ISF reads it: TIME in seconds since the slot's origin, TIMEDELTA (the show's
/// frame time, 0 on frame 0), FRAMEINDEX and DATE (year, month, day, seconds of the day, UTC). <see cref="Reanchor"/> says
/// TIME has left the range float32 holds to a millisecond (beyond 65 536 s, or before the origin).</summary>
public readonly record struct FxClock(float Time, float TimeDelta, int FrameIndex, float Year, float Month, float Day, float DaySeconds, bool Reanchor);

/// <summary>Every effect's time is a pure function of the leader's show time: no frame deltas are integrated, so two nodes at
/// the same leader instant give a shader the same numbers.</summary>
public static class FxTime
{
    /// <summary>Beyond this many seconds a float32 TIME steps by more than 4 ms.</summary>
    public const double PrecisionLimitSeconds = 65_536;

    /// <param name="leaderNs">The frame's leader instant.</param>
    /// <param name="originNs">The instant the slot's TIME counts from (its owner's origin, or the slot's own).</param>
    /// <param name="frameRate">The show's frame rate (TIMEDELTA is its frame time, never a measured delta).</param>
    /// <param name="unixMs">The leader instant as wall time, for DATE.</param>
    public static FxClock At(long leaderNs, long originNs, double frameRate, long unixMs)
    {
        var rate = frameRate > 0 && double.IsFinite(frameRate) ? frameRate : 60;
        var seconds = (leaderNs - originNs) / 1e9;
        var frame = seconds <= 0 ? 0 : Math.Min(int.MaxValue, Math.Floor(seconds * rate + 1e-6));
        var date = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(unixMs, 0, 253_402_300_799_000)).UtcDateTime;
        return new FxClock((float)seconds, frame <= 0 ? 0f : (float)(1 / rate), (int)frame, date.Year, date.Month, date.Day,
            (float)date.TimeOfDay.TotalSeconds, seconds is < 0 or > PrecisionLimitSeconds);
    }
}
