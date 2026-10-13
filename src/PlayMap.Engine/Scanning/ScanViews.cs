using PlayMap.Core.Model;
using PlayMap.Core.Scan;
using PlayMap.Core.Time;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Scanning;

/// <summary>
/// The scan side's feeds on the node's preview views (<see cref="EngineCore.Previews"/>): the desk's
/// <c>VIEW &lt;view&gt; SHOW camera:&lt;id&gt;</c> and <c>scan:&lt;id&gt;:smart|confidence</c>.
/// <list type="bullet">
/// <item>A camera view shows what the camera sees: the frames a running scan takes through it; otherwise, for a
/// capture camera, the camera itself, opened for the views and let go when a scan needs it (the scan holds it from
/// before it opens until after it has closed) or when its last view goes; for the simulated camera, its scene under
/// white light.</item>
/// <item>A scan view shows the first projector's smart scan or the decode's confidence, drawn once when shown — or,
/// for the scan running now, when it finishes. A scan's pictures are kept in memory until the node stops.</item>
/// </list>
/// A camera's pictures reach its views at most every <see cref="MinGapNs"/> (a glance, not a recording). Start and
/// a handle's Dispose are called under the registry's lock: neither calls back into the registry, and every picture is
/// written outside this class's own lock. Thread-safe.
/// </summary>
internal sealed class ScanViews
{
    /// <summary>The shortest time between two pictures of one camera on its views (25 a second).</summary>
    public const long MinGapNs = 40_000_000;

    private readonly ScanRunner _runner;
    private readonly EngineCore _engine;
    private readonly Func<CameraRig, IScanCamera?> _open;
    private readonly bool _anyCamera;
    private readonly int _simulatedWidth;
    private readonly int _simulatedHeight;
    private readonly Lock _gate = new();
    private readonly List<Watch> _watches = [];
    private readonly Dictionary<string, Pump> _pumps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _ended = new(StringComparer.Ordinal);
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastNs = new(StringComparer.Ordinal);
    private bool _stopped;

    /// <param name="open">Opens a capture camera for its views (the scan's own opener).</param>
    /// <param name="anyCamera">True when <paramref name="open"/> was handed in (tests): every rig is tried.</param>
    public ScanViews(ScanRunner runner, EngineCore engine, Func<CameraRig, IScanCamera?> open, bool anyCamera, int simulatedWidth, int simulatedHeight)
    {
        _runner = runner;
        _engine = engine;
        _open = open;
        _anyCamera = anyCamera;
        _simulatedWidth = simulatedWidth;
        _simulatedHeight = simulatedHeight;
        Cameras = new Feed(this, camera: true);
        Scans = new Feed(this, camera: false);
    }

    /// <summary>The feed for "camera" views.</summary>
    public IPreviewFeed Cameras { get; }

    /// <summary>The feed for "scan" views.</summary>
    public IPreviewFeed Scans { get; }

    /// <summary>One open view: a camera's (by camera id) or a scan's (by scan id, with its variant).</summary>
    private sealed record Watch(bool Camera, string Target, string? Variant, PreviewSink Sink);

    /// <summary>A capture camera opened for its views: stopped by its token, ended when it has let the device go.</summary>
    private sealed class Pump
    {
        public CancellationTokenSource Stop { get; } = new();

        public Task Ended { get; set; } = Task.CompletedTask;

        public bool Done { get; set; }
    }

    private sealed class Feed(ScanViews owner, bool camera) : IPreviewFeed
    {
        public IDisposable? Start(ViewRequest request, PreviewSink sink, out string why) =>
            camera ? owner.StartCamera(request, sink, out why) : owner.StartScan(request, sink, out why);
    }

    private sealed class Handle(ScanViews owner, Watch watch) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                owner.Drop(watch);
        }
    }

    // ---- cameras ----------------------------------------------------------------------------------------------

    private IDisposable? StartCamera(ViewRequest request, PreviewSink sink, out string why)
    {
        why = "";
        var target = request.TargetId ?? "";
        var stage = _engine.Store.Project.Stage;
        var rig = stage.Cameras.Find(target) ?? stage.Cameras.FirstOrDefault(c => c.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        var simulated = rig is null ? target.Equals(SimulatedRig.CameraId, StringComparison.OrdinalIgnoreCase) : rig.SourceKind == CameraSourceKind.Simulated;
        if (rig is null && !simulated)
        {
            why = $"no camera {target}";
            return null;
        }
        if (rig is not null && !simulated && !_anyCamera && CaptureCamera.OptionsFor(rig) is null)
        {
            why = $"{rig.Name} is a {rig.SourceKind.ToString().ToLowerInvariant()} camera, which this node cannot show yet";
            return null;
        }
        var id = rig?.Id ?? SimulatedRig.CameraId;
        var watch = new Watch(true, id, null, sink);
        bool still;
        lock (_gate)
        {
            if (_stopped)
            {
                why = "the node is stopping";
                return null;
            }
            _watches.Add(watch);
            still = simulated && !_held.Contains(id);
            if (!simulated && !_held.Contains(id))
                StartPump(id, rig!);
        }
        if (still)
            _ = Task.Run(() => DrawStill(rig, sink), CancellationToken.None);
        return new Handle(this, watch);
    }

    /// <summary>A picture a camera took (a scan's frame or its own): to every view of that camera, thinned in time.</summary>
    public void CameraFrame(string cameraId, byte[] gray, int width, int height, long stampNs)
    {
        PreviewSink[] sinks;
        lock (_gate)
        {
            if (_stopped)
                return;
            sinks = _watches.Where(w => w.Camera && w.Target == cameraId).Select(w => w.Sink).ToArray();
            if (sinks.Length == 0)
                return;
            if (_lastNs.TryGetValue(cameraId, out var last) && stampNs >= last && stampNs - last < MinGapNs)
                return;
            _lastNs[cameraId] = stampNs;
        }
        foreach (var sink in sinks)
            sink.WriteGray(gray, width, height, width, stampNs);
    }

    /// <summary>A scan takes the camera: its views' own capture lets the device go first (awaited), and from here its
    /// views show the scan's frames.</summary>
    public async Task HoldAsync(string cameraId)
    {
        Task ended;
        lock (_gate)
        {
            _held.Add(cameraId);
            StopPump(cameraId);
            ended = _ended.GetValueOrDefault(cameraId) ?? Task.CompletedTask;
        }
        await ended.ConfigureAwait(false);
    }

    /// <summary>The scan has let the camera go (closed it): views still open on it show the camera again.</summary>
    public void Release(string cameraId)
    {
        var stage = _engine.Store.Project.Stage;
        var rig = stage.Cameras.Find(cameraId);
        PreviewSink[] stills = [];
        lock (_gate)
        {
            _held.Remove(cameraId);
            if (_stopped || !_watches.Any(w => w.Camera && w.Target == cameraId))
                return;
            if (rig is null || rig.SourceKind == CameraSourceKind.Simulated)
                stills = _watches.Where(w => w.Camera && w.Target == cameraId).Select(w => w.Sink).ToArray();
            else
                StartPump(cameraId, rig);
        }
        foreach (var sink in stills)
            DrawStill(rig, sink);
    }

    /// <summary>Under the lock: opens the camera for its views once the previous opening has let it go.</summary>
    private void StartPump(string cameraId, CameraRig rig)
    {
        if (_pumps.ContainsKey(cameraId))
            return;
        var previous = _ended.GetValueOrDefault(cameraId) ?? Task.CompletedTask;
        var pump = new Pump();
        var token = pump.Stop.Token;
        pump.Ended = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                await PumpAsync(cameraId, rig, token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    pump.Done = true;
                    pump.Stop.Dispose();
                }
            }
        }, CancellationToken.None);
        _pumps[cameraId] = pump;
        _ended[cameraId] = pump.Ended;
    }

    /// <summary>Under the lock: asks the camera's own capture to stop (its end is in <see cref="_ended"/>).</summary>
    private void StopPump(string cameraId)
    {
        if (_pumps.Remove(cameraId, out var pump) && !pump.Done)
            pump.Stop.Cancel();
    }

    private async Task PumpAsync(string cameraId, CameraRig rig, CancellationToken ct)
    {
        IScanCamera? camera = null;
        try
        {
            camera = _open(rig);
            if (camera is null || !await camera.OpenAsync(ct).ConfigureAwait(false))
                return;
            var generation = 0L;
            while (!ct.IsCancellationRequested)
            {
                var frame = await camera.NextAsync(generation, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                if (frame is null)
                    continue;
                generation = frame.Generation;
                CameraFrame(cameraId, frame.Gray, frame.Width, frame.Height, frame.ArrivalNs);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A camera that stops giving pictures leaves its views on the last one; a scan through it says why.
        }
        finally
        {
            camera?.Dispose();
        }
    }

    /// <summary>The simulated camera's scene under the first projector's white, in colour.</summary>
    private void DrawStill(CameraRig? rig, PreviewSink sink)
    {
        var project = _engine.Store.Project;
        var (w, h) = rig is { Intrinsics: { Width: > 0, Height: > 0 } k } ? (k.Width, k.Height) : (_simulatedWidth, _simulatedHeight);
        var prior = project.Stage.Projectors.Length > 0 ? project.Stage.Projectors[0].Intrinsics : Intrinsics.FromThrowRatio(256, 160, 1.25);
        var scene = new SimulatedRig(SimulatedRig.SceneOf(rig), w, h, [prior]);
        var colour = scene.Simulator(0, ScanPlan.Create(prior.Width, prior.Height, new ScanPlanOptions { Mode = ScanMode.Fast })).RenderWhiteColour();
        sink.WriteBgra(ScanPictures.FromColour(colour), w, h, w * 4, MonoClock.Instance.NowNs);
    }

    // ---- scans ------------------------------------------------------------------------------------------------

    private IDisposable? StartScan(ViewRequest request, PreviewSink sink, out string why)
    {
        why = "";
        var id = request.TargetId ?? "";
        var outcome = _runner.Find(id);
        var running = _runner.RunningId == id;
        if (outcome is null && !running)
        {
            why = _engine.Store.Project.Stage.Scans.Find(id) is null
                ? $"no scan {id}"
                : $"{id}'s pictures are not in this node's memory (a scan keeps them until the node stops)";
            return null;
        }
        if (outcome is { Views.Count: 0 })
        {
            why = $"{id}'s pictures are not in this node's memory (a scan keeps them until the node stops)";
            return null;
        }
        var watch = new Watch(false, id, request.Variant, sink);
        lock (_gate)
        {
            if (_stopped)
            {
                why = "the node is stopping";
                return null;
            }
            _watches.Add(watch);
        }
        if (outcome is not null)
            _ = Task.Run(() => DrawScan(outcome, watch), CancellationToken.None);
        return new Handle(this, watch);
    }

    /// <summary>A scan finished: its views that were waiting for it draw it.</summary>
    public void Finished(ScanOutcome outcome)
    {
        Watch[] waiting;
        lock (_gate)
            waiting = _watches.Where(w => !w.Camera && w.Target == outcome.Id).ToArray();
        foreach (var watch in waiting)
            DrawScan(outcome, watch);
    }

    private static void DrawScan(ScanOutcome outcome, Watch watch)
    {
        if (outcome.Views.Count == 0)
            return;
        var (_, view) = outcome.Views[0];
        var bgra = watch.Variant == "confidence" ? ScanPictures.Confidence(view) : ScanPictures.Smart(view);
        watch.Sink.WriteBgra(bgra, view.Width, view.Height, view.Width * 4, MonoClock.Instance.NowNs);
    }

    // ---- both -------------------------------------------------------------------------------------------------

    private void Drop(Watch watch)
    {
        lock (_gate)
        {
            _watches.Remove(watch);
            if (watch.Camera && !_watches.Any(w => w.Camera && w.Target == watch.Target))
                StopPump(watch.Target);
        }
    }

    /// <summary>Takes the feeds off the node's views, closes their views (their rings go) and lets every camera go.</summary>
    public async Task StopAsync()
    {
        _engine.Previews.Register("camera", null);
        _engine.Previews.Register("scan", null);
        foreach (var (viewId, request) in _engine.Previews.Shown())
        {
            if (request.Kind is ViewKind.Camera or ViewKind.Scan)
                _engine.Previews.Hide(viewId);
        }
        Task[] ended;
        lock (_gate)
        {
            _stopped = true;
            foreach (var id in _pumps.Keys.ToList())
                StopPump(id);
            ended = [.. _ended.Values];
        }
        await Task.WhenAll(ended).ConfigureAwait(false);
    }
}
