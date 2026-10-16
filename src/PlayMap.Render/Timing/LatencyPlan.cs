using System.Globalization;

namespace PlayMap.Render.Timing;

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

/// <summary>An output's whole-frame delay: <c>d + 1</c> pictures in a ring, the newest written each frame, the one written <c>d</c>
/// frames ago presented. Until <c>d</c> frames have been written the oldest written one is presented (never one not yet drawn).
/// Swaps indices only; the pictures are made when the delay changes (off the steady frame path), so a warm frame allocates nothing.
/// Render thread.</summary>
public sealed class DelayRing<T> : IDisposable
    where T : class, IDisposable
{
    private T[] _slots = [];
    private int _head = -1;
    private int _written;

    public int Delay { get; private set; }

    /// <summary>Pictures held now (d + 1, or 0 with no delay).</summary>
    public int Count => _slots.Length;

    /// <summary>One of the pictures held (they are all made alike), or null with none.</summary>
    public T? First => _slots.Length > 0 ? _slots[0] : null;

    /// <summary>Sets the delay; a changed one (or a picture of another size, which the caller says by <paramref name="remake"/>) makes
    /// its pictures again with <paramref name="make"/>. 0 lets them all go.</summary>
    public void Configure(int delay, Func<T> make, bool remake = false)
    {
        ArgumentNullException.ThrowIfNull(make);
        ArgumentOutOfRangeException.ThrowIfNegative(delay);
        if (delay == Delay && !remake && (delay == 0 || _slots.Length == delay + 1))
            return;
        Release();
        Delay = delay;
        if (delay == 0)
            return;
        _slots = new T[delay + 1];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = make();
    }

    /// <summary>This frame's picture to write (the newest slot).</summary>
    public T Next()
    {
        if (_slots.Length == 0)
            throw new InvalidOperationException("a ring with no delay holds no pictures");
        _head = (_head + 1) % _slots.Length;
        _written = Math.Min(_written + 1, _slots.Length);
        return _slots[_head];
    }

    /// <summary>The picture to present this frame, after <see cref="Next"/> was written: the one written <see cref="Delay"/> frames
    /// ago, or the oldest written while the ring fills.</summary>
    public T Presented()
    {
        if (_head < 0)
            throw new InvalidOperationException("nothing written yet");
        var back = Math.Min(Delay, _written - 1);
        return _slots[(_head - back + _slots.Length) % _slots.Length];
    }

    /// <summary>Forgets what was written (the pictures stay): the next frame presents what it writes, then the delay builds again.</summary>
    public void Restart()
    {
        _head = -1;
        _written = 0;
    }

    private void Release()
    {
        foreach (var s in _slots)
            s.Dispose();
        _slots = [];
        _head = -1;
        _written = 0;
        Delay = 0;
    }

    public void Dispose() => Release();
}
