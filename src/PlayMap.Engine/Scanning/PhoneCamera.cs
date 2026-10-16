using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Net.Phone;

namespace PlayMap.Engine.Scanning;

/// <summary>
/// A phone as a camera (a camera rig of kind Phone, its source the phone's id): the phone's session on the hub. A scan drives it
/// lock-step (<see cref="ICuedScanCamera"/>). Anything that reads frames instead (an align run, a camera view) gets them on request:
/// each "still" cue is answered with the phone's next picture, stamped with when it was taken on the node's clock, one after another
/// while frames are read — and never once a scan drives the phone, whose cues it would replace. A phone that is not connected does not
/// open, with the words that say how to connect it; one that goes ends every wait with its words.
/// </summary>
public sealed class PhoneCamera : ICuedScanCamera
{
    private readonly PhoneHub _hub;
    private readonly CameraRig _rig;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private IPhoneSession? _session;
    private volatile string _problem = "";
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CameraFrame? _latest;
    private Task? _pump;
    private bool _cued;
    private long _generation;

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

    /// <summary>The phone's next picture after <paramref name="afterGeneration"/>, asked for with a still cue (null within the timeout
    /// when none came, or while a scan drives the phone).</summary>
    public async Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct)
    {
        Task arrived;
        lock (_gate)
        {
            if (_session is null || _cued)
            {
                if (_cued)
                    _problem = $"{_rig.Name} is taking a scan's patterns";
                return null;
            }
            if (_latest is { } f && f.Generation > afterGeneration)
                return f;
            _pump ??= Task.Run(() => PumpAsync(_session, _stop.Token), CancellationToken.None);
            arrived = _arrived.Task;
        }
        try
        {
            await arrived.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        lock (_gate)
            return _latest is { } f && f.Generation > afterGeneration ? f : null;
    }

    /// <summary>Stills, one after another, until the camera is closed, a scan takes it or the phone goes.</summary>
    private async Task PumpAsync(IPhoneSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_cued)
                        return;
                }
                var now = Core.Time.MonoClock.Instance.NowNs;
                var seen = await session.CueAsync(new PhoneCue(0, "still", "", 0, 0, now, now, 0, 0), ct).ConfigureAwait(false);
                if (seen.Outcome != "seen")
                    continue;
                var p = await session.PictureAsync(seen.Seq, ct).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                var at = session.HasClock && double.IsFinite(seen.CapturePageMs) ? session.ToNodeNs(seen.CapturePageMs) : Core.Time.MonoClock.Instance.NowNs;
                TaskCompletionSource done;
                lock (_gate)
                {
                    _latest = new CameraFrame(p.Gray, p.Width, p.Height, at, ++_generation);
                    done = _arrived;
                    _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                done.TrySetResult();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or PhoneGoneException or TimeoutException or ObjectDisposedException)
        {
            _problem = ex is PhoneGoneException ? ex.Message : $"{_rig.Name} stopped sending pictures";
        }
        finally
        {
            lock (_gate)
                _pump = null;
        }
    }

    private IPhoneSession Session => _session ?? throw new InvalidOperationException(_problem.Length > 0 ? _problem : $"{_rig.Name} is not open");

    /// <summary>From here on a scan drives the phone: the stills stop, so its cues are the only ones the phone sees.</summary>
    private void TakeForScan()
    {
        lock (_gate)
            _cued = true;
    }

    public async Task<CuedPrepared> PrepareAsync(CuedPrepare request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        TakeForScan();
        var p = await Session.PrepareAsync(new PhonePrepare(request.Profile, request.DisplayHz, request.Settle.AgreeTolerance, request.Settle.StableFrames), ct)
            .ConfigureAwait(false);
        return new CuedPrepared(p.Exposure, p.Focus, p.WhiteBalance, p.Exposure != "locked", p.Words);
    }

    public Task SetSitesAsync(string projectorId, CellLocateResult located, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(located);
        return Session.SetSitesAsync(new PhoneSites(projectorId, located.Chosen, located.Lit, located.MinSites), ct);
    }

    public int Moves => _session?.Moves ?? 0;

    public TimeSpan? Patience => null;

    /// <summary>A phone takes its pictures itself: SCAN NEXT shows its pattern again instead.</summary>
    public bool TakeNow() => false;

    public async Task<CuedPicture> CueAsync(PatternCue cue, CancellationToken ct, CancellationToken scan)
    {
        ArgumentNullException.ThrowIfNull(cue);
        TakeForScan();
        var session = Session;
        var seen = await session.CueAsync(new PhoneCue(0, cue.Kind, cue.ProjectorId, cue.Index, cue.Tag, cue.PresentNs, cue.EarliestNs, cue.Retake,
            cue.MinChange), ct).ConfigureAwait(false);
        if (seen.Outcome != "seen")
            return new CuedPicture("stuck", null, 0, 0, seen.Sites, seen.RollingRejects, seen.Why.Length > 0 ? seen.Why : "no picture");
        // Without a clock exchange yet the page's instant cannot be placed on the node's clock: no timing check for it (0).
        var capture = double.IsFinite(seen.CapturePageMs) && session.HasClock ? session.ToNodeNs(seen.CapturePageMs) : 0;
        // The picture outlives the cue's wait: the scan's own token ends the wait for it.
        return new CuedPicture("seen", PixelsAsync(session, seen.Seq, scan), seen.Tag, capture, seen.Sites, seen.RollingRejects, "");
    }

    private static async Task<CameraFrame> PixelsAsync(IPhoneSession session, long seq, CancellationToken ct)
    {
        var p = await session.PictureAsync(seq, ct).ConfigureAwait(false);
        return new CameraFrame(p.Gray, p.Width, p.Height, p.CaptureNodeNs, p.Seq);
    }

    /// <summary>Stops the stills; the session is the hub's, so the phone stays connected.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
