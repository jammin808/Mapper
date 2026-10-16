using PlayMap.Core.Sync;
using PlayMap.Engine.Presentation;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using PlayMap.Render.Sync;
using PlayMap.Render.Timing;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-output's side of the render host: presentation from the display's side (<see cref="IPresentBackend"/>) and the frame
/// lock (<see cref="IFrameLockBackend"/>), both published from the render thread and read from any thread.</summary>
public sealed partial class RenderHost : IPresentBackend, IFrameLockBackend
{
    /// <summary>Settled frames every display output presents before a DXGI start counts as up (its fuse goes).</summary>
    private const int FuseFrames = 120;

    private volatile bool _firstFrames;
    private volatile bool _fuseCleared;
    private volatile LatencyPlan _latency = LatencyPlan.None;
    private double _groupAirMs;
    private double _plannedGroupAirMs;

    partial void ComposeOutput(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // The output's frame inputs are the host's own (the show time of the vblank, chosen in Frame); nothing to add.
    }

    partial void OnContextOutput(RenderEngine renderer)
    {
        // The presenters are made by OutputWindows from the platform; nothing else lives in the context.
    }

    partial void OnContextLostOutput()
    {
        // As OnContextOutput.
    }

    // ---- IPresentBackend ----------------------------------------------------------------------------------

    public IReadOnlyList<OutputPresentView> Present()
    {
        lock (_healthGate)
        {
            var list = new List<OutputPresentView>(_health.Length);
            foreach (var h in _health)
                if (h.HasPresent)
                    list.Add(new OutputPresentView(h.OutputId, h.Present));
            return list;
        }
    }

    public MachinePresentView Machine()
    {
        var platform = _platform;
        double p95 = 0;
        var frames = (int)Math.Min(Interlocked.Read(ref _frames), FrameRing);
        if (frames > 0)
        {
            lock (_frameMsGate)
            {
                Array.Copy(_presentMs, _presentMsSorted, frames);
                Array.Sort(_presentMsSorted, 0, frames);
                p95 = _presentMsSorted[Math.Clamp((int)Math.Ceiling(0.95 * frames) - 1, 0, frames - 1)];
            }
        }
        var dialect = platform?.Context is { } c ? GlDialects.Word(c.Dialect) : "";
        // A headless node, or a platform with no window system, presents nothing: its outputs are offscreen.
        var dxgi = platform?.Path == PresentPath.DxgiFlip;
        var path = dxgi ? "dxgi" : _options.Windows && platform?.Path == PresentPath.GlWindow ? "gl" : "offscreen";
        var adapter = platform is { Adapter.Length: > 0 } ? platform.Adapter : _rendererName;
        return new MachinePresentView(dxgi ? "d3d11" : "gl", adapter, path, dxgi, platform?.Words ?? "", 0, Math.Round(p95, 2), dialect,
            platform?.Api ?? "")
        {
            FuseArmed = _options.Fuse is not null && !_fuseCleared,
            FirstFrames = _firstFrames,
            AirLatencyMs = Math.Round(_latency.AirLatencyMs > 0 ? _latency.AirLatencyMs : _options.DisplayLatencyNs / 1e6, 3),
            LatencyWords = _latency.Words,
        };
    }

    /// <summary>The node's latency plan now: its air latency (the largest output's, or the group's when larger) and each output's
    /// whole-frame delay. Any thread.</summary>
    public LatencyPlan Latency => _latency;

    /// <summary>The group's air latency (a sync group's slowest node), ms: the node's own is raised to it, never lowered; 0 lets it go.
    /// Any thread; the render thread plans again at its next frame.</summary>
    public void SetGroupAirLatency(double ms) => Volatile.Write(ref _groupAirMs, double.IsFinite(ms) && ms > 0 ? ms : 0);

    /// <summary>Render thread: the plan for the project's enabled outputs, handed to the renderer's delay rings.</summary>
    private void PlanLatency(Project project)
    {
        var group = Volatile.Read(ref _groupAirMs);
        _plannedGroupAirMs = group;
        var plan = LatencyPlan.Make(project.Outputs.Where(o => o.Enabled).Select(o => (o.Id, o.LatencyMs, o.RefreshHz)), group);
        foreach (var d in plan.Outputs)
            _renderer?.SetDelay(d.OutputId, d.DelayFrames, d.ResidualMs);
        _latency = plan;
    }

    /// <summary>Render thread, once a frame: the air latency the frame is drawn for — the plan's, or the configured display latency
    /// when no output names one; planned again when the group's moved.</summary>
    private long AirLatencyNs(Project project)
    {
        if (Volatile.Read(ref _groupAirMs) != _plannedGroupAirMs)
            PlanLatency(project);
        var plan = _latency;
        return plan.AirLatencyMs > 0 ? plan.AirLatencyNs : _options.DisplayLatencyNs;
    }

    /// <summary>Every display output of the DXGI start has presented 120 frames on a settled clock (render thread, under the health
    /// gate): what the fuse waits for.</summary>
    private bool FirstFramesReached()
    {
        var any = false;
        foreach (var h in _health)
        {
            if (!h.Open)
                continue;
            if (!h.HasPresent || h.Present.Presented < FuseFrames || !h.Present.Settled)
                return false;
            any = true;
        }
        return any;
    }

    public bool ConfirmFirstFrames()
    {
        if (!_firstFrames || _fuseCleared || _options.Fuse is not { } fuse)
            return false;
        _fuseCleared = true;
        return fuse.Clear();
    }

    public async Task<IReadOnlyList<OutputPresentView>> ResetAsync(IReadOnlyCollection<string>? outputIds, CancellationToken ct)
    {
        var before = await InvokeAsync(renderer =>
        {
            var list = new List<OutputPresentView>();
            foreach (var id in outputIds ?? (IReadOnlyCollection<string>)[.. Present().Select(p => p.OutputId)])
                if (renderer.ResetPresent(id) is { } old)
                    list.Add(new OutputPresentView(id, old));
            return list;
        }).WaitAsync(ct).ConfigureAwait(false);
        return before;
    }

    // ---- IFrameLockBackend --------------------------------------------------------------------------------

    public string Driver => _lock.Driver.Name;

    public string DriverWords => _lock.Driver.Words;

    public FrameLockBoard? Board => _lock.Board;

    public LockIntent Intent => _lock.Intent;

    public IReadOnlyCollection<string> HeldOut => _lock.HeldOut;

    public IReadOnlyDictionary<string, LockView> Locks() => _lock.Views;

    public string TimeSource => _lock.TimeSource;

    public void Apply(uint swapGroup, uint barrier)
    {
        _lock.Apply(swapGroup, barrier);
        _wake.Set();
    }

    public void LeaveAll() => _lock.Release();

    public void Leave(IReadOnlyCollection<string> outputIds) => _lock.Leave(outputIds);

    public void Rejoin(IReadOnlyCollection<string> outputIds) => _lock.Rejoin(outputIds);

    public Task<IReadOnlyDictionary<string, LockView>> ObserveAsync(IReadOnlyCollection<string> outputIds, Func<LockView, bool> settled,
        TimeSpan within, CancellationToken ct) => _lock.ObserveAsync(outputIds, settled, within, ct);

    public LockAnchor? LocalAnchor() => _lock.LocalAnchor();

    public void OfferAnchor(LockAnchor anchor) => _lock.OfferAnchor(anchor);

    /// <summary>The swap group and barrier SYNC APPLY joins on this node (NodeSettings.Present).</summary>
    public (uint Group, uint Barrier) SwapGroup => (_options.SwapGroup, _options.SwapBarrier);

    /// <summary>A lock view moved (render thread): the engine hears it off this thread.</summary>
    private void OnLockChanged()
    {
        Volatile.Read(ref _facts)?.Raise();
    }

    /// <summary>An output fell out of the lock (render thread): a fault row and a show event, off this thread.</summary>
    private void OnFellOut(string outputId, string words)
    {
        if (Volatile.Read(ref _engine) is not { } engine)
            return;
        var leaderNs = engine.LeaderNowNs;
        ThreadPool.UnsafeQueueUserWorkItem(static s =>
        {
            s.Engine.Journal.Add("fault", $"{s.OutputId} fell out of the frame lock: {s.Words}", ok: false);
            s.Engine.RaiseEvent("sync.fellout", s.LeaderNs, w =>
            {
                w.WriteString("output", s.OutputId);
                w.WriteString("words", s.Words);
            });
        }, (Engine: engine, OutputId: outputId, Words: words, LeaderNs: leaderNs), preferLocal: false);
    }
}
