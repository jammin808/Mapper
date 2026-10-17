using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels.Map;
using PlayMap.App.Views.Map;

namespace PlayMap.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The map package's module: the wall's lease and the first light's hand-over to MAP.</summary>
    public MapModule MapDesk { get; private set; } = null!;

    /// <summary>Health ▸ Calibration and the projectors' score cards.</summary>
    public CalibrationModule Calibration { get; private set; } = null!;

    partial void ComposeMapDesk(List<DeskModule> modules)
    {
        ModuleViews.Register<CalibrationTabViewModel>(() => new CalibrationTabView());
        ModuleViews.Register<ScoreCardViewModel>(() => new ScoreCardView());
        MapDesk = new MapModule(Map);
        Calibration = new CalibrationModule(Session, Drawers);
        Map.Lit += () => Workspace = Workspace.Map;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Workspace))
                return;
            if (Workspace == Workspace.Map)
                Map.EnterMap();
            else
                Map.LeaveMap();
        };
        modules.Add(MapDesk);
        modules.Add(Calibration);
    }
}

/// <summary>MAP's part of the module list: the window's tick renews this desk's overlay lease on the wall.</summary>
public sealed class MapModule(MapViewModel map) : DeskModule
{
    public override void Tick() => map.TickWall();

    public override void RefreshActions()
    {
        map.OnWall.Refresh();
        map.Guide.RefreshActions();
    }
}
