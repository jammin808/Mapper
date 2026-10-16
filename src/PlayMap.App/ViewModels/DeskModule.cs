using System.Collections.Immutable;
using PlayMap.App.State;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels;

/// <summary>One line of the attention bar: what needs a person now, in words, with the verbs that answer it.
/// Banners are rebuilt from STATE on every push, so one disappears the moment its cause clears; the lower the
/// priority, the higher it sits.</summary>
public sealed record Banner(string Id, string Light, string Words, ImmutableArray<DeskAction> Actions, int Priority)
{
    public bool IsRed => Light == "red";
}

/// <summary>A small mark on an object's row (a padlock, a format chip, a fault dot): its glyph key, its short text
/// and the words its tooltip carries.</summary>
public sealed record ObjectBadge(string Id, string Icon, string Text, string Light, string Tip);

/// <summary>Anything a right-click menu, the inspector or a badge is about: its id (the kind follows from the id's
/// prefix, so a row and the inspector of the same object name the same target).</summary>
public interface IMenuTarget
{
    string Id { get; }

    string Kind => MenuTargets.KindOf(Id);

    /// <summary>The object's own actions (its menu before the modules' entries; Bind ▸ is built from them).</summary>
    IReadOnlyList<DeskAction> MenuActions => [];
}

/// <summary>The kinds of the model's ids, by prefix (Core's <see cref="Ids"/>).</summary>
public static class MenuTargets
{
    public static string KindOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length < 3 || id[2] != '_')
            return "";
        return id[..2] switch
        {
            Ids.Media => "media",
            Ids.Playlist => "playlist",
            Ids.PlaylistItem => "item",
            Ids.Canvas => "canvas",
            Ids.Layer => "layer",
            Ids.Generator => "generator",
            Ids.Surface => "surface",
            Ids.Mesh => "mesh",
            Ids.Projector => "projector",
            Ids.Output => "output",
            Ids.Camera => "camera",
            Ids.Scan => "scan",
            Ids.Mask => "mask",
            Ids.Fx => "fx",
            Ids.Device => "device",
            Ids.Send => "send",
            _ => "",
        };
    }
}

/// <summary>A tab a module brings to one of the four drawers ("outputs", "inputs", "health", "network"); its
/// content is a view model whose view is registered with <see cref="Infrastructure.ModuleViews"/>.</summary>
public sealed record DrawerTab(string Drawer, string Key, string Title, object Content, int Order = 100);

/// <summary>A part of the desk a round-2 package brings (the show strip, the cards, the fleet board…): it reads the
/// mirror, offers slots, banners, shortcuts, right-click extras, badges, inspector sections and drawer tabs, and sends
/// only verbs. The window composes modules through one partial method per package, so packages never edit the same
/// file. Every hook is called on the UI thread.</summary>
public abstract class DeskModule
{
    public virtual void OnState(StateSnapshot state)
    {
    }

    public virtual void OnProject(Project project)
    {
    }

    public virtual void Tick()
    {
    }

    /// <summary>The view model for a named top-bar slot ("show", "timecode", "tempo"), or null.</summary>
    public virtual object? Slot(string name) => null;

    public virtual IEnumerable<Banner> Banners(StateSnapshot state) => [];

    /// <summary>Keyboard rows the module adds to the one table (F1 lists them).</summary>
    public virtual IEnumerable<Shortcut> Shortcuts() => [];

    /// <summary>Entries appended to a target's right-click menu. The same instances appear on the target's
    /// inspector toolbar (the Extras section), so the menu and the toolbar never disagree; return stable
    /// instances per target.</summary>
    public virtual IEnumerable<DeskAction> MenuExtras(IMenuTarget target) => [];

    /// <summary>Badges for a target's rows, from the latest STATE and project.</summary>
    public virtual IEnumerable<ObjectBadge> Badges(IMenuTarget target) => [];

    /// <summary>View models shown under the inspector's own fields for a target (registered views).</summary>
    public virtual IEnumerable<object> InspectorSections(IMenuTarget target) => [];

    public virtual IEnumerable<DrawerTab> DrawerTabs() => [];

    /// <summary>Why the desk should not send this line now (the node's words and the menu's tag), or null to leave it live.</summary>
    public virtual Link.GateRefusal? Gate(PlayMap.Core.Wire.WireLine line, PlayMap.Core.Wire.VerbSpec spec) => null;

    /// <summary>Esc reached this module (in the ladder's order); true when it used the key.</summary>
    public virtual bool TryEscape() => false;

    /// <summary>The ladder's rung (EscapeLadder's constants): transient work before overlays before PANIC.</summary>
    public virtual int EscapeOrder => int.MaxValue;
}
