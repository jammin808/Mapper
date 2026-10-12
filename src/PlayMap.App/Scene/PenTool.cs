using System.Collections.Immutable;
using PlayMap.App.Infrastructure;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Scene;

/// <summary>The pen tool, as logic: a click adds a corner node; a press-and-drag pulls out handles and makes
/// a smooth node (handles collinear, the drag sets the out handle and mirrors the in handle); Alt-click on a
/// node toggles it between smooth and corner; a click on the first node (or Enter) closes the path. Points
/// are in the space of what will own the mask (normalised output units in the projector view). The view
/// turns pointer events into these calls and draws <see cref="Nodes"/>.</summary>
public sealed class PenTool : ObservableObject
{
    private readonly List<BezierNode> _nodes = [];
    private int _dragging = -1;
    private Vec2d _pressAt;
    private bool _isClosed;
    private double _feather = 0.01;
    private MaskMode _mode = MaskMode.Subtract;

    public PenTool()
    {
        CloseCommand = new RelayCommand(() => Close(), () => CanClose);
    }

    public IReadOnlyList<BezierNode> Nodes => _nodes;

    public bool IsDrawing => _nodes.Count > 0 && !_isClosed;

    public bool IsClosed => _isClosed;

    public bool CanClose => !_isClosed && _nodes.Count >= 3;

    public RelayCommand CloseCommand { get; }

    /// <summary>How far the mask's edge feathers (in the owner's units: 0.01 = 1 % of the width).</summary>
    public double Feather
    {
        get => _feather;
        set => Set(ref _feather, Math.Clamp(Math.Round(value, 4), 0, 0.25));
    }

    public MaskMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    public IReadOnlyList<MaskMode> Modes { get; } = Enum.GetValues<MaskMode>();

    /// <summary>Raised when the path closes (the owner then sends it).</summary>
    public event Action<BezierPath>? Closed;

    /// <summary>A press at a point. <paramref name="hit"/> is the pick radius in the same units.</summary>
    public void Press(Vec2d p, bool alt, double hit)
    {
        if (_isClosed)
            Reset();
        var near = Nearest(p, hit);
        if (alt && near >= 0)
        {
            ToggleSmooth(near);
            return;
        }
        if (near == 0 && CanClose)
        {
            Close();
            return;
        }
        _nodes.Add(BezierNode.Corner(p.X, p.Y));
        _dragging = _nodes.Count - 1;
        _pressAt = p;
        Changed();
    }

    /// <summary>The pointer moved with the button down: a drag far enough makes the new node smooth.</summary>
    public void Drag(Vec2d p, double hit)
    {
        if (_dragging < 0)
            return;
        var handle = p - _pressAt;
        if (handle.Length < hit * 0.5)
            return;
        var n = _nodes[_dragging];
        _nodes[_dragging] = n with { Out = handle, In = -handle, Smooth = true };
        Changed();
    }

    public void Release()
    {
        _dragging = -1;
    }

    /// <summary>Smooth ↔ corner. A corner made smooth gets handles along its neighbours' chord, a third of
    /// the way to each, so it bends at once.</summary>
    public void ToggleSmooth(int index)
    {
        if (index < 0 || index >= _nodes.Count)
            return;
        var n = _nodes[index];
        if (n.Smooth)
        {
            _nodes[index] = n with { In = Vec2d.Zero, Out = Vec2d.Zero, Smooth = false };
        }
        else
        {
            var prev = _nodes[(index - 1 + _nodes.Count) % _nodes.Count].Point;
            var next = _nodes[(index + 1) % _nodes.Count].Point;
            var dir = (next - prev) / 6;
            if (dir.LengthSquared < 1e-12)
                dir = new Vec2d(0.02, 0);
            _nodes[index] = n with { In = -dir, Out = dir, Smooth = true };
        }
        Changed();
    }

    /// <summary>Closes the path (three nodes or more) and raises <see cref="Closed"/>.</summary>
    public BezierPath? Close()
    {
        if (!CanClose)
            return null;
        _isClosed = true;
        _dragging = -1;
        var path = Path;
        Changed();
        Closed?.Invoke(path);
        return path;
    }

    public BezierPath Path => new([.. _nodes], _isClosed);

    public void Cancel() => Reset();

    public void Reset()
    {
        _nodes.Clear();
        _isClosed = false;
        _dragging = -1;
        Changed();
    }

    /// <summary>The mask the closed path makes.</summary>
    public Mask ToMask(string name) => new("", name, Path, _mode, _feather, false, true);

    /// <summary>The ADD mask JSON for the closed path, owned by <paramref name="parentId"/> (a surface or an
    /// output); the engine gives it its id.</summary>
    public string MaskJson(string parentId, string name)
    {
        var json = ProjectJson.SerializeEntity(ToMask(name));
        return "{\"parent\":" + VerbText.JsonString(parentId) + "," + json[1..];
    }

    private int Nearest(Vec2d p, double hit)
    {
        var best = -1;
        var bestD = hit;
        for (var i = 0; i < _nodes.Count; i++)
        {
            var d = Vec2d.Distance(_nodes[i].Point, p);
            if (d <= bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    private void Changed()
    {
        Raise(nameof(Nodes), nameof(IsDrawing), nameof(IsClosed), nameof(CanClose));
        CloseCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Points along a path, for drawing and hit tests (16 steps a segment).</summary>
    public static ImmutableArray<Vec2d> Flatten(BezierPath path, int steps = 16)
    {
        ArgumentNullException.ThrowIfNull(path);
        var nodes = path.Nodes;
        var pts = ImmutableArray.CreateBuilder<Vec2d>();
        if (nodes.IsDefaultOrEmpty)
            return pts.ToImmutable();
        var count = path.Closed ? nodes.Length : nodes.Length - 1;
        pts.Add(nodes[0].Point);
        for (var i = 0; i < count; i++)
        {
            var a = nodes[i];
            var b = nodes[(i + 1) % nodes.Length];
            var p0 = a.Point;
            var p1 = a.Point + a.Out;
            var p2 = b.Point + b.In;
            var p3 = b.Point;
            for (var s = 1; s <= steps; s++)
            {
                var t = s / (double)steps;
                var u = 1 - t;
                pts.Add(p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t));
            }
        }
        return pts.ToImmutable();
    }
}
