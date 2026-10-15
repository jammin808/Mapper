using System.Globalization;

namespace PlayMap.Core.Media.Optimise;

/// <summary>How the optimise worker may run: fully (idle node), at a trickle (a show plays), not at all for a while after a frame
/// was dropped or late while it ran, or not at all while the show is armed.</summary>
public enum GovernorState
{
    Run,
    Trickle,
    Paused,
    Waiting,
}

/// <summary>What the governor reads each step: armed (the show gate), animating (something plays), the running totals of
/// outputs' dropped frames and on-air sources' late frames, whether a job ran since the last step, and the monotonic now.</summary>
public readonly record struct GovernorInputs(bool Armed, bool Animating, long DroppedTotal, long LateTotal, bool Working, long NowNs);

/// <summary>What the worker is told: the state, CPU threads, the GPU duty (share of time it may draw), the disk rate (MB/s, 0 =
/// unthrottled), whether hardware decode is allowed, and the words STATE shows.</summary>
public readonly record struct GovernorKnobs(GovernorState State, int CpuThreads, double GpuDuty, int DiskMbps, bool HwDecode, string Words)
{
    public bool MayWork => State is GovernorState.Run or GovernorState.Trickle;
}

/// <summary>The governor's thresholds (NodeSettings.Optimise, and the node's cores).</summary>
public sealed record GovernorSettings(double TrickleGpuDuty, int TrickleDiskMbps, int PauseAfterDropS, int Cores, int IdleToRunS = 10)
{
    public static readonly GovernorSettings Default = new(0.15, 50, 60, Environment.ProcessorCount);
}

/// <summary>"Measured, then governed": armed → Waiting; a frame dropped or late (the totals moved) while a job ran → Paused for
/// <see cref="GovernorSettings.PauseAfterDropS"/> after the last one; something plays → Trickle (one thread, GPU duty and disk
/// capped, software decode), and it stays at Trickle until the node has been idle <see cref="GovernorSettings.IdleToRunS"/>;
/// else Run. Pure: no clock of its own — the service steps it with its inputs.</summary>
public sealed class Governor(GovernorSettings settings)
{
    private readonly GovernorSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private long _dropped = -1;
    private long _late = -1;
    private long _pausedUntilNs = long.MinValue;
    private long _lastAnimatingNs = long.MinValue;
    private string _pauseWords = "";

    /// <summary>The last knobs <see cref="Step"/> answered.</summary>
    public GovernorKnobs Current { get; private set; } = new(GovernorState.Run, 1, 1, 0, true, "");

    public GovernorKnobs Step(in GovernorInputs inputs)
    {
        var c = CultureInfo.InvariantCulture;
        var dropped = inputs.DroppedTotal - (_dropped < 0 ? inputs.DroppedTotal : _dropped);
        var late = inputs.LateTotal - (_late < 0 ? inputs.LateTotal : _late);
        _dropped = inputs.DroppedTotal;
        _late = inputs.LateTotal;
        if (inputs.Animating)
            _lastAnimatingNs = inputs.NowNs;
        if (inputs.Working && (dropped > 0 || late > 0))
        {
            _pausedUntilNs = inputs.NowNs + _settings.PauseAfterDropS * 1_000_000_000L;
            _pauseWords = string.Create(c, $"paused for {_settings.PauseAfterDropS} s: {(dropped > 0 ? $"{dropped} frame{(dropped == 1 ? "" : "s")} dropped" : $"{late} frame{(late == 1 ? "" : "s")} late")} while it ran");
        }
        GovernorKnobs knobs;
        if (inputs.Armed)
            knobs = new(GovernorState.Waiting, 0, 0, 0, false, "optimise waits: the show is armed");
        else if (inputs.NowNs < _pausedUntilNs)
            knobs = new(GovernorState.Paused, 0, 0, 0, false, _pauseWords);
        else if (inputs.Animating || (_lastAnimatingNs != long.MinValue && inputs.NowNs - _lastAnimatingNs < _settings.IdleToRunS * 1_000_000_000L))
            knobs = new(GovernorState.Trickle, 1, _settings.TrickleGpuDuty, _settings.TrickleDiskMbps, false,
                string.Create(c, $"a show plays: one stream, GPU ≤ {_settings.TrickleGpuDuty * 100:0} %"));
        else
            knobs = new(GovernorState.Run, Math.Max(1, _settings.Cores - 1), 1, 0, true, "");
        Current = knobs;
        return knobs;
    }

    /// <summary>Whether a pause from a drop is in force at this instant (journalled once, when it starts).</summary>
    public bool PausedFor(long nowNs) => nowNs < _pausedUntilNs;
}
