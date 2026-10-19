using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.ViewModels;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Tests.Quad;

/// <summary>A quad's corner dragged in the projector view, on the throttle's manual clock: the press picks the quad and that corner (the
/// wall told); the moves send SURFACE CORNER … TO … RASTER, the raster point under the pointer, at most one line per 16 ms window — the
/// newest — and the release sends the last at once, unless the last window sent it already; the node keeps the burst as one undo step.
/// On an output turned a quarter, the corner lands under the pointer and the inspector reads it in the wall's pixels as the node does.</summary>
public class QuadCornerDragTests
{
    [AvaloniaFact]
    public async Task ADragSendsTheNewestCornerPerWindowAndTheReleaseCommits()
    {
        var time = new ManualTime();
        await using var desk = new DeskHarness(time: time);
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var quad = await QuadRig.DrawQuad(desk, window, canvas);
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        var steps = desk.Link.UndoSteps;
        desk.Link.ClearSent();

        // Press on TR, then three moves inside one window: nothing is sent until the window ends, then only the newest.
        var start = canvas.PointForQuadCorner(quad.Id, 1);
        var window16 = new[] { new Vec2d(0.76, 0.26), new Vec2d(0.77, 0.27), new Vec2d(0.78, 0.28) };
        var at = canvas.TranslatePoint(start, window)!.Value;
        window.MouseMove(at, Avalonia.Input.RawInputModifiers.None);
        window.MouseDown(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        DeskHarness.Pump();
        Assert.Equal(quad.Id, map.SelectedQuad!.Id);
        Assert.Equal(1, map.SelectedCorner);
        Assert.Contains($"HANDLE SELECT {quad.Id} TR", desk.Link.SentLines);
        foreach (var w in window16)
        {
            window.MouseMove(canvas.TranslatePoint(canvas.PointFor(w), window)!.Value, Avalonia.Input.RawInputModifiers.LeftMouseButton);
            DeskHarness.Pump();
        }
        var corners = desk.Link.SentLines.Where(l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal)).ToList();
        Assert.Empty(corners);
        time.Advance(TimeSpan.FromMilliseconds(16));
        DeskHarness.Pump();
        corners = [.. desk.Link.SentLines.Where(l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal))];
        var newest = RasterPx(desk, WallAt(canvas, window16[^1]));
        Assert.Equal([$"SURFACE CORNER {quad.Id} TR TO {Num(newest.X)} {Num(newest.Y)} RASTER"], corners);

        // Two more windows: one line each. The release sends the last at once.
        foreach (var w in new[] { new Vec2d(0.79, 0.29), new Vec2d(0.8, 0.3) })
        {
            window.MouseMove(canvas.TranslatePoint(canvas.PointFor(w), window)!.Value, Avalonia.Input.RawInputModifiers.LeftMouseButton);
            DeskHarness.Pump();
            time.Advance(TimeSpan.FromMilliseconds(16));
            DeskHarness.Pump();
        }
        var end = canvas.TranslatePoint(canvas.PointFor(new Vec2d(0.81, 0.31)), window)!.Value;
        window.MouseMove(end, Avalonia.Input.RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        DeskHarness.Pump();
        await desk.FlushThrottle();
        corners = [.. desk.Link.SentLines.Where(l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal))];
        Assert.Equal(4, corners.Count);
        Assert.All(corners, l => Assert.Null(DeskVerbRuleTests.Fault(l)));
        var last = RasterPx(desk, WallAt(canvas, new Vec2d(0.81, 0.31)));
        Assert.Equal($"SURFACE CORNER {quad.Id} TR TO {Num(last.X)} {Num(last.Y)} RASTER", corners[^1]);
        // The node has the corner where it was let go, and the drag was one undo step.
        var moved = desk.Link.CurrentProject.FindSurface(quad.Id)!.Pixel!.TopRight;
        Assert.Equal(last.X / 1920, moved.X, 3);
        Assert.Equal(steps + 1, desk.Link.UndoSteps);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ALetGoWhereTheLastWindowSentTheCornerSendsNothingMore()
    {
        var time = new ManualTime();
        await using var desk = new DeskHarness(time: time);
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var quad = await QuadRig.DrawQuad(desk, window, canvas);
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        desk.Link.ClearSent();
        var refusals = desk.Session.Toasts.Count(t => t.IsRefusal);

        var at = canvas.TranslatePoint(canvas.PointForQuadCorner(quad.Id, 1), window)!.Value;
        window.MouseMove(at, Avalonia.Input.RawInputModifiers.None);
        window.MouseDown(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        DeskHarness.Pump();
        var to = canvas.TranslatePoint(canvas.PointFor(new Vec2d(0.8, 0.3)), window)!.Value;
        window.MouseMove(to, Avalonia.Input.RawInputModifiers.LeftMouseButton);
        DeskHarness.Pump();
        time.Advance(TimeSpan.FromMilliseconds(16));
        DeskHarness.Pump();
        Assert.Single(desk.Link.SentLines, l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal));

        // The person stops, then lets go where the last window sent the corner: the node has it there, so nothing more goes.
        time.Advance(TimeSpan.FromMilliseconds(100));
        window.MouseUp(to, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        DeskHarness.Pump();
        await desk.FlushThrottle();
        Assert.Single(desk.Link.SentLines, l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal));
        Assert.Equal(refusals, desk.Session.Toasts.Count(t => t.IsRefusal));
        Assert.Equal(RasterPx(desk, WallAt(canvas, new Vec2d(0.8, 0.3))).X / 1920, desk.Link.CurrentProject.FindSurface(quad.Id)!.Pixel!.TopRight.X, 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnAnOutputTurnedAQuarterTheDraggedCornerLandsUnderThePointer()
    {
        var time = new ManualTime();
        await using var desk = new DeskHarness(time: time);
        Assert.True((await desk.Session.SendAsync("SET op_1 rotation 90", Ct)).Ok);
        DeskHarness.Pump();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var quad = await QuadRig.DrawQuad(desk, window, canvas);
        // The quad drawn on the turned output is where it was clicked in the view: its corner is the raster point under the click.
        var clicked = RasterPx(desk, WallAt(canvas, new Vec2d(0.75, 0.25)));
        Assert.Equal(clicked.X / 1920, desk.Link.CurrentProject.FindSurface(quad.Id)!.Pixel!.TopRight.X, 3);
        Assert.Equal(clicked.Y / 1080, desk.Link.CurrentProject.FindSurface(quad.Id)!.Pixel!.TopRight.Y, 3);
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        desk.Link.ClearSent();

        var target = new Vec2d(0.8, 0.3);
        QuadRig.Drag(window, canvas, canvas.PointForQuadCorner(quad.Id, 1), canvas.PointFor(target));
        await desk.FlushThrottle();
        var line = Assert.Single(desk.Link.SentLines, l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal));
        Assert.EndsWith(" RASTER", line, StringComparison.Ordinal);
        Assert.Null(DeskVerbRuleTests.Fault(line));
        // The corner sits under the pointer in the view.
        var released = WallAt(canvas, target);
        var corner = canvas.PointForQuadCorner(quad.Id, 1);
        var pointer = canvas.PointFor(released);
        Assert.True(Avalonia.Point.Distance(corner, pointer) < 1, $"the corner is drawn at {corner}, the pointer let go at {pointer}");
        // The inspector reads it in the wall's pixels with the output's quarter turn, as the node's wall frame does: (1 − y, x).
        map.Select("surface", quad.Id);
        DeskHarness.Pump();
        var inspector = Assert.IsType<SurfaceInspector>(map.Inspector.Current);
        var x = double.Parse(inspector.Corners[1].X, System.Globalization.CultureInfo.InvariantCulture);
        var y = double.Parse(inspector.Corners[1].Y, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal((1 - released.Y) * 1920, x, 0);
        Assert.Equal(released.X * 1080, y, 0);
        window.Close();
    }

    private static string Num(double v) => PlayMap.App.Infrastructure.VerbText.Num(v);

    /// <summary>The raster point under a point of the view, in the pixels the node reads RASTER in (op_1's warp taken back).</summary>
    private static Vec2d RasterPx(DeskHarness desk, Vec2d view)
    {
        var wall = PlayMap.App.ViewModels.Map.QuadWall.Of(desk.Link.CurrentProject, "pj_left")!;
        var r = wall.RasterAt(view)!.Value;
        return new Vec2d(Math.Round(r.X * wall.Width, 2), Math.Round(r.Y * wall.Height, 2));
    }

    /// <summary>A wall point as the view reads it back from the control's pixels (the pointer lands on a whole pixel).</summary>
    private static Vec2d WallAt(PlayMap.App.Controls.ProjectorCanvas canvas, Vec2d wall) => canvas.NormFor(canvas.PointFor(wall));
}
