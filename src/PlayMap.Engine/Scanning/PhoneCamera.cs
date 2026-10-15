using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Net.Phone;

namespace PlayMap.Engine.Scanning;

/// <summary>
/// A phone as a scan camera (a camera rig of kind Phone, its source the phone's id): the phone's session on the hub, driven
/// lock-step. It gives no free-running frames — a phone sends a picture only for a cue — so a camera view of it says so. A phone that
/// is not connected does not open, with the words that say how to connect it; one that goes during the scan ends every wait with
/// its words.
/// </summary>
public sealed class PhoneCamera : ICuedScanCamera
{
    private readonly PhoneHub _hub;
    private readonly CameraRig _rig;
    private IPhoneSession? _session;
    private string _problem = "";

    public PhoneCamera(PhoneHub hub, CameraRig rig)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _rig = rig ?? throw new ArgumentNullException(nameof(rig));
    }

    public string Name => _rig.Name;

    public string Profile => _session?.Profile.Exposure ?? _rig.Phone?.Exposure ?? PhoneProtocol.Steady;

    public string Problem => _session is { Connected: false } s ? $"{s.Name} {s.Gone}" : _problem;

    public Task<bool> OpenAsync(CancellationToken ct)
    {
        _session = _hub.Session(_rig.Source);
        _problem = _session is null ? $"{_rig.Name} is not connected — open the invite on the phone (PHONE INVITE makes one)" : "";
        return Task.FromResult(_session is not null);
    }

    /// <summary>A phone streams no frames: its pictures come only for a scan's cues.</summary>
    public Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct)
    {
        _problem = _session is null ? _problem : $"{_rig.Name} sends pictures only for a scan's patterns";
        return Task.FromResult<CameraFrame?>(null);
    }

    private IPhoneSession Session => _session ?? throw new InvalidOperationException(_problem.Length > 0 ? _problem : $"{_rig.Name} is not open");

    public async Task<CuedPrepared> PrepareAsync(CuedPrepare request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = await Session.PrepareAsync(new PhonePrepare(request.Profile, request.DisplayHz, request.Settle.AgreeTolerance, request.Settle.StableFrames), ct)
            .ConfigureAwait(false);
        return new CuedPrepared(p.Exposure, p.Focus, p.WhiteBalance, p.Exposure != "locked", p.Words);
    }

    public Task SetSitesAsync(string projectorId, CellLocateResult located, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(located);
        return Session.SetSitesAsync(new PhoneSites(projectorId, located.Chosen, located.Lit, located.MinSites), ct);
    }

    public async Task<CuedPicture> CueAsync(PatternCue cue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var session = Session;
        var seen = await session.CueAsync(new PhoneCue(0, cue.Kind, cue.ProjectorId, cue.Index, cue.Tag, cue.PresentNs, cue.EarliestNs, cue.Retake,
            cue.MinChange), ct).ConfigureAwait(false);
        if (seen.Outcome != "seen")
            return new CuedPicture("stuck", null, 0, 0, seen.Sites, seen.RollingRejects, seen.Why.Length > 0 ? seen.Why : "no picture");
        var capture = double.IsFinite(seen.CapturePageMs) ? session.ToNodeNs(seen.CapturePageMs) : 0;
        return new CuedPicture("seen", PixelsAsync(session, seen.Seq, ct), seen.Tag, capture, seen.Sites, seen.RollingRejects, "");
    }

    private static async Task<CameraFrame> PixelsAsync(IPhoneSession session, long seq, CancellationToken ct)
    {
        var p = await session.PictureAsync(seq, ct).ConfigureAwait(false);
        return new CameraFrame(p.Gray, p.Width, p.Height, p.CaptureNodeNs, p.Seq);
    }

    public void Dispose()
    {
        // The session is the hub's: the phone stays connected after the scan.
    }
}
