using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Show;

/// <summary>The one check every document edit, undo and redo passes before it is published: an edit that changes a locked
/// entity, a locked area, or (armed) the mapping or the outputs' layout is refused with the lock's words, and nothing moves.
/// Locks change only through LOCK and UNLOCK, which publish without asking this guard. Comparisons are by reference first (an
/// immutable root shares every list it did not touch), so a guard over a large project costs a handful of compares.</summary>
public static class LockGuard
{
    /// <summary>The areas an armed show locks without being asked.</summary>
    public static readonly IReadOnlyList<string> ArmedAreas = [LockAreas.Mapping, LockAreas.Layout];

    /// <summary>Refusal words for publishing <paramref name="after"/> over <paramref name="before"/>, or null to let it through.</summary>
    public static string? Check(Project before, Project after, bool armed)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (ReferenceEquals(before, after))
            return null;
        if (before.Show.Locks != after.Show.Locks && !before.Show.Locks.SequenceEqual(after.Show.Locks))
            return "locks change only through LOCK and UNLOCK";
        foreach (var l in before.Show.Locks)
        {
            var why = l.Kind == LockKind.Area ? AreaChanged(before, after, l.Target) ? AreaWords(l) : null : EntityWords(before, after, l);
            if (why is not null)
                return why;
        }
        if (FlaggedEntityWords(before, after) is { } flagged)
            return flagged;
        if (armed)
        {
            foreach (var area in ArmedAreas)
            {
                if (AreaChanged(before, after, area) && !before.Show.Locks.Any(l => l.Kind == LockKind.Area && l.Target == area))
                    return $"{area} is locked while the show is armed — SHOW DISARM first";
            }
        }
        return null;
    }

    /// <summary>True when the edit changes anything in a named area.</summary>
    public static bool AreaChanged(Project before, Project after, string area)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return area switch
        {
            LockAreas.Mapping => StageChanged(before.Stage, after.Stage) || SurfacesChanged(before.Stage, after.Stage, GeometryMoved, addedOrRemoved: true)
                || OutputsChanged(before, after, MappingMoved),
            LockAreas.Layout => OutputsChanged(before, after, LayoutMoved, addedOrRemoved: true),
            LockAreas.Content => before.Canvases != after.Canvases || before.Generators != after.Generators || before.Sends != after.Sends
                || before.GeneratorPresets != after.GeneratorPresets || OutputsChanged(before, after, ContentMoved)
                || SurfacesChanged(before.Stage, after.Stage, PictureMoved),
            LockAreas.Playlists => before.Playlists != after.Playlists,
            LockAreas.Media => before.Media != after.Media,
            LockAreas.Devices => before.Stage.Devices != after.Stage.Devices,
            // The node's links are machine settings, not the document: the show's gate holds the network area.
            _ => false,
        };
    }

    /// <summary>Everything on the stage but its device links and its surfaces (compared field by field: a surface's picture is
    /// content, its geometry mapping).</summary>
    private static bool StageChanged(Stage before, Stage after) =>
        !ReferenceEquals(before, after) && (before with { Devices = after.Devices, Surfaces = after.Surfaces }) != after;

    private static bool SurfacesChanged(Stage before, Stage after, Func<Surface, Surface, bool> moved, bool addedOrRemoved = false)
    {
        if (before.Surfaces == after.Surfaces)
            return false;
        if (before.Surfaces.Length != after.Surfaces.Length && addedOrRemoved)
            return true;
        foreach (var a in after.Surfaces)
        {
            var b = before.Surfaces.Find(a.Id);
            if (b is null)
            {
                if (addedOrRemoved)
                    return true;
                continue;
            }
            if (!ReferenceEquals(a, b) && moved(b, a))
                return true;
        }
        return false;
    }

    /// <summary>A surface's mapping moved: anything but its name, colour, tags, own lock flag and picture changed (mesh, faces,
    /// transform, masks, visibility, pixel quad, how its content is laid on it). A field no area names counts as mapping, the
    /// cautious side.</summary>
    private static bool GeometryMoved(Surface before, Surface after) =>
        (after with
        {
            Name = before.Name, Color = before.Color, Tags = before.Tags, Locked = before.Locked, Opacity = before.Opacity, Fx = before.Fx,
            Bindings = before.Bindings, Content = after.Content with { Source = before.Content.Source },
        }) != before;

    /// <summary>What a surface shows moved: the content.</summary>
    private static bool PictureMoved(Surface before, Surface after) =>
        !(before.Content.Source, before.Opacity, before.Fx, before.Bindings).Equals((after.Content.Source, after.Opacity, after.Fx, after.Bindings));

    private static bool OutputsChanged(Project before, Project after, Func<Output, Output, bool> moved, bool addedOrRemoved = false)
    {
        if (before.Outputs == after.Outputs)
            return false;
        if (before.Outputs.Length != after.Outputs.Length)
            return addedOrRemoved || after.Outputs.Any(a => before.Outputs.Find(a.Id) is { } b && moved(b, a));
        foreach (var a in after.Outputs)
        {
            var b = before.Outputs.Find(a.Id);
            if (b is null)
            {
                if (addedOrRemoved)
                    return true;
                continue;
            }
            if (!ReferenceEquals(a, b) && moved(b, a))
                return true;
        }
        return false;
    }

    /// <summary>An output's mapping moved: anything but its name, layout and content changed (warp, masks, blend, projector, the
    /// measured correction and the calibration it uses). A field no area names counts as mapping, the cautious side.</summary>
    private static bool MappingMoved(Output before, Output after) =>
        (after with
        {
            Name = before.Name, Kind = before.Kind, DisplayKey = before.DisplayKey, Width = before.Width, Height = before.Height,
            RefreshHz = before.RefreshHz, Mode = before.Mode, Plan = before.Plan, Rotation = before.Rotation, Enabled = before.Enabled,
            SourceRect = before.SourceRect, LatencyMs = before.LatencyMs, Source = before.Source, Color = before.Color, Fx = before.Fx,
        }) != before;

    /// <summary>An output's layout moved: its display, raster, refresh, mode, plan, rotation, rectangle, kind, latency or whether it
    /// is on.</summary>
    private static bool LayoutMoved(Output before, Output after) =>
        !(before.DisplayKey, before.Width, before.Height, before.RefreshHz, before.Mode, before.Plan, before.Rotation, before.Enabled,
            before.SourceRect, before.Kind, before.LatencyMs)
            .Equals((after.DisplayKey, after.Width, after.Height, after.RefreshHz, after.Mode, after.Plan, after.Rotation, after.Enabled,
                after.SourceRect, after.Kind, after.LatencyMs));

    /// <summary>What an output shows moved: its source, colour trim or effects.</summary>
    private static bool ContentMoved(Output before, Output after) =>
        !(before.Source, before.Color, before.Fx).Equals((after.Source, after.Color, after.Fx));

    private static string? EntityWords(Project before, Project after, ShowLock l)
    {
        var was = ShowEntities.Find(before, l.Target);
        if (was is null)
            return null;
        var now = ShowEntities.Find(after, l.Target);
        if (ReferenceEquals(was, now) || Equals(was, now) || OnlyLockedFlagMoved(was, now))
            return null;
        var (label, kind) = ShowEntities.Describe(was);
        var why = l.Words.Length > 0 ? $"'{l.Words}', " : "";
        return $"{label} ({kind}) is locked by {l.By} ({why}{Clock(l.AtUnixMs)}) — UNLOCK {l.Target} first";
    }

    /// <summary>A surface or projector whose own Locked flag is set counts as an entity lock (the desk's gizmo lock).</summary>
    private static string? FlaggedEntityWords(Project before, Project after)
    {
        if (before.Stage.Surfaces != after.Stage.Surfaces)
        {
            foreach (var s in before.Stage.Surfaces)
            {
                if (s.Locked && after.Stage.Surfaces.Find(s.Id) is var a && !ReferenceEquals(s, a) && !OnlyLockedFlagMoved(s, a))
                    return $"{s.Name} (surface) is locked — SET {s.Id} locked false first";
            }
        }
        if (before.Stage.Projectors != after.Stage.Projectors)
        {
            foreach (var p in before.Stage.Projectors)
            {
                if (p.Locked && after.Stage.Projectors.Find(p.Id) is var a && !ReferenceEquals(p, a) && !OnlyLockedFlagMoved(p, a))
                    return $"{p.Name} (projector) is locked — SET {p.Id} locked false first";
            }
        }
        return null;
    }

    private static bool OnlyLockedFlagMoved(object was, object? now) => (was, now) switch
    {
        (Surface s, Surface t) => (s with { Locked = t.Locked }) == t,
        (Projector p, Projector q) => (p with { Locked = q.Locked }) == q,
        _ => false,
    };

    private static string AreaWords(ShowLock l)
    {
        var why = l.Words.Length > 0 ? $"'{l.Words}', " : "";
        return $"{l.Target} is locked by {l.By} ({why}{Clock(l.AtUnixMs)}) — UNLOCK AREA {l.Target} first";
    }

    /// <summary>A lock's time as the operator's clock reads it (hours and minutes).</summary>
    public static string Clock(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Any entity of a project by its id, and how to name it in words: the lock guard's and LOCK's lookup.</summary>
public static class ShowEntities
{
    /// <summary>The entity with this id, searching the list its prefix names first and every list after that.</summary>
    public static object? Find(Project project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(id);
        return id.Length >= 2 && ByPrefix(project, id) is { } hit ? hit : Anywhere(project, id);
    }

    private static object? ByPrefix(Project p, string id) => id[..2] switch
    {
        Ids.Output => p.FindOutput(id),
        Ids.Surface => p.FindSurface(id),
        Ids.Projector => p.FindProjector(id),
        Ids.Mesh => p.FindMesh(id),
        Ids.Media => p.FindMedia(id),
        Ids.Playlist => p.FindPlaylist(id),
        Ids.Generator => p.FindGenerator(id),
        Ids.Canvas => p.FindCanvas(id),
        Ids.Device => p.FindDevice(id),
        Ids.ControlPoint => p.Stage.Controls.Find(id),
        Ids.Survey => p.Stage.Surveys.Find(id),
        Ids.Stack => p.FindStack(id),
        _ => null,
    };

    private static object? Anywhere(Project p, string id)
    {
        object?[] lists =
        [
            p.FindOutput(id), p.FindSurface(id), p.FindProjector(id), p.FindMesh(id), p.FindMedia(id), p.FindPlaylist(id), p.FindGenerator(id),
            p.FindCanvas(id), p.FindDevice(id), p.FindSend(id), p.Stage.Cameras.Find(id), p.Stage.Viewpoints.Find(id), p.Stage.Scans.Find(id),
            p.FindBinding(id), p.FindControlSurface(id), p.FindTimecodeSource(id), p.FindGeneratorPreset(id), p.FindStack(id), p.FindAlign(id),
            p.FindCalibration(id), p.Stage.Controls.Find(id), p.Stage.Surveys.Find(id),
        ];
        var hit = Array.Find(lists, x => x is not null);
        if (hit is not null)
            return hit;
        foreach (var list in p.Playlists)
        {
            if (list.Items.Find(id) is { } item)
                return item;
        }
        foreach (var c in p.Canvases)
        {
            if (c.Layers.Find(id) is { } layer)
                return layer;
        }
        return p.FindFx(id)?.Slot;
    }

    /// <summary>The entity's name and kind in words ("Left", "output").</summary>
    public static (string Label, string Kind) Describe(object entity) => entity switch
    {
        Output o => (o.Name, "output"),
        Surface s => (s.Name, "surface"),
        Projector p => (p.Name, "projector"),
        MeshAsset m => (m.Name, "mesh"),
        MediaItem m => (m.Name, "media"),
        Playlist l => (l.Name, "playlist"),
        PlaylistItem i => (i.Label, "item"),
        Generator g => (g.Name, "generator"),
        Canvas c => (c.Name, "canvas"),
        Layer l => (l.Name, "layer"),
        DeviceLink d => (d.Name, "device"),
        ControlPoint c => (c.Name, "control point"),
        SurveySource v => (v.Name, "survey"),
        StackGroup k => (k.Name, "stack"),
        IEntity e => (e.Id, "entity"),
        _ => ("?", "entity"),
    };
}
