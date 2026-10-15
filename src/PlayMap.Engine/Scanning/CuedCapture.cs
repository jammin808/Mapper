using System.Globalization;
using PlayMap.Core.Phone;
using PlayMap.Core.Scan;

namespace PlayMap.Engine.Scanning;

/// <summary>How a view was paired, as STATE.scan.pairing shows it: by tag (observed) or by timing (the camera saw no tag places),
/// the tag places used, the rolling-shutter mixes the camera threw away, the patterns shown again, the pictures refused because the
/// tag they showed was another pattern's, the pictures whose capture time came before their present, the camera's worst
/// auto-exposure drift (steady profile) and the exposure path the camera ran.</summary>
public sealed record ScanPairing(string Mode, int Sites, int RollingRejects, int Retakes, int Caught, int TimingDisagrees, double AeDriftPct,
    string Exposure, string Words);

/// <summary>
/// The scan runner's lock-step path for a camera told what is on the glass (<see cref="ICuedScanCamera"/>, a phone), per projector:
/// <list type="number">
/// <item>prepare — the probe (the coarsest Gray level) on the glass while the camera settles and sets its exposure profile;</item>
/// <item>locate — twelve frames that name every candidate tag cell; the decode chooses up to three sites and the camera is told where
/// they land;</item>
/// <item>patterns — each pattern with its tag painted at the sites goes up, and the next waits until the camera says it saw this one
/// (its tag at every lit site in two consecutive agreeing frames); uploads trail behind, at most the in-flight limit; a camera
/// stuck on a pattern has it shown again, up to the retake limit, then the view fails with the camera's words;</item>
/// <item>verify — each picture's tag is read again on the node: one that shows another pattern is refused and its pattern shot again
/// (observed, never assumed); a picture that never arrives is shot again too;</item>
/// <item>end check — the first coded pattern shown once more: a picture that differs from the first (brightness levelled) by more than
/// the move limit means the camera moved, and the view fails.</item>
/// </list>
/// The steady profile skips the full white and black (made afterwards from the coarse pairs) and levels the exposure drift. Timing
/// is the cross-check: a picture taken before its pattern was presented is counted, never trusted over its tag.
/// </summary>
public sealed partial class ScanRunner
{
    private readonly Lock _cueGate = new();
    private Action? _cueNext;
    private bool _nextAsked;

    /// <summary>SCAN NEXT: the pattern a cued camera waits on now is shown again and asked for afresh (a phone that was bumped or
    /// lost its view). False when no scan waits on a camera.</summary>
    public bool Next()
    {
        Action? next;
        lock (_cueGate)
        {
            next = _cueNext;
            _cueNext = null;
            _nextAsked = next is not null;
        }
        next?.Invoke();
        return next is not null;
    }

    /// <summary>Whether a scan waits on a cued camera's picture now.</summary>
    public bool WaitingForPicture
    {
        get
        {
            lock (_cueGate)
                return _cueNext is not null;
        }
    }

    /// <summary>One cue to the camera, re-shown by SCAN NEXT, given up after the pattern timeout; null when it timed out.</summary>
    private async Task<CuedPicture?> AskAsync(ICuedScanCamera camera, Func<CancellationToken, Task<long>> show, Func<long, int, PatternCue> cue,
        int retake, CancellationToken ct)
    {
        while (true)
        {
            var present = await show(ct).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(_options.PatternTimeout);
            lock (_cueGate)
            {
                _cueNext = wait.Cancel;
                _nextAsked = false;
            }
            try
            {
                return await camera.CueAsync(cue(present, retake), wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                bool next;
                lock (_cueGate)
                    next = _nextAsked;
                if (!next)
                    return null;
            }
            finally
            {
                lock (_cueGate)
                    _cueNext = null;
            }
        }
    }

    private sealed class Pairing
    {
        public int Rolling;
        public int Retakes;
        public int Caught;
        public int TimingDisagrees;
    }

    /// <summary>One projector through a cued camera: its frames (plan order, the camera's raster), the tag shadow to leave out of the
    /// decode and the pairing facts.</summary>
    private async Task<(byte[][] Frames, int Width, int Height, CellLocateResult Located, ScanPairing Pairing)> CaptureCuedAsync(IScanDisplay display,
        ICuedScanCamera camera, string cameraId, int j, Part part, Track track, List<string> notes, CancellationToken ct)
    {
        var views = ViewsOf();
        var plan = part.Plan;
        var output = part.Output!;
        var pj = part.Projector;
        var (w0, h0) = (output.Width, output.Height);
        var hz = output.RefreshHz > 0 ? output.RefreshHz : 60;
        var framePeriodNs = (long)(1e9 / hz);
        Task<long> Show(byte[] raster, CancellationToken c) => display.ShowAsync(output.Id, raster, w0, h0, c);

        // Prepare: the probe on the glass while the camera settles.
        var probeIndex = FirstCoded(plan);
        track.Step("running", "preparing", $"{pj.Name}: {camera.Name} is settling its exposure", j, "preparing");
        await Show(plan.Render(probeIndex), ct).ConfigureAwait(false);
        using (var prepareWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            prepareWait.CancelAfter(_options.PatternTimeout + _options.PatternTimeout);
            CuedPrepared prepared;
            try
            {
                prepared = await camera.PrepareAsync(new CuedPrepare(camera.Profile, hz, _options.Settle), prepareWait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"{camera.Name} did not answer while setting its exposure");
            }
            notes.Add($"{pj.Name}: {camera.Name} — {prepared.Words}.");
            return await CaptureCuedViewAsync(camera, cameraId, j, part, track, notes, prepared, Show, framePeriodNs, views, ct).ConfigureAwait(false);
        }
    }

    private static int FirstCoded(ScanPlan plan)
    {
        for (var i = 0; i < plan.Count; i++)
        {
            if (plan.Frames[i].Kind is ScanFrameKind.Gray)
                return i;
        }
        return 0;
    }

    private async Task<(byte[][] Frames, int Width, int Height, CellLocateResult Located, ScanPairing Pairing)> CaptureCuedViewAsync(ICuedScanCamera camera,
        string cameraId, int j, Part part, Track track, List<string> notes, CuedPrepared prepared, Func<byte[], CancellationToken, Task<long>> show,
        long framePeriodNs, ScanViews? views, CancellationToken ct)
    {
        var plan = part.Plan;
        var pj = part.Projector;
        var (pw, ph) = (plan.ProjectorWidth, plan.ProjectorHeight);
        var steady = prepared.SteadyPlan;
        var facts = new Pairing();

        // Locate.
        var candidates = CellLocate.Candidates(pw, ph);
        var background = steady ? _options.LocateBackgroundSteady : (byte)0;
        var locate = new byte[CellLocate.Frames][];
        int w = 0, h = 0;
        for (var k = 0; k < CellLocate.Frames; k++)
        {
            track.Step("running", "locating", string.Create(CultureInfo.InvariantCulture,
                $"{pj.Name}: finding the tag places ({k + 1} of {CellLocate.Frames})"), j, "locating");
            var raster = new byte[pw * ph];
            CellLocate.RenderFrame(k, raster, pw, ph, candidates, background);
            var index = k;
            var frame = await TakeAsync(camera, c => show(raster, c), (present, retake) =>
                new PatternCue("locate", pj.Id, index, 0, present, present, retake, LocateMinChange), facts, $"locate frame {k + 1}", ct).ConfigureAwait(false);
            (w, h) = Size(frame, w, h);
            locate[k] = frame.Gray;
            views?.CameraFrame(cameraId, frame.Gray, frame.Width, frame.Height, frame.ArrivalNs);
        }
        var located = await Task.Run(() => CellLocate.Decode(w, h, locate, candidates, _options.MaxTagSites), ct).ConfigureAwait(false);
        notes.Add($"{pj.Name}: {located.Words}.");
        await camera.SetSitesAsync(pj.Id, located, ct).ConfigureAwait(false);
        var tagged = located.Chosen.Length > 0;

        // Patterns, lock-step; the pictures trail behind.
        byte[] Raster(int i)
        {
            var r = plan.Render(i);
            if (tagged)
                TagOverlay.Apply(r, pw, located.Sites, FrameTag.Value(i), plan.Options.High, plan.Options.Low);
            return r;
        }
        PatternCue Cue(int i, long present, int retake) =>
            new("pattern", pj.Id, i, tagged ? FrameTag.Value(i) : 0, present, present, retake, _options.Settle.MinChange);
        var pending = new Task<CameraFrame>?[plan.Count];
        for (var i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (steady && (i == plan.WhiteIndex || i == plan.BlackIndex))
                continue;
            var index = i;
            var raster = Raster(i);
            var picture = await SeenAsync(camera, c => show(raster, c), (present, retake) => Cue(index, present, retake), facts,
                $"pattern {i + 1} ({plan.Frames[i].Name})", framePeriodNs, ct).ConfigureAwait(false);
            pending[i] = picture.Pixels;
            track.Pattern(j, i, string.Create(CultureInfo.InvariantCulture,
                $"{pj.Name}: pattern {i + 1} of {plan.Count} ({plan.Frames[i].Name}) seen by {camera.Name}"));
            while (pending.Count(t => t is { IsCompleted: false }) >= _options.MaxInFlight)
                await Task.WhenAny(pending.Where(t => t is { IsCompleted: false })!).ConfigureAwait(false);
        }

        // Verify: every picture's tag read on the node; one that shows another pattern, or never came, is shot again.
        track.Step("running", "checking", $"{pj.Name}: checking each picture's tag", j, "checking");
        var frames = new byte[plan.Count][];
        for (var i = 0; i < plan.Count; i++)
        {
            if (pending[i] is not { } p)
                continue;
            var index = i;
            for (var round = 0; ; round++)
            {
                var frame = await PictureOrNullAsync(p, ct).ConfigureAwait(false);
                var why = frame is null ? "its picture never arrived"
                    : frame.Width != w || frame.Height != h ? "the camera changed its picture size"
                    : tagged && FrameTag.ReadFrame(frame.Gray, w, h, located.Chosen, located.MinSites) is var read
                        && !(read.Outcome == FrameTagOutcome.Read && read.Value == FrameTag.Value(i))
                        ? string.Create(CultureInfo.InvariantCulture, $"its picture shows {(read.Outcome == FrameTagOutcome.Read ? "pattern tag " + read.Value : "no tag")}")
                    : null;
                if (why is null)
                {
                    frames[i] = frame!.Gray;
                    views?.CameraFrame(cameraId, frame.Gray, frame.Width, frame.Height, frame.ArrivalNs);
                    break;
                }
                if (frame is not null && why.StartsWith("its picture shows", StringComparison.Ordinal))
                    facts.Caught++;
                if (round >= _options.MaxRetakes)
                    throw new InvalidOperationException($"{pj.Name}: pattern {i + 1} could not be taken — {why}");
                facts.Retakes++;
                var raster = Raster(i);
                var again = await SeenAsync(camera, c => show(raster, c), (present, retake) => Cue(index, present, retake + 1 + round), facts,
                    $"pattern {i + 1} again", framePeriodNs, ct).ConfigureAwait(false);
                p = again.Pixels!;
            }
        }

        // End check: the first coded pattern again, against its first picture.
        var check = FirstCoded(plan);
        var checkRaster = Raster(check);
        var last = await SeenAsync(camera, c => show(checkRaster, c), (present, retake) => Cue(check, present, retake), facts, "the closing check",
            framePeriodNs, ct).ConfigureAwait(false);
        var closing = await PictureOrNullAsync(last.Pixels!, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{camera.Name}'s closing picture never arrived");
        var moved = MovedLevels(frames[check], closing.Gray, located.Lit, w);
        if (moved > _options.MoveCheckLevels)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{camera.Name} moved during the view: the closing picture differs from the first by {moved:0.0} levels — set it down, keep it still and scan again"));

        var drift = 0.0;
        if (steady)
            drift = GainNormaliser.PrepareSteady(plan, frames);
        if (facts.TimingDisagrees > 3)
            notes.Add($"{pj.Name}: the phone's clock or latency is off — pairing by tags only.");
        var mode = tagged ? "tag" : "timing";
        var words = tagged
            ? string.Create(CultureInfo.InvariantCulture, $"paired by tag at {located.Chosen.Length} place{(located.Chosen.Length == 1 ? "" : "s")}")
            : "the camera sees none of the tag places: paired by timing, keep the phone very still";
        return (frames, w, h, located, new ScanPairing(mode, located.Chosen.Length, facts.Rolling, facts.Retakes, facts.Caught, facts.TimingDisagrees,
            drift, prepared.Exposure, words));
    }

    private const double LocateMinChange = 3;

    private static (int, int) Size(CameraFrame f, int w, int h)
    {
        if (w != 0 && (f.Width != w || f.Height != h))
            throw new InvalidOperationException("the camera changed its picture size during the scan");
        return (f.Width, f.Height);
    }

    private static async Task<CameraFrame?> PictureOrNullAsync(Task<CameraFrame> p, CancellationToken ct)
    {
        try
        {
            return await p.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A cue seen (its picture on the way), shown again while the camera is stuck, up to the retake limit.</summary>
    private async Task<CuedPicture> SeenAsync(ICuedScanCamera camera, Func<CancellationToken, Task<long>> show, Func<long, int, PatternCue> cue,
        Pairing facts, string what, long framePeriodNs, CancellationToken ct)
    {
        var why = "";
        for (var retake = 0; retake <= _options.MaxRetakes; retake++)
        {
            if (retake > 0)
                facts.Retakes++;
            long present = 0;
            var picture = await AskAsync(camera, async c => present = await show(c).ConfigureAwait(false), cue, retake, ct).ConfigureAwait(false);
            if (picture is { Seen: true } seen)
            {
                facts.Rolling += seen.RollingRejects;
                if (seen.CaptureNs > 0 && seen.CaptureNs < present - framePeriodNs)
                    facts.TimingDisagrees++;
                var timeout = _options.PatternTimeout;
                return seen with { Pixels = seen.Pixels!.WaitAsync(timeout + timeout, ct) };
            }
            facts.Rolling += picture?.RollingRejects ?? 0;
            why = picture?.Why ?? string.Create(CultureInfo.InvariantCulture, $"it said nothing for {_options.PatternTimeout.TotalSeconds:0.#} s");
        }
        throw new InvalidOperationException($"{camera.Name} could not take {what}: {why}");
    }

    private async Task<CameraFrame> TakeAsync(ICuedScanCamera camera, Func<CancellationToken, Task<long>> show, Func<long, int, PatternCue> cue,
        Pairing facts, string what, CancellationToken ct)
    {
        for (var round = 0; ; round++)
        {
            var seen = await SeenAsync(camera, show, cue, facts, what, 0, ct).ConfigureAwait(false);
            if (await PictureOrNullAsync(seen.Pixels!, ct).ConfigureAwait(false) is { } frame)
                return frame;
            if (round >= _options.MaxRetakes)
                throw new InvalidOperationException($"{camera.Name}'s picture of {what} never arrived");
            facts.Retakes++;
        }
    }

    /// <summary>Mean absolute difference over the lit rectangle after levelling the two pictures' brightness.</summary>
    private static double MovedLevels(byte[] first, byte[] again, PixelRect lit, int width)
    {
        var r = lit.IsEmpty ? new PixelRect(0, 0, width, first.Length / width) : lit;
        double sa = 0, sb = 0;
        var n = 0;
        for (var y = r.Y; y < r.Bottom; y += 2)
        {
            for (var x = r.X; x < r.Right; x += 2)
            {
                sa += first[y * width + x];
                sb += again[y * width + x];
                n++;
            }
        }
        if (n == 0 || sb <= 0)
            return 0;
        var scale = sa / sb;
        double d = 0;
        for (var y = r.Y; y < r.Bottom; y += 2)
        {
            for (var x = r.X; x < r.Right; x += 2)
                d += Math.Abs(first[y * width + x] - again[y * width + x] * scale);
        }
        return d / n;
    }
}
