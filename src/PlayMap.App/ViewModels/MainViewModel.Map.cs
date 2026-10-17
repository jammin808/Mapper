namespace PlayMap.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The map package's module: the wall's lease and the first light's hand-over to MAP.</summary>
    public MapModule MapDesk { get; private set; } = null!;

    partial void ComposeMapDesk(List<DeskModule> modules)
    {
        MapDesk = new MapModule(Map);
        Map.Lit += () => Workspace = Workspace.Map;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workspace) && Workspace != Workspace.Map)
                Map.LeaveMap();
        };
        modules.Add(MapDesk);
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
