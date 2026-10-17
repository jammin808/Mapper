using PlayMap.App.Link;
using PlayMap.App.State;

namespace PlayMap.App.ViewModels.Health;

/// <summary>The health package's module: Health ▸ Fleet (the fleet board) and Health ▸ Preflight (the preflight board, which reads its
/// report only while it is on screen).</summary>
public sealed class HealthModule : DeskModule
{
    private readonly DrawerHostViewModel _drawers;

    public HealthModule(DeskSession session, DrawerHostViewModel drawers)
    {
        ArgumentNullException.ThrowIfNull(drawers);
        _drawers = drawers;
        Preflight = new PreflightBoardViewModel(session);
        Fleet = new FleetBoardViewModel(session);
        drawers.Opened += _ => Shown();
        drawers.Health.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DrawerViewModel.Selected))
                Shown();
        };
    }

    public PreflightBoardViewModel Preflight { get; }

    public FleetBoardViewModel Fleet { get; }

    /// <summary>The Health tab the Ready light opens: the fleet board when other machines are heard, else the preflight board.</summary>
    public string ReadyTab => Fleet.Facts.Machines.IsDefaultOrEmpty ? "preflight" : "fleet";

    private void Shown() => Preflight.IsShown = _drawers.Open == _drawers.Health && ReferenceEquals(_drawers.Health.Selected?.Content, Preflight);

    public override IEnumerable<DrawerTab> DrawerTabs() =>
    [
        new DrawerTab("health", "fleet", "Fleet", Fleet, 0),
        new DrawerTab("health", "preflight", "Preflight", Preflight, 10),
    ];

    public override void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Fleet.OnState(state);
        Preflight.OnState(state);
    }

    public override void RefreshActions()
    {
        Fleet.RefreshActions();
        Preflight.RefreshActions();
    }
}
