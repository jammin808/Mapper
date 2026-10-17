using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.Core.Numerics;

namespace PlayMap.App.ViewModels.Map;

/// <summary>The quad tool, as logic: up to four clicks on the wall (wall-normalised, (0, 0) at the output's top-left, 1 across), drawn
/// at once with a rubber band to the pointer; the fourth makes the quad ready, and Enter draws it. A click outside the picture, or a
/// fifth, takes nothing. The view turns pointer events into these calls; the map sends the lines.</summary>
public sealed class QuadTool : ObservableObject
{
    private readonly List<Vec2d> _clicks = [];
    private Vec2d? _pointer;

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
        4 => "Enter draws the quad · Esc cancels.",
        var n => $"Corner {n + 1} of 4 · Esc cancels.",
    };

    /// <summary>Raised when what the tool draws changed.</summary>
    public event Action? Changed;

    /// <summary>A click at a wall point: true when it took a corner.</summary>
    public bool Click(Vec2d wall)
    {
        if (_clicks.Count >= 4 || !Inside(wall))
            return false;
        _clicks.Add(wall);
        Moved();
        return true;
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
        Moved();
    }

    private void Moved()
    {
        Raise(nameof(Clicks), nameof(IsDrawing), nameof(IsReady), nameof(Hint));
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
