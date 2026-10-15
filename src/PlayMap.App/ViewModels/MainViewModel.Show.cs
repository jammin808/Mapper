using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels.Show;
using PlayMap.App.Views.Show;

namespace PlayMap.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The show strip's module (Show Mode, panic, locks).</summary>
    public ShowStripModule ShowModule { get; private set; } = null!;

    partial void ComposeShowDesk(List<DeskModule> modules)
    {
        ModuleViews.Register<ShowStripViewModel>(() => new ShowStripView());
        ShowModule = new ShowStripModule(Session, Prefs, () => Workspace == Workspace.Play, () => Drawers.Show("health"));
        modules.Add(ShowModule);
    }
}
