using System.Collections.ObjectModel;
using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.ViewModels.Map;

/// <summary>The unfinished quads of one desk on one node, a draft per projector: the points placed so far (wall-normalised), kept on the
/// desk — never in the project — until Enter draws the quad or a Discard drops it, and written to the user's desk.json by node and
/// projector so a desk restart and a reattach to the same node keep them. A gesture's moves (a placed point dragged, an arrow) are kept
/// in memory and written once the gesture ends (<see cref="Persist"/>), never at every pointer move. A draft whose projector the project
/// no longer names is dropped (the map says so).</summary>
public sealed class QuadDrafts
{
    private readonly DeskPrefs _prefs;
    private readonly Dictionary<string, List<Vec2d>> _drafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsaved = new(StringComparer.Ordinal);
    private string _node = "";

    public QuadDrafts(DeskPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        _prefs = prefs;
    }

    /// <summary>Raised when a draft came, moved or went.</summary>
    public event Action? Changed;

    /// <summary>The projectors that hold a draft, in the order they were begun.</summary>
    public IReadOnlyList<string> Projectors => [.. _drafts.Keys];

    /// <summary>The node the drafts are read from and kept for: another node's drafts are read from desk.json when the desk attaches.</summary>
    public void UseNode(string node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node == _node)
            return;
        foreach (var pj in _unsaved.ToList())
            Persist(pj);
        _node = node;
        _drafts.Clear();
        _unsaved.Clear();
        if (node.Length > 0)
        {
            foreach (var pj in _prefs.QuadDraftProjectors(node))
            {
                if (_prefs.QuadDraft(node, pj) is { Count: > 0 } points)
                    _drafts[pj] = [.. points.Take(4)];
            }
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<Vec2d>? Of(string projector) => _drafts.TryGetValue(projector, out var d) ? d : null;

    /// <summary>Keeps a projector's points (none: forgets the draft) and writes desk.json, or, with <paramref name="persist"/> false (a
    /// gesture's move), holds them in memory until <see cref="Persist"/>.</summary>
    public void Keep(string projector, IReadOnlyList<Vec2d> points, bool persist = true)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            Drop(projector);
            return;
        }
        var moved = !(_drafts.TryGetValue(projector, out var had) && had.SequenceEqual(points));
        if (moved)
        {
            _drafts[projector] = [.. points];
            _unsaved.Add(projector);
        }
        if (persist)
            Persist(projector);
        if (moved)
            Changed?.Invoke();
    }

    /// <summary>Writes a projector's draft to desk.json if a gesture moved it since it was last written (a drag let go, Enter refused,
    /// the person elsewhere, Resume).</summary>
    public void Persist(string projector)
    {
        if (!_unsaved.Remove(projector) || !_drafts.TryGetValue(projector, out var points))
            return;
        if (_node.Length > 0)
            _prefs.SetQuadDraft(_node, projector, points);
    }

    public bool Drop(string projector)
    {
        _unsaved.Remove(projector);
        if (!_drafts.Remove(projector))
            return false;
        if (_node.Length > 0)
            _prefs.SetQuadDraft(_node, projector, null);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drops every draft whose projector the project does not name; returns the projectors dropped.</summary>
    public IReadOnlyList<string> DropGone(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var gone = _drafts.Keys.Where(pj => project.FindProjector(pj) is null).ToList();
        foreach (var pj in gone)
            Drop(pj);
        return gone;
    }
}

/// <summary>A draft's chip, in the view's corner and under Surfaces in the outliner: "Quad 2/4 · P1" with Resume and Discard.</summary>
public sealed class QuadDraftChip : ObservableObject
{
    private int _placed;
    private string _projectorName = "";

    public QuadDraftChip(string projector, DeskAction resume, DeskAction discard)
    {
        Projector = projector;
        Resume = resume;
        Discard = discard;
        Actions = [resume, discard];
    }

    public string Projector { get; }

    public DeskAction Resume { get; }

    public DeskAction Discard { get; }

    public IReadOnlyList<DeskAction> Actions { get; }

    /// <summary>"Quad 2/4".</summary>
    public string Text => string.Create(CultureInfo.InvariantCulture, $"Quad {_placed}/4");

    /// <summary>"Quad 2/4 on Left projector".</summary>
    public string Words => Text + " on " + (_projectorName.Length > 0 ? _projectorName : Projector);

    public void Update(int placed, string projectorName)
    {
        if (_placed == placed && _projectorName == projectorName)
            return;
        _placed = placed;
        _projectorName = projectorName;
        Raise(nameof(Text), nameof(Words));
    }
}

/// <summary>Where a quad's corners are on the wall: the output that puts its projector on the wall (the first enabled output in projector
/// mode that shows it, else the first that shows it — the node's choice), its warp and quarter turns, and the wall's pixels; with no
/// output the projector's raster, its pixels. The inspector's typed corners and the view's drags are in these units.</summary>
public sealed class QuadWall
{
    private readonly WarpField _field;
    private readonly int _quarters;

    private QuadWall(Projector projector, Output? output)
    {
        Output = output;
        Width = output?.Width ?? projector.Intrinsics.Width;
        Height = output?.Height ?? projector.Intrinsics.Height;
        _field = output is null ? WarpField.Identity : new WarpField(output.Warp);
        _quarters = output is null ? 0 : (int)(((Math.Round(output.Rotation / 90.0) % 4) + 4) % 4);
    }

    /// <summary>The output whose pixels are the wall's, or null (then the units are raster pixels).</summary>
    public Output? Output { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>"PX" on a wall, "RASTER" on the projector's own raster.</summary>
    public string Units => Output is null ? "RASTER" : "PX";

    public static QuadWall? Of(Project project, string projectorId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.FindProjector(projectorId) is not { } pj)
            return null;
        var output = project.Outputs.FirstOrDefault(o => o.Mode == OutputMode.Projector && o.ProjectorId == pj.Id && o.Enabled)
            ?? project.Outputs.FirstOrDefault(o => o.Mode == OutputMode.Projector && o.ProjectorId == pj.Id);
        return new QuadWall(pj, output);
    }

    /// <summary>A raster-normalised point in the wall's pixels (raster pixels with no output).</summary>
    public Vec2d Pixels(Vec2d raster)
    {
        var a = _field.Evaluate(raster.X, raster.Y);
        return PixelsAt(a);
    }

    /// <summary>A point of the projector view in the wall's pixels: the view draws the output's warp but not its quarter turns, so the
    /// turns are applied here, as the node's wall frame applies them.</summary>
    public Vec2d PixelsAt(Vec2d view)
    {
        var turned = _quarters switch
        {
            1 => new Vec2d(1 - view.Y, view.X),
            2 => new Vec2d(1 - view.X, 1 - view.Y),
            3 => new Vec2d(view.Y, 1 - view.X),
            _ => view,
        };
        return new Vec2d(turned.X * Width, turned.Y * Height);
    }

    /// <summary>The raster-normalised point under a point of the projector view (the output's warp taken back); null where no point of
    /// the warped picture lands there.</summary>
    public Vec2d? RasterAt(Vec2d view) => _field.Invert(view.X, view.Y);
}
