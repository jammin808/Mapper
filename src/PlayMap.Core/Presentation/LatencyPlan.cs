using System.Globalization;

namespace PlayMap.Core.Presentation;

/// <summary>One output's place in the latency plan: its own input lag, its refresh, the whole frames its picture is held back, and
/// what is left over (|residual| ≤ half a frame).</summary>
public readonly record struct OutputDelay(string OutputId, double LatencyMs, double RefreshHz, int DelayFrames, double ResidualMs);

/// <summary>Per-output latency (present M8): the node's air latency is the largest input lag of its enabled outputs (raised, never
/// lowered, to the group's when a sync group asks), every frame is drawn for the instant its light leaves the slowest display, and each
/// faster output presents the picture drawn <c>d = round((L_air − L_o) / T_o)</c> frames ago, so all of them light the same picture at
/// the same instant within half a frame. At most <see cref="MaxDelayFrames"/> frames: a larger difference is a configuration error
/// (more than 100 ms at 60 Hz), refused when it is set and capped here with words. Pure.</summary>
public sealed record LatencyPlan(double AirLatencyMs, IReadOnlyList<OutputDelay> Outputs, string Words)
{
    public const int MaxDelayFrames = 6;

    public static readonly LatencyPlan None = new(0, [], "");

    public long AirLatencyNs => (long)Math.Round(AirLatencyMs * 1e6);

    /// <summary>The delay of one output (0 frames when the plan does not name it).</summary>
    public OutputDelay For(string outputId)
    {
        foreach (var o in Outputs)
            if (o.OutputId == outputId)
                return o;
        return new OutputDelay(outputId, 0, 60, 0, 0);
    }

    /// <summary>The plan for these outputs (id, input lag ms, refresh Hz; a refresh of 0 or less is 60 Hz), with the group's air
    /// latency when it is larger than the node's own.</summary>
    public static LatencyPlan Make(IEnumerable<(string Id, double LatencyMs, double RefreshHz)> outputs, double groupAirMs = 0)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var list = outputs.Select(o => (o.Id, Latency: Math.Max(0, double.IsFinite(o.LatencyMs) ? o.LatencyMs : 0),
            Hz: o.RefreshHz > 1 && double.IsFinite(o.RefreshHz) ? o.RefreshHz : 60)).ToList();
        var air = list.Count == 0 ? 0 : list.Max(o => o.Latency);
        if (double.IsFinite(groupAirMs) && groupAirMs > air)
            air = groupAirMs;
        var words = "";
        var delays = new List<OutputDelay>(list.Count);
        foreach (var (id, latency, hz) in list)
        {
            var period = 1000.0 / hz;
            var want = (air - latency) / period;
            var d = (int)Math.Round(want, MidpointRounding.AwayFromZero);
            if (d > MaxDelayFrames)
            {
                words = string.Create(CultureInfo.InvariantCulture,
                    $"{id} would wait {d} frames for the slowest display (at most {MaxDelayFrames}): held {MaxDelayFrames}, the rest left over");
                d = MaxDelayFrames;
            }
            delays.Add(new OutputDelay(id, latency, hz, d, Math.Round(air - latency - d * period, 3)));
        }
        return new LatencyPlan(air, delays, words);
    }

    /// <summary>Why a set of input lags cannot be aligned (an output would wait more than <see cref="MaxDelayFrames"/> frames), or null.</summary>
    public static string? Refuse(IEnumerable<(string Id, double LatencyMs, double RefreshHz)> outputs)
    {
        var plan = Make(outputs);
        foreach (var o in plan.Outputs)
        {
            var period = 1000.0 / o.RefreshHz;
            var d = (int)Math.Round((plan.AirLatencyMs - o.LatencyMs) / period, MidpointRounding.AwayFromZero);
            if (d > MaxDelayFrames)
                return string.Create(CultureInfo.InvariantCulture,
                    $"{o.OutputId} would wait {d} frames ({plan.AirLatencyMs - o.LatencyMs:0.#} ms at {o.RefreshHz:0.##} Hz) for the slowest display: the delay ring holds at most {MaxDelayFrames} — check the displays' input lag");
        }
        return null;
    }
}
