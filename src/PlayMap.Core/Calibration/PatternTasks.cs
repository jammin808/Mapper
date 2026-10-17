namespace PlayMap.Core.Calibration;

/// <summary>The work that puts patterns on the wall in place of the show, in STATE's own state words: while one is under way its
/// cancel is the safe direction (the wall goes back to the show). The engine's runners and every client read the same words.</summary>
public static class PatternTasks
{
    /// <summary>A scan under way (STATE.scan.state).</summary>
    public static bool ScanRunning(string? state) => state is "starting" or "running";

    /// <summary>An align run under way (STATE.align.run.state).</summary>
    public static bool AlignRunning(string? state) => state is "running";

    /// <summary>A calibration task holding the calibration lane (STATE.calibration.task.state): capturing, matching, solving or
    /// verifying.</summary>
    public static bool CalibrationRunning(string? state) => state is "capturing" or "matching" or "solving" or "verifying";
}
