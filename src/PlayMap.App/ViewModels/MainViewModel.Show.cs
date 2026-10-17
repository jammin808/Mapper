using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels.Health;
using PlayMap.App.ViewModels.Show;
using PlayMap.App.Views.Health;
using PlayMap.App.Views.Show;

namespace PlayMap.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The show strip's module (Show Mode, panic, locks, the blind chip).</summary>
    public ShowStripModule ShowModule { get; private set; } = null!;

    /// <summary>The health boards' module (Health ▸ Fleet, Health ▸ Preflight, the failover alarm).</summary>
    public HealthModule Health { get; private set; } = null!;

    partial void ComposeShowDesk(List<DeskModule> modules)
    {
        ModuleViews.Register<ShowStripViewModel>(() => new ShowStripView());
        ModuleViews.Register<PreflightBoardViewModel>(() => new PreflightBoardView());
        ModuleViews.Register<FleetBoardViewModel>(() => new FleetBoardView());
        Health = new HealthModule(Session, Drawers);
        ModuleViews.Register<BlindChipViewModel>(() => new BlindPanelView());
        ShowModule = new ShowStripModule(Session, Prefs, () => Workspace == Workspace.Play, () => Drawers.Show("health", Health.ReadyTab),
            () => Map.ShowDraft, on => Map.ShowDraft = on);
        modules.Add(ShowModule);
        modules.Add(Health);
    }
}
