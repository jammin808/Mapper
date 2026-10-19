using System.Runtime.CompilerServices;
using PlayMap.Core.Gpu;

namespace PlayMap.Render.Engine;

/// <summary>A device's timestamp slots, handed out two at a time to the spans one frame measures: every frame starts with all of
/// them free, so a span holds slots only in a frame it is measured, and a span the frame has no slots left for is not measured that
/// frame. One table per device (<see cref="Of"/>), so the outputs', generators' and effects' timers share one frame's slots.
/// Render thread.</summary>
internal sealed class GpuTimeSlots
{
    private static readonly ConditionalWeakTable<IGpuDevice, GpuTimeSlots> Tables = new();

    private readonly IGpuDevice _device;
    private long _frame = long.MinValue;
    private int _next;

    private GpuTimeSlots(IGpuDevice device)
    {
        _device = device;
    }

    /// <summary>The device's table.</summary>
    public static GpuTimeSlots Of(IGpuDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Tables.GetValue(device, static d => new GpuTimeSlots(d));
    }

    public IGpuTimers Timers => _device.Timers;

    /// <summary>The first of two slots still free in <paramref name="frame"/>; false when the frame's slots are taken or the device
    /// writes no timestamps.</summary>
    public bool TryLease(long frame, out int first)
    {
        if (frame != _frame)
        {
            _frame = frame;
            _next = 0;
        }
        if (_next + 2 > _device.Timers.Slots)
        {
            first = -1;
            return false;
        }
        first = _next;
        _next += 2;
        return true;
    }
}

/// <summary>The GPU time of a span of a frame's work: a timestamp before it and one after, in two of the frame's slots
/// (<see cref="GpuTimeSlots"/>), read back frames later once the device has them — never waited for. At most <see cref="Depth"/>
/// spans are in flight; a span not read back within <see cref="Depth"/> frames is let go (its frame's slots were written again).
/// A device with no timestamps (an ES 3.0 context) measures nothing and the time stays 0. Render thread; allocation-free.</summary>
internal sealed class GpuSpanTimer
{
    /// <summary>Spans in flight, and the frames a span may wait to be read.</summary>
    public const int Depth = 3;

    private readonly GpuTimeSlots _slots;
    private readonly long[] _frames = new long[Depth];
    private readonly int[] _first = new int[Depth];
    private int _pending;
    private long _openFrame;
    private int _openSlot = -1;

    public GpuSpanTimer(IGpuDevice device)
    {
        _slots = GpuTimeSlots.Of(device);
    }

    /// <summary>The last span measured, ns (0 until one lands).</summary>
    public long LastNs { get; private set; }

    /// <summary>The last span measured, ms.</summary>
    public double LastMs => LastNs / 1e6;

    /// <summary>Starts a span of <paramref name="frame"/> (the frame <paramref name="list"/> records): reads what has landed first,
    /// then writes the span's first timestamp, unless spans are in flight to the depth or the frame has no slots left.</summary>
    public void Begin(ICommandList list, long frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        Collect(frame);
        if (_openSlot >= 0 || _pending == Depth || !_slots.TryLease(frame, out var first))
            return;
        list.Timestamp(first);
        _openFrame = frame;
        _openSlot = first;
    }

    /// <summary>Ends the open span: its second timestamp, read frames later.</summary>
    public void End(ICommandList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_openSlot < 0)
            return;
        list.Timestamp(_openSlot + 1);
        _frames[_pending] = _openFrame;
        _first[_pending] = _openSlot;
        _pending++;
        _openSlot = -1;
    }

    /// <summary>Drops a span begun with nothing to measure (it is never read).</summary>
    public void Cancel() => _openSlot = -1;

    /// <summary>Reads every span that landed, oldest first, so the newest read is the last kept.</summary>
    private void Collect(long frame)
    {
        var timers = _slots.Timers;
        var i = 0;
        while (i < _pending)
        {
            if (timers.TryElapsedUs(_frames[i], _first[i], _first[i] + 1, out var us))
            {
                LastNs = Math.Max(0, (long)Math.Round(us * 1000));
                Remove(i);
            }
            else if (frame - _frames[i] > Depth)
            {
                Remove(i);
            }
            else
            {
                i++;
            }
        }
    }

    private void Remove(int i)
    {
        for (var k = i + 1; k < _pending; k++)
        {
            _frames[k - 1] = _frames[k];
            _first[k - 1] = _first[k];
        }
        _pending--;
    }
}
