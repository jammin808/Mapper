using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels.Ux;

namespace PlayMap.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The consoles' safety patterns: the GO guard, blind made unmistakable, the Oops history, the look, closing while armed.</summary>
    public UxModule Ux { get; private set; } = null!;

    /// <summary>True while this desk edits its own blind draft (the grey variant, BLIND on every pane that edits it, the blind frame).</summary>
    public bool IsBlindMine => Ux.IsBlindMine;

    /// <summary>The word every pane that edits the draft carries while it is this desk's.</summary>
    public string BlindTag => Ux.BlindTag;

    /// <summary>The veil over the desk for its own brightness (0 at 100 %).</summary>
    public double VeilOpacity => Ux.VeilOpacity;

    /// <summary>True while the confirmation before closing an armed desk is up.</summary>
    public bool IsCloseAsked => Ux.IsCloseAsked;

    /// <summary>Undo's right-click: Undo, Redo, the Oops history and the look (the bar's narrow forms keep them here).</summary>
    public IReadOnlyList<DeskAction> UndoMenu { get; private set; } = [];

    /// <summary>The help overlay's "First light guide": MAP with the guide open again.</summary>
    public RelayCommand OpenFirstLight { get; private set; } = null!;

    partial void ComposeUxDesk(List<DeskModule> modules)
    {
        Ux = new UxModule(Session, Prefs, _nowMs, () => ShowModule.Strip.Blind.OwnsDraft);
        Ux.Changed += () => Raise(nameof(IsBlindMine), nameof(VeilOpacity), nameof(IsCloseAsked));
        // A window opened or closed, a flash, the refusal's words: every GO control and the attention bar are drawn again from the push
        // the desk holds (nothing is asked of the node).
        Ux.GuardMoved += () => OnState(Session.State);
        ModuleViews.Register<OopsPanelViewModel>(() => new Views.Ux.OopsPanelView());
        UndoMenu = [Undo, Redo, Ux.Oops.Toggle, Ux.Look, Ux.MapAnything];
        OpenFirstLight = new RelayCommand(() =>
        {
            IsHelpOpen = false;
            Workspace = Workspace.Map;
            Map.Guide.Reopen();
        });
        modules.Add(Ux);
    }
}
