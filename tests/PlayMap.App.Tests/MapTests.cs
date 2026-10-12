using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.Link;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Tests;

/// <summary>MAP: the outliner follows the engine's project through SET, ADD, REMOVE and UNDO; the pen tool,
/// driven by the pointer on the projector view, makes a closed path with smooth nodes and sends a mask the
/// engine accepts; the pin table colours each pin's live residual; a warp drag sends invariant numbers.</summary>
public class MapTests
{
    private static async Task<(DeskHarness Desk, Window Window)> OpenMap()
    {
        var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        DeskHarness.Click(window, MainWindowTests.Find<Avalonia.Controls.Primitives.ToggleButton>(window, "MapTab"));
        await Task.Yield();
        DeskHarness.Pump();
        return (desk, window);
    }

    [AvaloniaFact]
    public async Task TheOutlinerFollowsSetAddRemoveAndUndo()
    {
        var (desk, window) = await OpenMap();
        await using var _ = desk;
        var surfaces = desk.Main.Map.Outliner[0];
        Assert.Equal(["Back wall", "Cube", "Column"], surfaces.Nodes.Select(n => n.Name));

        await desk.Session.SendAsync("SET sf_wall name \"Lobby wall\"", Ct);
        DeskHarness.Pump();
        Assert.Equal("Lobby wall", surfaces.Nodes[0].Name);
        var names = MainWindowTests.Find<ItemsControl>(window, "Outliner").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Lobby wall", names);

        var added = await desk.Session.SendAsync("ADD surface " + ProjectJson.SerializeEntity(new Surface("", "Door", "ms_wall", Transform3.Identity, [],
            SurfaceContent.Empty, 1, [], true, false, "#FFFFFF", [])), Ct);
        DeskHarness.Pump();
        Assert.Equal(4, surfaces.Nodes.Count);
        Assert.Equal(added.Text, surfaces.Nodes[3].Id);
        Assert.Equal("4", surfaces.Count);

        await desk.Session.SendAsync("REMOVE " + added.Text, Ct);
        DeskHarness.Pump();
        Assert.Equal(3, surfaces.Nodes.Count);

        await desk.Session.SendAsync("UNDO", Ct);
        DeskHarness.Pump();
        Assert.Equal(4, surfaces.Nodes.Count);
        await desk.Session.SendAsync("UNDO", Ct);
        await desk.Session.SendAsync("UNDO", Ct);
        DeskHarness.Pump();
        Assert.Equal(["Back wall", "Cube", "Column"], surfaces.Nodes.Select(n => n.Name));

        // The outliner's own buttons are verbs too: the eye sends SURFACE SHOW.
        var cube = surfaces.Nodes[1];
        cube.EyeAction!.Command.Execute(null);
        await cube.EyeAction.Command.Last;
        DeskHarness.Pump();
        Assert.Equal("SURFACE SHOW sf_cube OFF", desk.Verbs()[^1]);
        Assert.False(cube.Visible);
        Screens.Save(window, "map-3d");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ThePenToolDrawsAClosedPathWithSmoothNodesAndSendsAValidMask()
    {
        var (desk, window) = await OpenMap();
        await using var _ = desk;
        var map = desk.Main.Map;
        map.Select("projector", "pj_left");
        map.IsProjectorView = true;
        DeskHarness.Pump();
        DeskHarness.Click(window, MainWindowTests.Find<Avalonia.Controls.Primitives.ToggleButton>(window, "PenToolButton"));
        Assert.Equal(MapTool.Pen, map.Tool);
        var canvas = MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView");
        Point At(double x, double y) => canvas.TranslatePoint(canvas.PointFor(new Vec2d(x, y)), window)!.Value;

        // A corner, a smooth node (press and drag), a corner, a corner Alt-clicked smooth, then the first node closes.
        window.MouseDown(At(0.40, 0.30), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(At(0.40, 0.30), MouseButton.Left, RawInputModifiers.None);
        window.MouseDown(At(0.70, 0.30), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(At(0.74, 0.33), RawInputModifiers.LeftMouseButton);
        window.MouseMove(At(0.78, 0.36), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(0.78, 0.36), MouseButton.Left, RawInputModifiers.None);
        window.MouseDown(At(0.70, 0.70), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(At(0.70, 0.70), MouseButton.Left, RawInputModifiers.None);
        window.MouseDown(At(0.40, 0.70), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(At(0.40, 0.70), MouseButton.Left, RawInputModifiers.None);
        window.MouseDown(At(0.40, 0.70), MouseButton.Left, RawInputModifiers.Alt);
        window.MouseUp(At(0.40, 0.70), MouseButton.Left, RawInputModifiers.Alt);
        Assert.Equal(4, map.Pen.Nodes.Count);
        Assert.Equal([false, true, false, true], map.Pen.Nodes.Select(n => n.Smooth));
        Assert.Equal(new Vec2d(0.08, 0.06), Round(map.Pen.Nodes[1].Out));
        Assert.Equal(-map.Pen.Nodes[1].Out, map.Pen.Nodes[1].In);
        Screens.Save(window, "map-pen");

        window.MouseDown(At(0.40, 0.30), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(At(0.40, 0.30), MouseButton.Left, RawInputModifiers.None);
        await map.LastPenCommit;
        DeskHarness.Pump();

        var line = desk.Verbs().Single(l => l.StartsWith("ADD mask ", StringComparison.Ordinal));
        var json = line["ADD mask ".Length..];
        Assert.Equal("op_1", JsonNode.Parse(json)!["parent"]!.GetValue<string>());
        var mask = ProjectJson.DeserializeEntity<Mask>(json);
        Assert.True(mask.Path.Closed);
        Assert.Equal(4, mask.Path.Nodes.Length);
        Assert.Equal(2, mask.Path.Nodes.Count(n => n.Smooth));
        var masks = desk.Link.CurrentProject.FindOutput("op_1")!.Masks;
        Assert.Equal(2, masks.Length);
        Assert.Equal(masks[1].Id, map.SelectedMaskId);
        Assert.False(map.Pen.IsDrawing);
        window.Close();
    }

    private static Vec2d Round(Vec2d v) => new(Math.Round(v.X, 3), Math.Round(v.Y, 3));

    [AvaloniaFact]
    public async Task ThePinTableColoursEachPinsResidualAndSolveSendsTheVerb()
    {
        var (desk, window) = await OpenMap();
        await using var _ = desk;
        desk.Main.Map.Select("projector", "pj_left");
        DeskHarness.Pump();
        var inspector = Assert.IsType<ProjectorInspector>(desk.Main.Map.Inspector.Current);
        Assert.Equal(8, inspector.Pins.Count);
        var levels = inspector.Pins.Select(p => p.Level).ToList();
        Assert.Contains("good", levels);
        Assert.Contains("fair", levels);
        Assert.Contains("poor", levels);
        foreach (var row in inspector.Pins)
            Assert.Equal(row.ResidualPx < 1 ? "good" : row.ResidualPx < 3 ? "fair" : "poor", row.Level);

        var badges = MainWindowTests.Find<ItemsControl>(window, "PinTable").GetVisualDescendants().OfType<Border>().Where(b => b.Name == "Residual").ToList();
        Assert.Equal(8, badges.Count);
        Color ColorOf(string word) => ((ISolidColorBrush)LightBrushConverter.Brushes.For(word)).Color;
        for (var i = 0; i < 8; i++)
            Assert.Equal(ColorOf(inspector.Pins[i].Level), ((ISolidColorBrush)badges[i].Background!).Color);
        Assert.Equal(Color.Parse("#3FB950"), ColorOf("good"));
        Assert.Equal(Color.Parse("#D29922"), ColorOf("fair"));
        Assert.Equal(Color.Parse("#E5484D"), ColorOf("poor"));

        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "SolveButton"));
        await inspector.Solve.Command.Last;
        DeskHarness.Pump();
        Assert.Equal("PROJECTOR SOLVE pj_left", desk.Verbs()[^1]);
        Assert.Contains("px RMS", inspector.SolveText, StringComparison.Ordinal);
        Screens.Save(window, "map-projector-inspector");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AWarpDragSendsWarpNodeWithInvariantNumbers()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var (desk, window) = await OpenMap();
            await using var _ = desk;
            var map = desk.Main.Map;
            map.Select("projector", "pj_left");
            map.Tool = MapTool.Warp;
            DeskHarness.Pump();
            var canvas = MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView");
            var lattice = desk.Link.CurrentProject.FindOutput("op_1")!.Warp.Lattice!;
            var rest = new Vec2d(2 / 4.0, 1 / 3.0);
            var start = canvas.TranslatePoint(canvas.PointFor(rest + lattice.Offset(2, 1)), window)!.Value;
            var end = canvas.TranslatePoint(canvas.PointFor(rest + new Vec2d(0.05, 0.025)), window)!.Value;
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), RawInputModifiers.LeftMouseButton);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            await desk.FlushThrottle();
            var warp = desk.Verbs().Where(l => l.StartsWith("WARP NODE op_1 2 1 ", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(warp);
            Assert.DoesNotContain(warp, l => l.Contains(','));
            var offset = desk.Link.CurrentProject.FindOutput("op_1")!.Warp.Lattice!.Offset(2, 1);
            Assert.Equal(0.05, offset.X, 2);
            Assert.Equal(0.025, offset.Y, 2);
            Screens.Save(window, "map-warp");
            window.Close();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
