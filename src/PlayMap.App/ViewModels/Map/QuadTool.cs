using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.Core.Numerics;

namespace PlayMap.App.ViewModels.Map;

/// <summary>The quad tool, as logic: up to four clicks on the wall (wall-normalised, (0, 0) at the output's top-left, 1 across), drawn
/// at once with a rubber band to the pointer; the fourth makes the quad ready, and Enter draws it. A click outside the picture, or a
/// fifth, takes nothing. Before Enter each placed point moves (dragged, or nudged: the selected point, the newest unless Tab picked
/// another), inside the picture. The view turns pointer events into these calls; the map keeps the draft and sends the lines.</summary>
public sealed class QuadTool : ObservableObject
{
    private readonly List<Vec2d> _clicks = [];
    private Vec2d? _pointer;
    private int _selected = -1;

    /// <summary>The corners clicked so far, in order.</summary>
    public IReadOnlyList<Vec2d> Clicks => _clicks;

    /// <summary>Where the pointer is over the picture while the quad is drawn (the rubber band's end), or null.</summary>
    public Vec2d? Pointer => _pointer;

    public bool IsDrawing => _clicks.Count > 0;

    /// <summary>Four corners: Enter draws the quad.</summary>
    public bool IsReady => _clicks.Count == 4;

    /// <summary>What the next click does, in words.</summary>
    public string Hint => _clicks.Count switch
    {
        0 => "Click the four corners of the wall, on the wall — any order.",
        4 => "Enter draws the quad · drag or nudge a point to move it · Esc discards.",
        var n => $"Corner {n + 1} of 4 · drag or nudge a point to move it · Esc discards.",
    };

    /// <summary>Raised when what the tool draws changed.</summary>
    public event Action? Changed;

    /// <summary>The placed point the arrows and Tab move (the newest unless Tab picked another), or -1 with none placed.</summary>
    public int Selected => _clicks.Count == 0 ? -1 : Math.Clamp(_selected < 0 ? _clicks.Count - 1 : _selected, 0, _clicks.Count - 1);

    /// <summary>A click at a wall point: true when it took a corner.</summary>
    public bool Click(Vec2d wall)
    {
        if (_clicks.Count >= 4 || !Inside(wall))
            return false;
        _clicks.Add(wall);
        _selected = _clicks.Count - 1;
        Moved();
        return true;
    }

    /// <summary>The placed point within <paramref name="reach"/> (wall-normalised) of <paramref name="wall"/>, nearest first, or -1.</summary>
    public int PointAt(Vec2d wall, double reach)
    {
        var best = -1;
        var bestD = reach;
        for (var i = 0; i < _clicks.Count; i++)
        {
            var d = (_clicks[i] - wall).Length;
            if (d <= bestD)
            {
                best = i;
                bestD = d;
            }
        }
        return best;
    }

    /// <summary>Moves a placed point to a wall point inside the picture (clamped to it); true when it moved.</summary>
    public bool MovePoint(int index, Vec2d wall)
    {
        if (index < 0 || index >= _clicks.Count)
            return false;
        var to = new Vec2d(Math.Clamp(wall.X, 0, 1), Math.Clamp(wall.Y, 0, 1));
        _selected = index;
        if (_clicks[index] == to)
            return false;
        _clicks[index] = to;
        Moved();
        return true;
    }

    /// <summary>Tab: the next (or previous) placed point is the one the arrows move.</summary>
    public void Step(int step)
    {
        if (_clicks.Count == 0)
            return;
        _selected = (Selected + step + _clicks.Count) % _clicks.Count;
        Moved();
    }

    /// <summary>Takes up a kept draft's points (another projector shown, a desk restart): nothing is sent.</summary>
    public void Restore(IReadOnlyList<Vec2d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _clicks.Clear();
        _clicks.AddRange(points.Take(4));
        _selected = _clicks.Count - 1;
        _pointer = null;
        Moved();
    }

    /// <summary>The pointer moved over the picture (null: it left).</summary>
    public void MoveTo(Vec2d? wall)
    {
        if (!IsDrawing || _pointer == wall)
            return;
        _pointer = wall;
        Changed?.Invoke();
    }

    /// <summary>The four corners, and the tool empty again.</summary>
    public ImmutableArray<Vec2d> Take()
    {
        ImmutableArray<Vec2d> clicks = [.. _clicks];
        Cancel();
        return clicks;
    }

    public void Cancel()
    {
        if (_clicks.Count == 0 && _pointer is null)
            return;
        _clicks.Clear();
        _pointer = null;
        _selected = -1;
        Moved();
    }

    private void Moved()
    {
        Raise(nameof(Clicks), nameof(IsDrawing), nameof(IsReady), nameof(Hint), nameof(Selected));
        Changed?.Invoke();
    }

    private static bool Inside(Vec2d p) => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1;

    /// <summary>The id a SURFACE QUAD or LIGHT WALL reply names (its JSON's field), or null.</summary>
    public static string? Field(string reply, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            // Not a JSON reply: the node answered words only, and names no id.
            return null;
        }
    }
}
