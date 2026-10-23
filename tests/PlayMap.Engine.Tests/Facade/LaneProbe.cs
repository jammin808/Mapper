using System.Diagnostics;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>How long the control lane keeps a line waiting or takes to answer one, read on the lane, by the stopwatch. Whatever holds
/// the lane ahead of the line is in it — a slice's work, a wait inside a slice, the lane thread stopped — and the asking test's own
/// pace and its continuation after the lane has answered never are.</summary>
internal sealed class LaneProbe(ControlLane lane)
{
    /// <summary>Posts an empty line and answers how long the lane kept it waiting (ms): from the instant it is posted to the instant
    /// the lane starts it. A stopwatch read is the tick it falls in, so a span is taken one tick longer than its reads, never shorter
    /// than it lasted.</summary>
    public async Task<double> WaitedMsAsync()
    {
        var posted = Stopwatch.GetTimestamp();
        var started = await lane.RunAsync(Stopwatch.GetTimestamp);
        return (started - posted + 1) * 1e3 / Stopwatch.Frequency;
    }

    /// <summary>Asks a line of the lane, waits for its answer, and answers how long the lane took to come through it (ms): how long an
    /// empty line posted just before it waited for the lane, and the lane's turn from that line's end to the start of an empty line
    /// posted just behind it. <paramref name="ask"/> posts its line to the lane before it returns (the engine posts an on-lane verb's
    /// line before its first await). The line before holds the lane until the asked line and the one behind are both posted, so the
    /// turn holds the lane's work between them alone — the asked line's, and whatever else the lane was given meanwhile, as an import's
    /// slices — and the asking thread's pace between its posts is in neither span.</summary>
    public async Task<double> AnsweredMsAsync(Func<Task> ask)
    {
        // The line before owns the gate once the asking thread has set it: it waits on it and lets it go, whatever the asking thread
        // does after.
        var bothPosted = new ManualResetEventSlim();
        var posted = Stopwatch.GetTimestamp();
        var ahead = lane.RunAsync(() =>
        {
            var started = Stopwatch.GetTimestamp();
            bothPosted.Wait();
            var ended = Stopwatch.GetTimestamp();
            bothPosted.Dispose();
            return (Started: started, Ended: ended);
        });
        Task answer;
        Task<long> behind;
        try
        {
            answer = ask();
            behind = lane.RunAsync(Stopwatch.GetTimestamp);
        }
        finally
        {
            bothPosted.Set();
        }
        var (started, ended) = await ahead;
        var reached = await behind;
        await answer;
        return ((started - posted + 1) + (reached - ended + 1)) * 1e3 / Stopwatch.Frequency;
    }
}
