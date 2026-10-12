using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Tests;

/// <summary>The desk drawn in representative states — a show playing, a generator open, the stage in 3D with
/// a surface selected, a projector's view with its pins, a finished scan's review, the drawers. Each asserts
/// the facts its picture shows; with PLAYMAP_SCREENS_DIR set the pictures are saved for the papers.</summary>
public class ScreensTests
{
    [AvaloniaFact]
    public async Task TheDeskDrawsAShowPlayingWithItsItemInspector()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        await desk.Session.SendAsync("PLAYLIST GO pl_show", Ct);
        await desk.Session.SendAsync("PLAYLIST GO pl_amb", Ct);
        desk.Clock.AdvanceMs(62_400);
        desk.Link.Tick();
        DeskHarness.Pump();
        var show = desk.Main.Play.Playlists.Single(p => p.Id == "pl_show");
        show.SelectedItem = show.Items[0];
        DeskHarness.Pump();
        Assert.Equal("1:02", desk.Main.Master.PositionText);
        Assert.Equal("−2:28", desk.Main.Master.RemainingText);
        Assert.Equal("Show 2:28", desk.Main.VtChip);
        Assert.True(show.Items[0].IsCurrent);
        Assert.IsType<PlaylistItemInspector>(desk.Main.Play.Inspector.Current);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Screens.Save(window, "play");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDeskDrawsTheStageAndAProjectorsView()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        desk.Main.Map.Select("surface", "sf_cube");
        DeskHarness.Pump();
        Assert.IsType<SurfaceInspector>(desk.Main.Map.Inspector.Current);
        Assert.True(MainWindowTests.Find<MapViewport>(window, "Viewport").IsEffectivelyVisible);
        Screens.Save(window, "map-3d");

        desk.Main.Map.Select("projector", "pj_left");
        desk.Main.Map.Tool = MapTool.Pin;
        DeskHarness.Pump();
        Assert.True(desk.Main.Map.IsProjectorView);
        Assert.True(MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView").IsEffectivelyVisible);
        Assert.Contains("VIEW pj-pj_left SHOW projector:pj_left 1920x1080", desk.Link.SentLines);
        Screens.Save(window, "map-projector");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDeskDrawsAFinishedScansReview()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Scan;
        await desk.Session.SendAsync("SCAN START cm_webcam pj_left pj_right MODE ROBUST", Ct);
        desk.Clock.AdvanceMs(30_000);
        desk.Link.Tick();
        DeskHarness.Pump();
        var scan = desk.Main.Scan;
        scan.Current = 4;
        scan.PickAt(0.25, 0.65);
        DeskHarness.Pump();
        Assert.Equal("cube", scan.SelectedRegion!.Label);
        Assert.Contains($"VIEW scan-{scan.ScanId}-smart SHOW scan:{scan.ScanId}:smart 1280x720", desk.Link.SentLines);
        Screens.Save(window, "scan-review");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDeskDrawsItsDrawers()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.IsOutputsOpen = true;
        desk.Main.Outputs.Selected = desk.Main.Outputs.Tiles[0];
        DeskHarness.Pump();
        Assert.Equal(["", "DISPLAY1", "DISPLAY2", "DISPLAY3"], ((OutputInspector)desk.Main.Outputs.Inspector.Current!).DisplayChoices);
        Screens.Save(window, "outputs");
        desk.Main.IsNetworkOpen = true;
        DeskHarness.Pump();
        Assert.False(desk.Main.IsOutputsOpen);
        Screens.Save(window, "network");
        window.Close();
    }
}
