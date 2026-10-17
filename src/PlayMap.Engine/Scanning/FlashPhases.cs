using PlayMap.Core.Scan;

namespace PlayMap.Engine.Scanning;

/// <summary>
/// The pacing of a camera's latency flashes (a scan's and an align run's): each phase — the black before the flashes, each flash,
/// each black between — stays on the glass for its hold and until the camera has sent the pictures the estimate measures a change
/// from (<see cref="LatencyEstimator.FramesAfterChange"/> stamped after the phase's present), so a slow camera or a loaded node still
/// measures. A phase whose pictures have not come within the frame timeout after its hold ends the measurement with what it has.
/// The camera's pictures are told through <see cref="Frame"/>, from the thread that collects them.
/// </summary>
internal sealed class FlashPhases(TimeSpan hold, TimeSpan frameTimeout) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private long _phaseAtNs = long.MaxValue;
    private int _phaseFrames;

    /// <summary>A picture the camera sent, stamped at <paramref name="arrivalNs"/>.</summary>
    public void Frame(long arrivalNs)
    {
        lock (_gate)
        {
            if (arrivalNs > _phaseAtNs)
                _phaseFrames++;
        }
        _arrived.Release();
    }

    /// <summary>Holds the phase presented at <paramref name="presentNs"/>; false when its pictures did not come.</summary>
    public async Task<bool> HoldAsync(long presentNs, CancellationToken ct)
    {
        lock (_gate)
        {
            _phaseAtNs = presentNs;
            _phaseFrames = 0;
        }
        await Task.Delay(hold, ct).ConfigureAwait(false);
        using var patience = new CancellationTokenSource(frameTimeout);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, patience.Token);
        while (true)
        {
            lock (_gate)
            {
                if (_phaseFrames >= LatencyEstimator.FramesAfterChange)
                    return true;
            }
            try
            {
                await _arrived.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    public void Dispose() => _arrived.Dispose();
}
