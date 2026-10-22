using System.Diagnostics;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>How long the control lane keeps a line waiting, read on the lane: from the instant an empty line is posted to the instant
/// the lane starts it, by the stopwatch. Whatever holds the lane ahead of the line is in it — a slice's work, a wait inside a slice,
/// the lane thread stopped — and the asking test's own continuation after the lane has answered never is.</summary>
internal sealed class LaneProbe(ControlLane lane)
{
    /// <summary>Posts an empty line and answers how long the lane kept it waiting (ms). A stopwatch read is the tick it falls in, so
    /// the span is taken one tick longer than its reads, never shorter than it lasted.</summary>
    public async Task<double> WaitedMsAsync()
    {
        var posted = Stopwatch.GetTimestamp();
        var started = await lane.RunAsync(Stopwatch.GetTimestamp);
        return (started - posted + 1) * 1e3 / Stopwatch.Frequency;
    }
}
