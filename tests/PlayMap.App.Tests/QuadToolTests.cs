using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.App.Tests;

/// <summary>The quad tool through the real input path: Q arms it on the projector view, four clicks on the wall draw a rubber band
/// on the desk and on the wall (CURSOR with its trail) and send no quad, Enter sends exactly one SURFACE QUAD in the output's wall
/// pixels — the line a real engine takes, leaving the same corners — and selects the new quad with its first corner (HANDLE SELECT);
/// the arrows move that corner in wall pixels (Alt+Right: SURFACE CORNER &lt;sf&gt; TL BY 0.1 0 PX, plain 1 px, Shift 10 px), Tab and
/// Shift+Tab pick the next and previous corner, while the view has the keyboard (a focused slider keeps its arrows, and Tab its
/// walk); Ctrl+Left stays the transport's; Esc drops a quad half drawn, and showing another projector keeps it as its own projector's
/// draft. A right-click on a quad
/// is the quad's menu.</summary>
public class QuadToolTests
{
    internal static readonly SessionInfo DeskOnNode = new("demo", "desk", "127.0.0.1", true, Link.FakeEngineLink.ClientName, true);

    internal static void Key(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None, PhysicalKey physical = PhysicalKey.None)
    {
        window.KeyPress(key, mods, physical, "");
        DeskHarness.Pump();
    }

    /// <summary>A click with the headless mouse at a wall point of the projector view.</summary>
    internal static void ClickWall(Window window, ProjectorCanvas canvas, double x, double y)
    {
        var at = canvas.TranslatePoint(canvas.PointFor(new Vec2d(x, y)), window)!.Value;
        window.MouseMove(at, RawInputModifiers.None);
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        DeskHarness.Pump();
    }

    internal static async Task<CommandReply> Run(EngineCore engine, string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out _), text);
        return await engine.HandleAsync(line, DeskOnNode, Ct);
    }

    [AvaloniaFact]
    public async Task QFourClicksAndEnterSendExactlyOneSurfaceQuadAndTheArrowsMoveItsCorner()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        Key(window, Avalonia.Input.Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2);
        var map = desk.Main.Map;
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        Key(window, Avalonia.Input.Key.Q, physical: PhysicalKey.Q);
        Assert.Equal(MapTool.Quad, map.Tool);
        Assert.True(map.IsProjectorView);
        Assert.Equal("op_1", map.WallOutput!.Id);
        var canvas = MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView");
        var before = desk.Link.CurrentProject;
        desk.Link.ClearSent();

        // Four clicks in any order: drawn at once, the wall's rubber band follows (CURSOR with the clicks so far), and no quad yet.
        (double X, double Y)[] corners = [(0.25, 0.2), (0.75, 0.2), (0.75, 0.8), (0.25, 0.8)];
        foreach (var (x, y) in corners)
            ClickWall(window, canvas, x, y);
        await desk.FlushThrottle();
        Assert.Equal(4, map.Quad.Clicks.Count);
        Assert.True(map.Quad.IsReady);
        Assert.DoesNotContain(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal));
        // The wall's cursor is at the last click with the earlier ones as its trail (a line per click, or the throttle's latest).
        var cursors = desk.Verbs().Where(l => l.StartsWith("CURSOR ", StringComparison.Ordinal)).ToList();
        Assert.InRange(cursors.Count, 1, 4);
        Assert.Equal("CURSOR op_1 0.25 0.8 TRAIL 0.25 0.2 0.75 0.2 0.75 0.8", cursors[^1]);
        Assert.All(cursors, c => Assert.Null(DeskVerbRuleTests.Fault(c)));

        // Enter: exactly one SURFACE QUAD, in op_1's wall pixels.
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Enter, physical: PhysicalKey.Enter);
        await map.LastQuad;
        DeskHarness.Pump();
        const string Expected = "SURFACE QUAD pj_left 480 216 1440 216 1440 864 480 864 PX";
        Assert.Equal(Expected, Assert.Single(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal)));
        Assert.Null(DeskVerbRuleTests.Fault(Expected));
        Assert.False(map.Quad.IsDrawing);
        var quad = desk.Link.CurrentProject.Stage.Surfaces.Single(s => s.Pixel is not null);
        Assert.Equal("pj_left", quad.Pixel!.ProjectorId);
        // The new quad is selected with its first corner, on the desk and on the wall.
        Assert.Equal(quad.Id, map.SelectedQuad!.Id);
        Assert.Equal(0, map.SelectedCorner);
        Assert.Contains("HANDLE SELECT " + quad.Id + " TL", desk.Link.SentLines);

        // A real engine given the same show takes the same line and leaves the same corners (the line means what the desk drew).
        await using (var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), before))
        {
            var reply = await Run(engine, Expected);
            Assert.True(reply.Ok, reply.Payload);
            var real = engine.Store.Project.Stage.Surfaces.Single(s => s.Pixel is not null).Pixel!;
            foreach (var (a, b) in new[] { (real.TopLeft, quad.Pixel.TopLeft), (real.TopRight, quad.Pixel.TopRight), (real.BottomRight, quad.Pixel.BottomRight), (real.BottomLeft, quad.Pixel.BottomLeft) })
            {
                Assert.Equal(a.X, b.X, 6);
                Assert.Equal(a.Y, b.Y, 6);
            }
            // The arrows' lines run there too.
            var realId = engine.Store.Project.Stage.Surfaces.Single(s => s.Pixel is not null).Id;
            var nudged = await Run(engine, $"SURFACE CORNER {realId} TL BY 0.1 0 PX");
            Assert.True(nudged.Ok, nudged.Payload);
        }

        // Alt+Right: 0.1 px right; Right: 1 px; Shift+Down: 10 px down — each one line, on the selected corner.
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Right, RawInputModifiers.Alt, PhysicalKey.ArrowRight);
        Assert.Equal($"SURFACE CORNER {quad.Id} TL BY 0.1 0 PX", Assert.Single(desk.Verbs()));
        Key(window, Avalonia.Input.Key.Right, physical: PhysicalKey.ArrowRight);
        Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown);
        Assert.Equal([$"SURFACE CORNER {quad.Id} TL BY 0.1 0 PX", $"SURFACE CORNER {quad.Id} TL BY 1 0 PX", $"SURFACE CORNER {quad.Id} TL BY 0 10 PX"], desk.Verbs());
        Assert.All(desk.Verbs(), l => Assert.Null(DeskVerbRuleTests.Fault(l)));
        // The node moved the corner: 11.1 px right and 10 px down on the wall from where it was.
        var moved = desk.Link.CurrentProject.FindSurface(quad.Id)!.Pixel!;
        Assert.NotEqual(quad.Pixel.TopLeft, moved.TopLeft);
        Assert.Equal(quad.Pixel.TopRight, moved.TopRight);

        // Tab: the next corner (the wall shows it), and the arrows move that one; Shift+Tab back.
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Tab, physical: PhysicalKey.Tab);
        Assert.Equal(1, map.SelectedCorner);
        Key(window, Avalonia.Input.Key.Right, RawInputModifiers.Alt, PhysicalKey.ArrowRight);
        Key(window, Avalonia.Input.Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab);
        Assert.Equal(0, map.SelectedCorner);
        Assert.Equal([$"HANDLE SELECT {quad.Id} TR", $"SURFACE CORNER {quad.Id} TR BY 0.1 0 PX", $"HANDLE SELECT {quad.Id} TL"], desk.Verbs());

        // Ctrl+Left is still the transport's previous item in MAP.
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Left, RawInputModifiers.Control, PhysicalKey.ArrowLeft);
        await desk.Main.Master.Prev.Command.Last;
        Assert.StartsWith("PLAYLIST PREV ", Assert.Single(desk.Verbs()), StringComparison.Ordinal);
        Screens.Save(window, "map-quad");
        window.Close();
    }

    /// <summary>Draws one quad on pj_left's view (Q, four clicks, Enter) and answers it, selected with its first corner.</summary>
    private static async Task<Surface> DrawQuad(DeskHarness desk, Window window, ProjectorCanvas canvas)
    {
        var map = desk.Main.Map;
        Key(window, Avalonia.Input.Key.Q, physical: PhysicalKey.Q);
        foreach (var (x, y) in new[] { (0.3, 0.3), (0.7, 0.3), (0.7, 0.7), (0.3, 0.7) })
            ClickWall(window, canvas, x, y);
        Key(window, Avalonia.Input.Key.Enter, physical: PhysicalKey.Enter);
        await map.LastQuad;
        DeskHarness.Pump();
        var quad = desk.Link.CurrentProject.Stage.Surfaces.Single(s => s.Pixel is not null);
        Assert.Equal(quad.Id, map.SelectedQuad!.Id);
        // Selected with its inspector, once the project that names it has arrived.
        Assert.Equal(quad.Id, Assert.IsType<SurfaceInspector>(map.Inspector.Current).Id);
        return quad;
    }

    [AvaloniaFact]
    public async Task AFocusedControlKeepsItsArrowsAndTabAndTheViewHasThemBack()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var map = desk.Main.Map;
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        var canvas = MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView");
        var quad = await DrawQuad(desk, window, canvas);

        // The quad's own inspector: its Opacity slider has the keyboard. Left moves the slider, not the quad's corner on the wall,
        // and Tab walks on to the next control instead of the next corner.
        var slider = MainWindowTests.Find<Slider>(window, "SurfaceOpacity");
        Assert.True(slider.Focus());
        var before = slider.Value;
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Left, physical: PhysicalKey.ArrowLeft);
        Assert.DoesNotContain(desk.Link.SentLines, l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal));
        Assert.True(slider.Value < before, $"{slider.Value} after {before}");
        Key(window, Avalonia.Input.Key.Tab, physical: PhysicalKey.Tab);
        Assert.DoesNotContain(desk.Link.SentLines, l => l.StartsWith("HANDLE SELECT", StringComparison.Ordinal));
        Assert.Equal(0, map.SelectedCorner);
        Assert.False(slider.IsFocused);

        // The projector view has the keyboard again: the arrows and Tab are the quad's.
        Assert.True(canvas.Focus());
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Right, RawInputModifiers.Alt, PhysicalKey.ArrowRight);
        Key(window, Avalonia.Input.Key.Tab, physical: PhysicalKey.Tab);
        Assert.Equal([$"SURFACE CORNER {quad.Id} TL BY 0.1 0 PX", $"HANDLE SELECT {quad.Id} TR"],
            desk.Link.SentLines.Where(l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal) || l.StartsWith("HANDLE SELECT", StringComparison.Ordinal)).ToArray());
        window.Close();
    }

    [AvaloniaFact]
    public async Task ARightClickOnAQuadIsTheQuadsMenuAndAQuadHalfDrawnStaysWithItsProjector()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var map = desk.Main.Map;
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        var canvas = MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView");
        var quad = await DrawQuad(desk, window, canvas);
        Key(window, Avalonia.Input.Key.S, physical: PhysicalKey.S);
        map.Select("projector", "pj_left");
        DeskHarness.Pump();

        // A right-click inside the quad, over the wall's mesh behind it: the quad is selected, and its menu (the outliner's entries
        // for it) opens — Remove removes the quad, not the surface behind.
        var at = canvas.TranslatePoint(canvas.PointFor(new Vec2d(0.5, 0.5)), window)!.Value;
        window.MouseMove(at, RawInputModifiers.None);
        window.MouseDown(at, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Right, RawInputModifiers.None);
        DeskHarness.Pump();
        Assert.Equal(("surface", quad.Id), (map.SelectedKind, map.SelectedId));
        var items = canvas.ContextMenu!.Items.OfType<MenuItem>().Select(i => i.Tag).OfType<DeskAction>().ToList();
        Assert.Contains("REMOVE " + quad.Id, items.Select(a => a.Verb));
        Assert.All(items.Where(a => a.Verb.StartsWith("REMOVE", StringComparison.Ordinal)), a => Assert.Equal("REMOVE " + quad.Id, a.Verb));
        canvas.ContextMenu.Close();
        // The view's own entries, where nothing is under the pointer: the quad tool, Keep quad tool, On the wall, the first-light guide.
        Assert.Equal([map.QuadToolAction, map.KeepQuadToolAction, map.OnWall, map.FirstLight], map.ViewActions);
        await map.QuadToolAction.Command.ExecuteAsync();
        Assert.Equal(MapTool.Quad, map.Tool);

        // Two corners on pj_left's wall, then the view shows pj_right: the half-drawn quad stays pj_left's draft (the wall's cursor
        // off), and Enter after two more clicks on pj_right's wall draws nothing from pj_left's corners.
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        ClickWall(window, canvas, 0.2, 0.2);
        ClickWall(window, canvas, 0.8, 0.2);
        Assert.True(map.Quad.IsDrawing);
        desk.Link.ClearSent();
        map.Select("projector", "pj_right");
        DeskHarness.Pump();
        Assert.Equal("pj_right", map.ViewProjector!.Id);
        Assert.False(map.Quad.IsDrawing);
        Assert.Contains("CURSOR OFF", desk.Link.SentLines);
        Assert.Equal(2, map.Drafts.Of("pj_left")!.Count);
        ClickWall(window, canvas, 0.2, 0.8);
        ClickWall(window, canvas, 0.8, 0.8);
        Key(window, Avalonia.Input.Key.Enter, physical: PhysicalKey.Enter);
        await map.LastQuad;
        Assert.DoesNotContain(desk.Link.SentLines, l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal));
        Assert.Equal(2, map.Quad.Clicks.Count);
        // Back on pj_left, its own two corners are there again.
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        Assert.Equal(2, map.Quad.Clicks.Count);
        Assert.Equal(0.2, map.Quad.Clicks[0].X, 2);
        Assert.Equal(0.8, map.Quad.Clicks[1].X, 2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscDropsAQuadHalfDrawnAndAnAreaTooSmallIsTheNodesRefusal()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var map = desk.Main.Map;
        map.Select("projector", "pj_left");
        Key(window, Avalonia.Input.Key.Q, physical: PhysicalKey.Q);
        var canvas = MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView");
        ClickWall(window, canvas, 0.3, 0.3);
        ClickWall(window, canvas, 0.6, 0.3);
        Assert.Equal(2, map.Quad.Clicks.Count);
        Assert.Equal("Corner 3 of 4 · drag or nudge a point to move it · Esc discards.", map.ToolHint);
        // Enter with two corners draws nothing.
        Key(window, Avalonia.Input.Key.Enter, physical: PhysicalKey.Enter);
        Assert.DoesNotContain(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal));
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Escape, physical: PhysicalKey.Escape);
        Assert.False(map.Quad.IsDrawing);
        Assert.Contains("CURSOR OFF", desk.Link.SentLines);
        Assert.Equal(MapTool.Quad, map.Tool);

        // Four clicks almost on one spot: the node refuses the quad with its words, and nothing is drawn.
        foreach (var (x, y) in new[] { (0.5, 0.5), (0.501, 0.5), (0.501, 0.501), (0.5, 0.501) })
            ClickWall(window, canvas, x, y);
        Key(window, Avalonia.Input.Key.Enter, physical: PhysicalKey.Enter);
        await map.LastQuad;
        DeskHarness.Pump();
        Assert.DoesNotContain(desk.Link.CurrentProject.Stage.Surfaces, s => s.Pixel is not null);
        Assert.Contains("it needs at least 64 px²", desk.Session.LastToast!.Text, StringComparison.Ordinal);
        Assert.Null(map.SelectedQuad);
        window.Close();
    }
}
