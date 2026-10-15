using PlayMap.Core.Model;
using PlayMap.Core.Sync;
using PlayMap.Engine.Presentation;
using PlayMap.Net.Sync;
using PlayMap.Render.Platform;

namespace PlayMap.Engine.Sync;

/// <summary>One output's vblank facts as this node measured them, on its local monotonic clock: its lock state and present path,
/// the fitted refresh period, the instant of its last fitted vblank (−1 before any), how many refreshes it presented, the
/// vblanks it missed in the last minute, whether the fit settled, its air latency and the board's frame count (0 with none).</summary>
public readonly record struct OutputVblank(string OutputId, LockState State, byte Path, long PeriodPs, long LastVblankLocalNs, ulong RefreshIndex,
    uint Missed60s, bool Settled, int AirLatencyUs, uint FrameCount);

/// <summary>Where the sync package reads this node's outputs' vblanks (the render host's display-side numbers; a test's synthetic
/// ones). Any thread; published numbers only.</summary>
public interface IVblankSource
{
    IReadOnlyList<OutputVblank> Read();
}

/// <summary>The vblanks of the enabled outputs the render host presents (wp2-output's <see cref="IPresentBackend"/> and the lock
/// machine's states); none on a node without a render host.</summary>
public sealed class PresentVblankSource(EngineCore engine) : IVblankSource
{
    public IReadOnlyList<OutputVblank> Read()
    {
        if (engine.Services.Outputs is not IPresentBackend present)
            return [];
        var locks = (engine.Services.Outputs as IFrameLockBackend)?.Locks();
        var project = engine.Snapshot.Project.Project;
        var views = present.Present();
        var list = new List<OutputVblank>(views.Count);
        foreach (var v in views)
        {
            if (project.FindOutput(v.OutputId) is not { Enabled: true } o)
                continue;
            var p = v.Present;
            if (p.LastVblankNs < 0 || p.RefreshHz is <= 20 or > 1000 || !double.IsFinite(p.RefreshHz))
                continue;
            var state = locks is not null && locks.TryGetValue(o.Id, out var l) ? l.State : p.Settled ? LockState.FreeMeasured : LockState.FreeUnknown;
            var path = p.Path switch
            {
                PresentPath.GlWindow => VblankReport.PathGl,
                PresentPath.DxgiFlip => VblankReport.PathDxgiFlip,
                _ => VblankReport.PathOffscreen,
            };
            list.Add(new OutputVblank(o.Id, state, path, (long)Math.Round(1e12 / p.RefreshHz), p.LastVblankNs, (ulong)Math.Max(0, p.Presented),
                (uint)Math.Clamp(p.Missed60s, 0, VblankReport.MaxMissed60s), p.Settled, AirLatencyUs(o), 0));
        }
        return list;
    }

    /// <summary>An output's air latency in µs, within what a report carries.</summary>
    public static int AirLatencyUs(Output o) =>
        (int)Math.Clamp(Math.Round((o ?? throw new ArgumentNullException(nameof(o))).LatencyMs * 1000), 0, VblankReport.MaxAirLatencyUs);
}
