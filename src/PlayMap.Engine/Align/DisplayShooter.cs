using System.Globalization;
using PlayMap.Core.Align;
using PlayMap.Core.Model;
using PlayMap.Core.Scan;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Align;

/// <summary>
/// The camera's side of a run through a real display and camera: before the first picture, the camera's latency measured with white
/// flashes on the first output; then each shot puts its frame on its output and black on every other output of the group (only those
/// that change are presented again), and keeps the first camera picture that arrived at or after the last present plus the latency and
/// that the settle detector accepts (after enough late pictures, the last one).
/// </summary>
internal sealed class DisplayShooter(IScanDisplay display, IScanCamera camera, IReadOnlyList<Output> outputs, AlignRunnerOptions options) : IAlignShooter
{
    private readonly bool[] _black = new bool[outputs.Count];
    private readonly byte[]?[] _blackFrames = new byte[]?[outputs.Count];
    private double? _latencyMs;
    private long _generation;
    private SettleDetector? _settle;

    public async Task<CameraShot> ShootAsync(int output, byte[]? frame, CancellationToken ct)
    {
        _latencyMs ??= await MeasureLatencyAsync(ct).ConfigureAwait(false);
        var present = 0L;
        for (var k = 0; k < outputs.Count; k++)
        {
            var o = outputs[k];
            if (k == output && frame is not null)
            {
                present = Math.Max(present, await display.ShowAsync(o.Id, frame, o.Width, o.Height, ct).ConfigureAwait(false));
                _black[k] = false;
            }
            else if (!_black[k])
            {
                _blackFrames[k] ??= new byte[o.Width * o.Height];
                present = Math.Max(present, await display.ShowAsync(o.Id, _blackFrames[k]!, o.Width, o.Height, ct).ConfigureAwait(false));
                _black[k] = true;
            }
        }
        var earliest = present + (long)(Math.Max(0, _latencyMs.Value) * 1e6);
        CameraFrame? last = null;
        var offered = 0;
        while (true)
        {
            var f = await camera.NextAsync(_generation, options.FrameTimeout, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"the camera sent no picture for {options.FrameTimeout.TotalSeconds:0.#} s ({camera.Problem})"));
            _generation = f.Generation;
            if (f.ArrivalNs < earliest)
                continue;
            if (_settle is null || last is not null && (f.Width != last.Width || f.Height != last.Height))
                _settle = new SettleDetector(f.Width, f.Height, options.Settle);
            last = f;
            offered++;
            if (_settle.Offer(f.Gray) == SettleState.Settled || offered >= options.MaxUnsettledFrames)
                break;
        }
        return new CameraShot(last.Gray, last.Width, last.Height);
    }

    /// <summary>Black on every output, then white and black a few times on the first, each change stamped at its present; every
    /// camera picture's mean brightness collected meanwhile.</summary>
    private async Task<double> MeasureLatencyAsync(CancellationToken ct)
    {
        if (options.LatencyFlashes <= 0 || outputs.Count == 0)
            return options.FallbackLatencyMs;
        var first = outputs[0];
        var white = new byte[first.Width * first.Height];
        Array.Fill(white, (byte)255);
        var black = new byte[first.Width * first.Height];
        var estimator = new LatencyEstimator();
        var gate = new Lock();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var collect = Task.Run(async () =>
        {
            var generation = 0L;
            while (!stop.IsCancellationRequested)
            {
                CameraFrame? f;
                try
                {
                    f = await camera.NextAsync(generation, TimeSpan.FromMilliseconds(200), stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return generation;
                }
                if (f is null)
                    continue;
                generation = f.Generation;
                var mean = LatencyEstimator.MeanBrightness(f.Gray);
                lock (gate)
                    estimator.AddFrame(f.ArrivalNs / 1e6, mean);
            }
            return generation;
        }, CancellationToken.None);
        try
        {
            for (var k = 0; k < outputs.Count; k++)
            {
                var o = outputs[k];
                _blackFrames[k] ??= new byte[o.Width * o.Height];
                await display.ShowAsync(o.Id, _blackFrames[k]!, o.Width, o.Height, ct).ConfigureAwait(false);
                _black[k] = true;
            }
            await Task.Delay(options.FlashHold, ct).ConfigureAwait(false);
            for (var k = 0; k < 2 * options.LatencyFlashes; k++)
            {
                var at = await display.ShowAsync(first.Id, k % 2 == 0 ? white : black, first.Width, first.Height, ct).ConfigureAwait(false);
                lock (gate)
                    estimator.AddChange(at / 1e6);
                await Task.Delay(options.FlashHold, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            _generation = await collect.ConfigureAwait(false);
        }
        LatencyEstimate? estimate;
        lock (gate)
            estimate = estimator.Estimate();
        return estimate?.LatencyMs ?? options.FallbackLatencyMs;
    }
}
