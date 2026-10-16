using PlayMap.Core.Phone;
using PlayMap.Core.Scan;

namespace PlayMap.Engine.Scanning;

/// <summary>What is on the glass now, for a camera told: a locate frame (paired by change and timing) or a pattern (paired by its
/// tag when sites exist, else by change and timing), its index and tag value (0 for none), when it was presented and the earliest
/// instant a picture can show it (node monotonic ns), which retake this is, and the level a sample of the picture's grid must move by
/// from the last picture taken for a pairing by change (<see cref="Core.Phone.PictureGrid"/>).</summary>
public sealed record PatternCue(string Kind, string ProjectorId, int Index, int Tag, long PresentNs, long EarliestNs, int Retake, double MinChange);

/// <summary>The camera's answer to a cue: "seen" with its picture on the way (<see cref="Pixels"/> completes when it lands), the tag
/// it read, when it was taken (node ns), how many sites it read, the rolling-shutter mixes it threw away; or "stuck" with why.</summary>
public sealed record CuedPicture(string Outcome, Task<CameraFrame>? Pixels, int Tag, long CaptureNs, int Sites, int RollingRejects, string Why)
{
    public bool Seen => Outcome == "seen" && Pixels is not null;
}

/// <summary>What a view asks of the camera first: the exposure profile (lock or steady), the output's refresh rate and the settle
/// rule.</summary>
public sealed record CuedPrepare(string Profile, double DisplayHz, SettleOptions Settle);

/// <summary>What the camera ran: exposure "locked" or "steady", focus and white balance, whether the plan must skip its full white
/// and black (the steady profile), and the camera's words.</summary>
public sealed record CuedPrepared(string Exposure, string Focus, string WhiteBalance, bool SteadyPlan, string Words);

/// <summary>A camera the scan tells what is on the glass and that answers when it has seen it (a phone): lock-step capture paced by
/// observation instead of by a camera's frame stream and a measured latency.</summary>
public interface ICuedScanCamera : IScanCamera
{
    /// <summary>The profile the camera can run ("lock" or "steady").</summary>
    string Profile { get; }

    /// <summary>The camera's name in words ("Pixel 8").</summary>
    string Name { get; }

    Task<CuedPrepared> PrepareAsync(CuedPrepare request, CancellationToken ct);

    /// <summary>Where the projector's tag sites land in the camera's picture (none: pairing by change and timing).</summary>
    Task SetSitesAsync(string projectorId, CellLocateResult located, CancellationToken ct);

    /// <summary>Puts a cue to the camera; <paramref name="ct"/> ends the wait for its answer, <paramref name="scan"/> the wait for the
    /// picture it names.</summary>
    Task<CuedPicture> CueAsync(PatternCue cue, CancellationToken ct, CancellationToken scan);

    /// <summary>How many times the camera's own motion sensor has said it moved (0 for a camera without one).</summary>
    int Moves { get; }

    /// <summary>How long a scan waits on one picture before asking again (null: the runner's pattern timeout).</summary>
    TimeSpan? Patience { get; }

    /// <summary>SCAN NEXT for a camera a person releases by hand (a manual folder camera): the picture is taken now. False when the
    /// camera takes its pictures on its own.</summary>
    bool TakeNow();
}
