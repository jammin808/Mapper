using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests.Quad;

/// <summary>The quad tests' shared moves: MAP on a projector's view, the headless pointer on the view, and whether a line changes the
/// project.</summary>
internal static class QuadRig
{
    public static (Window Window, ProjectorCanvas Canvas, MapViewModel Map) OpenMap(DeskHarness desk, string projector = "pj_left")
    {
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var map = desk.Main.Map;
        map.Select("projector", projector);
        map.IsProjectorView = true;
        DeskHarness.Pump();
        return (window, MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView"), map);
    }

    public static void Key(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None) => QuadToolTests.Key(window, key, mods);

    public static void ClickWall(Window window, ProjectorCanvas canvas, double x, double y) => QuadToolTests.ClickWall(window, canvas, x, y);

    /// <summary>A press at one point of the view, moves to the others, and the release at the last.</summary>
    public static void Drag(Window window, ProjectorCanvas canvas, Avalonia.Point from, params Avalonia.Point[] to)
    {
        var start = canvas.TranslatePoint(from, window)!.Value;
        window.MouseMove(start, RawInputModifiers.None);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        DeskHarness.Pump();
        var at = start;
        foreach (var p in to)
        {
            at = canvas.TranslatePoint(p, window)!.Value;
            window.MouseMove(at, RawInputModifiers.LeftMouseButton);
            DeskHarness.Pump();
        }
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        DeskHarness.Pump();
    }

    /// <summary>True when a line would change the project or the show (a mutating row that is not ephemeral).</summary>
    public static bool Changes(string line) =>
        WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && m.Spec.Mutates && !m.Spec.Ephemeral;

    /// <summary>Draws one quad on the shown projector (Q, four clicks, Enter) and returns it as the node has it.</summary>
    public static async Task<Surface> DrawQuad(DeskHarness desk, Window window, ProjectorCanvas canvas, double inset = 0.25)
    {
        var map = desk.Main.Map;
        Key(window, Avalonia.Input.Key.Q);
        foreach (var (x, y) in new[] { (inset, inset), (1 - inset, inset), (1 - inset, 1 - inset), (inset, 1 - inset) })
            ClickWall(window, canvas, x, y);
        Key(window, Avalonia.Input.Key.Enter);
        await map.LastQuad;
        DeskHarness.Pump();
        return desk.Link.CurrentProject.Stage.Surfaces.Last(s => s.Pixel is not null);
    }

    public static Vec2d Px(Vec2d wall) => new(Math.Round(wall.X * 1920, 2), Math.Round(wall.Y * 1080, 2));
}
