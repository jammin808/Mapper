using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Tests.Quad;

/// <summary>A quad's corner dragged in the projector view, on the throttle's manual clock: the press picks the quad and that corner (the
/// wall told); the moves send SURFACE CORNER … TO … PX in the output's wall pixels, at most one line per 16 ms window — the newest — and
/// the release sends the last at once; the node keeps the burst as one undo step.</summary>
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
        var newest = QuadRig.Px(WallAt(canvas, window16[^1]));
        Assert.Equal([$"SURFACE CORNER {quad.Id} TR TO {Num(newest.X)} {Num(newest.Y)} PX"], corners);

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
        var last = QuadRig.Px(WallAt(canvas, new Vec2d(0.81, 0.31)));
        Assert.Equal($"SURFACE CORNER {quad.Id} TR TO {Num(last.X)} {Num(last.Y)} PX", corners[^1]);
        // The node has the corner where it was let go, and the drag was one undo step.
        var moved = desk.Link.CurrentProject.FindSurface(quad.Id)!.Pixel!.TopRight;
        Assert.Equal(last.X / 1920, moved.X, 3);
        Assert.Equal(steps + 1, desk.Link.UndoSteps);
        window.Close();
    }

    private static string Num(double v) => PlayMap.App.Infrastructure.VerbText.Num(v);

    /// <summary>A wall point as the view reads it back from the control's pixels (the pointer lands on a whole pixel).</summary>
    private static Vec2d WallAt(PlayMap.App.Controls.ProjectorCanvas canvas, Vec2d wall) => canvas.NormFor(canvas.PointFor(wall));
}
