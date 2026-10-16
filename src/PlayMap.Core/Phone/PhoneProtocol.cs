using System.Collections.Immutable;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Phone;

/// <summary>The words of <c>playmap.camera.v1</c>, the camera page's protocol: JSON text messages naming their type in <c>t</c>,
/// and binary PMF1 frames. A message of another type closes the socket (fail closed).</summary>
public static class PhoneProtocol
{
    public const string SubProtocol = "playmap.camera.v1";

    /// <summary>The types a phone may send. Anything else closes the socket with 1008.</summary>
    public static readonly ImmutableHashSet<string> FromPhone =
        ImmutableHashSet.Create(StringComparer.Ordinal, "hello", "ping", "state", "prepared", "seen", "stuck", "moved", "bye");

    /// <summary>The exposure profiles: <c>lock</c> (the page sets exposure, focus and white balance itself and holds them),
    /// <c>steady</c> (the camera keeps its own exposure; no full white or black is shown and the references are made).</summary>
    public const string Lock = "lock";

    public const string Steady = "steady";
}

/// <summary>What the node asks before a view: the profile to run, the output's refresh (exposure rounds up to whole frames), and the
/// settle rule (two frames agreeing within the tolerance).</summary>
public sealed record PhonePrepare(string Profile, double DisplayHz, double AgreeTolerance, int StableFrames);

/// <summary>What the phone ran: exposure "locked" or "steady", the exposure time and ISO it set or read, focus and white balance
/// ("locked", "auto", "not offered"), and the words to show.</summary>
public sealed record PhonePrepared(string Exposure, double ExposureMs, double Iso, string Focus, string WhiteBalance, string Words);

/// <summary>A cue: the pattern now on the glass. <see cref="Id"/> is the session's own count; <see cref="Kind"/> "locate" (paired by
/// change and timing) or "pattern" (paired by its tag when sites exist); <see cref="Tag"/> the value its sites show (0 for none);
/// present and earliest are node monotonic nanoseconds — a picture taken before earliest cannot show it.</summary>
public sealed record PhoneCue(long Id, string Kind, string ProjectorId, int Index, int Tag, long PresentNs, long EarliestNs, int Retake,
    double MinChange);

/// <summary>Where the tag sites land in the phone's picture, the lit part of it, and how many sites a picture's tag needs.</summary>
public sealed record PhoneSites(string ProjectorId, ImmutableArray<TagSitePlacement> Sites, PixelRect Lit, int MinSites);

/// <summary>The phone's answer to a cue: "seen" with the upload's sequence number that carries the picture, the tag it read, when
/// it was taken (page ms), how many sites were lit, the smallest pair margin, the two settled frames' difference and the
/// rolling-shutter mixes it threw away on the way; or "stuck" with why.</summary>
public sealed record PhoneSeen(string Outcome, long Cue, long Seq, int Tag, double CapturePageMs, int Sites, int Margin, double SettleDiff,
    int RollingRejects, string Why);

/// <summary>A picture that arrived: its upload number, the stream's raster with the sent rows in place (rows outside the ROI are
/// zero), and when it was taken on the node's clock.</summary>
public sealed record PhonePicture(long Seq, string Kind, int Width, int Height, byte[] Gray, long CaptureNodeNs);
