using PlayMap.Render.Gpu;

namespace PlayMap.Render.Effects;

/// <summary>A slot's GPU time between two timestamps, three frames deep so reading never waits: the span lands two or three
/// frames after it was drawn. Timestamps, unlike elapsed-time queries, may sit inside the output's own query.</summary>
internal sealed class FxTimer : IDisposable
{
    private const int Depth = 3;
    private readonly GlDevice _device;
    private readonly uint[] _start = new uint[Depth];
    private readonly uint[] _end = new uint[Depth];
    private readonly bool[] _pending = new bool[Depth];
    private int _next;
    private int _open = -1;

    public FxTimer(GlDevice device)
    {
        _device = device;
        for (var i = 0; i < Depth; i++)
        {
            _start[i] = device.CreateQuery();
            _end[i] = device.CreateQuery();
        }
    }

    /// <summary>The last span measured, ns (0 until one lands).</summary>
    public long LastNs { get; private set; }

    public void Begin()
    {
        Collect();
        if (_pending[_next])
            return;
        _device.Timestamp(_start[_next]);
        _open = _next;
    }

    public void End()
    {
        if (_open < 0)
            return;
        _device.Timestamp(_end[_open]);
        _pending[_open] = true;
        _next = (_open + 1) % Depth;
        _open = -1;
    }

    /// <summary>Drops a span begun with nothing drawn (its start is written again next time).</summary>
    public void Cancel() => _open = -1;

    private void Collect()
    {
        for (var i = 0; i < Depth; i++)
        {
            if (!_pending[i] || !_device.TryQueryResult(_end[i], out var end) || !_device.TryQueryResult(_start[i], out var start))
                continue;
            _pending[i] = false;
            LastNs = Math.Max(0, end - start);
        }
    }

    public void Dispose()
    {
        for (var i = 0; i < Depth; i++)
        {
            _device.DeleteQuery(_start[i]);
            _device.DeleteQuery(_end[i]);
        }
    }
}
