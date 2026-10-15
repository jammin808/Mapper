using PlayMap.Core.Model;

namespace PlayMap.Core.Content;

/// <summary>A generator's own time: seconds since its origin on the leader clock, in double on the CPU (a float32 of a leader
/// time days into a show has lost its milliseconds). The origin is the leader instant <c>GENERATOR NEW</c> or <c>RETIME</c>
/// stamped (<see cref="Generator.OriginNs"/>; 0 = the leader epoch, files from before round 2); a generator a playlist routes reads
/// its item's clip position instead. Every generator's picture is a pure function of this time.</summary>
public static class ContentTime
{
    /// <summary>Past this many seconds a non-periodic kind's float time on the GPU loses precision: STATE warns and RETIME is the
    /// fix (2¹⁶ s, about 18 hours).</summary>
    public const double PrecisionSeconds = 65_536;

    public const string PrecisionWords = "time precision: re-time at a quiet moment";

    /// <summary>Seconds from <paramref name="originNs"/> to <paramref name="leaderNs"/>; negative before the origin.</summary>
    public static double Seconds(long leaderNs, long originNs) => (leaderNs - originNs) / 1e9;

    /// <summary>The phase of a rate at a time, 0–1, computed in double (a shader is handed the phase, never t·rate in float).</summary>
    public static double Phase(double seconds, double rate)
    {
        var v = seconds * rate;
        var f = v - Math.Floor(v);
        return f >= 1 ? 0 : f;
    }

    /// <summary>A time wrapped into one period (period ≤ 0: unchanged), so the float a shader reads stays small.</summary>
    public static double Wrap(double seconds, double period) =>
        period > 0 ? seconds - Math.Floor(seconds / period) * period : seconds;

    /// <summary>True when a generator of this kind running this long has lost float precision on the GPU.</summary>
    public static bool PrecisionLost(string kindId, double seconds) =>
        Math.Abs(seconds) > PrecisionSeconds && GeneratorTraits.Of(kindId).PeriodSeconds <= 0;
}
