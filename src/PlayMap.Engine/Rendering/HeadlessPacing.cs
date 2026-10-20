using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Render.Timing;

namespace PlayMap.Engine.Rendering;

/// <summary>Each output with no window measured against its own refresh target, its configured
/// <see cref="Output.RefreshHz"/>: fed every instant the render loop finished a frame, it counts the refresh slots of
/// that output which passed without a new picture. The headless loop runs at the fastest such target
/// (<see cref="LoopRateHz"/>), so an output is served at its rate and a slow frame shows as dropped on the outputs it
/// starved. Its deadline waits go through the machine platform's timer (<see cref="WaitUntil"/>). Render thread only; nothing
/// allocates per frame.</summary>
internal sealed class HeadlessPacing : IDisposable
{
    private readonly Dictionary<string, FramePacer> _pacers = new(StringComparer.Ordinal);
    private readonly List<string> _gone = [];
    private IDeadlineTimer? _timer;

    /// <summary>Waits until the deadline (ns, the machine's monotonic clock) or until <paramref name="wake"/> is signalled, on the
    /// platform's deadline timer (<see cref="ThreadClaims.CreateTimer"/>), made at the first wait: on Windows under TIMER HIGH the
    /// high-resolution waitable timer, which wakes near its due time without raising the system's timer resolution; elsewhere, and
    /// under TIMER DEFAULT, the runtime's own wait.</summary>
    public void WaitUntil(long deadlineNs, WaitHandle wake)
    {
        _timer ??= ThreadClaims.CreateTimer(MonoClock.Instance);
        _ = _timer.WaitUntil(deadlineNs, wake);
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>One pacer per enabled output at its refresh; a changed refresh starts its count again.</summary>
    public void Sync(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var id in _pacers.Keys)
            if (project.FindOutput(id) is not { Enabled: true })
                _gone.Add(id);
        foreach (var id in _gone)
            _pacers.Remove(id);
        _gone.Clear();
        foreach (var o in project.Outputs)
        {
            if (!o.Enabled)
                continue;
            var hz = TargetHz(o);
            if (!_pacers.TryGetValue(o.Id, out var pacer) || Math.Abs(pacer.NominalHz - hz) > 1e-6)
                _pacers[o.Id] = new FramePacer(hz);
        }
    }

    /// <summary>The loop finished a frame (every output's picture moved on) at this instant.</summary>
    public void OnPresented(long presentNs)
    {
        foreach (var pacer in _pacers.Values)
            pacer.OnPresented(presentNs);
    }

    /// <summary>An output's pacing against its own refresh (null for one not enabled).</summary>
    public FramePacer? Of(string outputId) => _pacers.GetValueOrDefault(outputId);

    /// <summary>The rate the loop runs at with no window to pace it: the fastest enabled output's refresh target, or
    /// <paramref name="fallbackHz"/> (the project's frame rate) when none is enabled.</summary>
    public static double LoopRateHz(Project project, double fallbackHz)
    {
        ArgumentNullException.ThrowIfNull(project);
        var hz = 0.0;
        foreach (var o in project.Outputs)
            if (o.Enabled)
                hz = Math.Max(hz, TargetHz(o));
        return hz > 0 ? hz : fallbackHz;
    }

    /// <summary>An output's refresh target: its configured rate, 60 when it names none.</summary>
    public static double TargetHz(Output output) =>
        output.RefreshHz is > 1 and <= 1000 && double.IsFinite(output.RefreshHz) ? output.RefreshHz : 60;
}
