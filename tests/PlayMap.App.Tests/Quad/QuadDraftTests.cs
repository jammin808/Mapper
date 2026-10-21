using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Tests.Quad;

/// <summary>The four-click quad stays for a person to come back to: two clicks are a draft of their projector, kept with its chip
/// ("Quad 2/4 · Resume · Discard", in the view's corner and under Surfaces) through a tool change, another projector, PLAY and back, a
/// control in the bar and an entry of a right-click menu, the first-light guide closed and a desk restart (read back from desk.json); its
/// placed points drag and nudge on the desk alone; nothing that changes the project is sent before Enter, and the wall's rubber band
/// (CURSOR … TRAIL) only while the draft is shown; Enter sends one SURFACE QUAD with the four as they stand; Discard and Esc drop it; a
/// draft whose projector is gone is dropped with words.</summary>
public class QuadDraftTests
{
    private static void AssertDraft(MapViewModel map, int points, string projector = "pj_left")
    {
        Assert.Equal(points, map.Drafts.Of(projector)?.Count ?? 0);
        var chip = Assert.Single(map.DraftChips, c => c.Projector == projector);
        Assert.Equal($"Quad {points}/4", chip.Text);
    }

    [AvaloniaFact]
    public async Task TwoClicksStayThroughEverythingButEnterOrADiscard()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-quad-");
        var path = Path.Combine(dir.FullName, "desk.json");
        try
        {
            await using (var desk = new DeskHarness(prefs: DeskPrefs.Load(path)))
            {
                var (window, canvas, map) = QuadRig.OpenMap(desk);
                QuadRig.Key(window, Avalonia.Input.Key.Q);
                QuadRig.ClickWall(window, canvas, 0.25, 0.25);
                QuadRig.ClickWall(window, canvas, 0.75, 0.25);
                await desk.FlushThrottle();
                AssertDraft(map, 2);
                Assert.True(map.IsDraftShown);
                Assert.Same(map.ViewDraftChip, map.DraftChips[0]);
                Assert.True(MainWindowTests.Find<ContentControl>(window, "ViewDraftChip").IsVisible);
                Assert.Equal(1, window.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "OutlinerDrafts" && i.IsVisible).ItemCount);
                Assert.Contains(desk.Link.SentLines, l => l.StartsWith("CURSOR op_1 ", StringComparison.Ordinal) && l.Contains(" TRAIL ", StringComparison.Ordinal));

                // A tool change: the draft stays, its rubber band leaves the wall; Q takes it up again and the band is back.
                desk.Link.ClearSent();
                QuadRig.Key(window, Avalonia.Input.Key.S);
                AssertDraft(map, 2);
                Assert.False(map.IsDraftShown);
                Assert.Equal(["CURSOR OFF"], desk.Link.SentLines.Where(l => l.StartsWith("CURSOR", StringComparison.Ordinal)));
                desk.Link.ClearSent();
                QuadRig.Key(window, Avalonia.Input.Key.Q);
                await desk.FlushThrottle();
                Assert.True(map.IsDraftShown);
                Assert.Equal(2, map.Quad.Clicks.Count);
                Assert.StartsWith("CURSOR op_1 0.75 0.25 TRAIL 0.25 0.25", Assert.Single(desk.Link.SentLines, l => l.StartsWith("CURSOR", StringComparison.Ordinal)), StringComparison.Ordinal);

                // Another projector, and back.
                map.Select("projector", "pj_right");
                DeskHarness.Pump();
                Assert.False(map.Quad.IsDrawing);
                Assert.Null(map.ViewDraftChip);
                AssertDraft(map, 2);
                map.Select("projector", "pj_left");
                DeskHarness.Pump();
                Assert.Equal(2, map.Quad.Clicks.Count);

                // PLAY and back.
                desk.Main.Workspace = Workspace.Play;
                DeskHarness.Pump();
                AssertDraft(map, 2);
                desk.Main.Workspace = Workspace.Map;
                DeskHarness.Pump();
                Assert.Equal(2, map.Quad.Clicks.Count);

                // A control of the bar clicked (Blackout, on and off) and an entry chosen from a right-click menu (the projector's own
                // Select, in the menu its outliner row opens).
                var blackout = MainWindowTests.Find<Button>(window, "BlackoutButton");
                DeskHarness.Click(window, blackout);
                await DeskHarness.Drain([desk.Main.Blackout]);
                DeskHarness.Click(window, blackout);
                await DeskHarness.Drain([desk.Main.Blackout]);
                var row = window.GetVisualDescendants().OfType<Border>()
                    .First(b => b.DataContext is OutlinerNode { Id: "pj_left" } && b.ContextMenu is not null && b.IsEffectivelyVisible);
                DeskHarness.Click(window, row, Avalonia.Input.MouseButton.Right);
                var menu = row.ContextMenu!;
                Assert.True(menu.IsOpen);
                var select = menu.Items.OfType<MenuItem>().First(i => i.Tag is DeskAction { Id: "select" });
                select.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                DeskHarness.Pump();
                menu.Close();
                DeskHarness.Pump();
                Assert.Equal(("projector", "pj_left"), (map.SelectedKind, map.SelectedId));
                AssertDraft(map, 2);
                Assert.Equal(2, map.Quad.Clicks.Count);

                // The first-light guide closed.
                map.Guide.Dismiss.Execute(null);
                DeskHarness.Pump();
                AssertDraft(map, 2);

                // Nothing so far changed the project; every CURSOR went while the draft was shown, or took it off.
                Assert.DoesNotContain(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal));
                Assert.DoesNotContain(desk.Verbs(), l => QuadRig.Changes(l) && !l.StartsWith("BLACKOUT", StringComparison.Ordinal));
                Assert.Empty(desk.Link.CurrentProject.Stage.Surfaces.Where(s => s.Pixel is not null));
                window.Close();
            }

            // A desk restart: the draft is read back from desk.json, and Q on its projector takes it up.
            var kept = DeskPrefs.Load(path).QuadDraft("demo0001", "pj_left");
            Assert.Equal(2, kept!.Count);
            await using (var again = new DeskHarness(prefs: DeskPrefs.Load(path)))
            {
                var (window, canvas, map) = QuadRig.OpenMap(again);
                AssertDraft(map, 2);
                QuadRig.Key(window, Avalonia.Input.Key.Q);
                Assert.True(map.IsDraftShown);
                Assert.Equal(0.25, map.Quad.Clicks[0].X, 2);
                Assert.Equal(0.75, map.Quad.Clicks[1].X, 2);

                // Two more corners, and Enter: one SURFACE QUAD with all four; the draft and its chip go.
                QuadRig.ClickWall(window, canvas, 0.75, 0.75);
                QuadRig.ClickWall(window, canvas, 0.25, 0.75);
                again.Link.ClearSent();
                QuadRig.Key(window, Avalonia.Input.Key.Enter);
                await map.LastQuad;
                DeskHarness.Pump();
                Assert.Equal("SURFACE QUAD pj_left 480 270 1440 270 1440 810 480 810 PX", Assert.Single(again.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal)));
                Assert.Empty(map.DraftChips);
                Assert.Null(DeskPrefs.Load(path).QuadDraft("demo0001", "pj_left"));
                window.Close();
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ThePlacedPointsDragAndNudgeOnTheDeskAndEnterSendsThemAsTheyStand()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        QuadRig.Key(window, Avalonia.Input.Key.Q);
        foreach (var (x, y) in new[] { (0.25, 0.25), (0.75, 0.25), (0.75, 0.75), (0.25, 0.75) })
            QuadRig.ClickWall(window, canvas, x, y);
        await desk.FlushThrottle();
        desk.Link.ClearSent();

        // The first point dragged a tenth of the picture right.
        QuadRig.Drag(window, canvas, canvas.PointForDraft(0), canvas.PointFor(new Vec2d(0.30, 0.25)), canvas.PointFor(new Vec2d(0.35, 0.25)));
        Assert.Equal(0.35, map.Quad.Clicks[0].X, 3);
        Assert.Equal(0, map.Quad.Selected);
        Assert.Equal(4, map.Quad.Clicks.Count);
        // The arrows nudge the selected point by wall pixels (1, Shift 10, Alt 0.1); Tab steps to the next point.
        var first = map.Quad.Clicks[0];
        var second = map.Quad.Clicks[1];
        QuadRig.Key(window, Avalonia.Input.Key.Right);
        Assert.Equal(first.X + (1 / 1920.0), map.Quad.Clicks[0].X, 9);
        QuadRig.Key(window, Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.Shift);
        Assert.Equal(first.Y + (10 / 1080.0), map.Quad.Clicks[0].Y, 9);
        QuadRig.Key(window, Avalonia.Input.Key.Tab);
        Assert.Equal(1, map.Quad.Selected);
        QuadRig.Key(window, Avalonia.Input.Key.Left, Avalonia.Input.RawInputModifiers.Alt);
        Assert.Equal(second.X - (0.1 / 1920), map.Quad.Clicks[1].X, 9);
        await desk.FlushThrottle();
        // On the desk alone: nothing but the wall's cursor went to the node.
        Assert.All(desk.Verbs(), l => Assert.StartsWith("CURSOR op_1 ", l, StringComparison.Ordinal));
        Assert.Equal(map.Quad.Clicks, map.Drafts.Of("pj_left"));

        // Enter: the four as they stand, in op_1's wall pixels.
        var standing = map.Quad.Clicks.ToArray();
        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.Enter);
        await map.LastQuad;
        DeskHarness.Pump();
        var expected = "SURFACE QUAD pj_left " + string.Join(' ', standing.Select(c => VerbText.Num(Math.Round(c.X * 1920, 2)) + " " + VerbText.Num(Math.Round(c.Y * 1080, 2)))) + " PX";
        Assert.Equal(expected, Assert.Single(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal)));
        Assert.Equal(Math.Round(standing[0].X * 1920, 2), Math.Round(first.X * 1920, 2) + 1, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task APlacedPointsDragIsWrittenToDeskJsonOnceWhenItIsLetGo()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-quad-");
        var path = Path.Combine(dir.FullName, "desk.json");
        try
        {
            var prefs = DeskPrefs.Load(path);
            await using var desk = new DeskHarness(prefs: prefs);
            var (window, canvas, map) = QuadRig.OpenMap(desk);
            QuadRig.Key(window, Avalonia.Input.Key.Q);
            QuadRig.ClickWall(window, canvas, 0.25, 0.25);
            QuadRig.ClickWall(window, canvas, 0.75, 0.25);
            var writes = prefs.Writes;

            // Three moves of the first point, then the release: desk.json is written once, with the point where it was let go.
            QuadRig.Drag(window, canvas, canvas.PointForDraft(0), canvas.PointFor(new Vec2d(0.28, 0.25)), canvas.PointFor(new Vec2d(0.31, 0.25)),
                canvas.PointFor(new Vec2d(0.34, 0.25)));
            Assert.Equal(writes + 1, prefs.Writes);
            Assert.Equal(map.Quad.Clicks[0].X, DeskPrefs.Load(path).QuadDraft("demo0001", "pj_left")![0].X, 6);

            // Arrows on the draft move it on the desk alone; the draft is written once the person is elsewhere (a tool change).
            writes = prefs.Writes;
            QuadRig.Key(window, Avalonia.Input.Key.Right);
            QuadRig.Key(window, Avalonia.Input.Key.Right);
            Assert.Equal(writes, prefs.Writes);
            var nudged = map.Quad.Clicks[0];
            QuadRig.Key(window, Avalonia.Input.Key.S);
            Assert.Equal(writes + 1, prefs.Writes);
            Assert.Equal(nudged.X, DeskPrefs.Load(path).QuadDraft("demo0001", "pj_left")![0].X, 6);
            window.Close();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AnotherNodeWithTheSameProjectorShowsItsOwnDraftsNeverTheLastNodes()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-quad-");
        var path = Path.Combine(dir.FullName, "desk.json");
        try
        {
            await using var desk = new DeskHarness(prefs: DeskPrefs.Load(path));
            var (window, canvas, map) = QuadRig.OpenMap(desk);
            QuadRig.Key(window, Avalonia.Input.Key.Q);
            QuadRig.ClickWall(window, canvas, 0.25, 0.25);
            QuadRig.ClickWall(window, canvas, 0.75, 0.25);
            AssertDraft(map, 2);

            // The desk attaches to node B, whose show has a pj_left too: B's view shows none of A's points, and B has no chip.
            await using var b = new PlayMap.App.Link.FakeEngineLink(clock: desk.Clock) { NodeId = "demo0002" };
            desk.Main.Attach(b, "Node B");
            DeskHarness.Pump();
            Assert.Equal("demo0002", desk.Session.NodeId);
            Assert.Equal(MapTool.Quad, map.Tool);
            Assert.False(map.Quad.IsDrawing);
            Assert.False(map.ToolIsOnView);
            Assert.Empty(map.DraftChips);
            b.ClearSent();
            Key(window, Avalonia.Input.Key.Enter);
            Assert.DoesNotContain(b.SentLines, l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal));

            // A click on B's wall is B's first point; A's two are A's still, and nothing was dropped.
            QuadRig.ClickWall(window, canvas, 0.4, 0.4);
            AssertDraft(map, 1);
            var kept = DeskPrefs.Load(path);
            Assert.Equal(2, kept.QuadDraft("demo0001", "pj_left")!.Count);
            Assert.Single(kept.QuadDraft("demo0002", "pj_left")!);
            Assert.DoesNotContain(desk.Session.Toasts, t => t.Text.Contains("dropped", StringComparison.Ordinal));
            window.Close();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task LightingANewWallLeavesItsViewEmptyAndTheDraftWithItsOwnProjector()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        QuadRig.Key(window, Avalonia.Input.Key.Q);
        QuadRig.ClickWall(window, canvas, 0.25, 0.25);
        QuadRig.ClickWall(window, canvas, 0.75, 0.25);
        AssertDraft(map, 2);
        map.Guide.Reopen();
        await map.Guide.Reading;
        DeskHarness.Pump();
        var light = map.Guide.Lights.Single(a => a.Verb == "LIGHT WALL DISPLAY1");

        // LIGHT WALL of the desk's own screen answers before the project that names the new projector arrives.
        desk.Link.HoldsProject = true;
        await light.Command.ExecuteAsync();
        await map.Guide.Lighting;
        DeskHarness.Pump();
        Assert.Equal(MapTool.Quad, map.Tool);
        Assert.False(map.ToolIsOnView);
        Assert.False(map.IsDraftShown);
        AssertDraft(map, 2);

        desk.Link.HoldsProject = false;
        desk.Link.PushProject();
        DeskHarness.Pump();
        var lit = map.ViewProjector!.Id;
        Assert.NotEqual("pj_left", lit);
        Assert.Null(map.Drafts.Of(lit));
        Assert.False(map.Quad.IsDrawing);
        QuadRig.ClickWall(window, canvas, 0.4, 0.4);
        Assert.Single(map.Drafts.Of(lit)!);
        Assert.Equal("Quad 1/4", map.ViewDraftChip!.Text);
        AssertDraft(map, 2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task APushBetweenEnterAndItsReplyTakesNothingUpAgain()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-quad-");
        var path = Path.Combine(dir.FullName, "desk.json");
        try
        {
            await using var desk = new DeskHarness(prefs: DeskPrefs.Load(path));
            var (window, canvas, map) = QuadRig.OpenMap(desk);
            QuadRig.Key(window, Avalonia.Input.Key.Q);
            foreach (var (x, y) in new[] { (0.25, 0.25), (0.75, 0.25), (0.75, 0.75), (0.25, 0.75) })
                QuadRig.ClickWall(window, canvas, x, y);
            var reply = new TaskCompletionSource();
            desk.Link.ReplyAfter = line => line.StartsWith("SURFACE QUAD", StringComparison.Ordinal) ? reply.Task : Task.CompletedTask;
            desk.Link.ClearSent();

            // Enter: the node draws the quad and pushes its project before the reply comes back.
            QuadRig.Key(window, Avalonia.Input.Key.Enter);
            DeskHarness.Pump();
            Assert.Empty(map.DraftChips);
            Assert.False(map.Quad.IsDrawing);
            reply.SetResult();
            await map.LastQuad;
            DeskHarness.Pump();
            desk.Link.ReplyAfter = null;

            // No chip came back, desk.json holds no draft, and Q then Enter sends no second quad.
            Assert.Empty(map.DraftChips);
            Assert.Null(DeskPrefs.Load(path).QuadDraft("demo0001", "pj_left"));
            QuadRig.Key(window, Avalonia.Input.Key.Q);
            QuadRig.Key(window, Avalonia.Input.Key.Enter);
            await map.LastQuad;
            DeskHarness.Pump();
            Assert.Single(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal));
            window.Close();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task TheBandIsOnTheWallOnlyWhileTheProjectorViewShowsTheDraft()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        QuadRig.Key(window, Avalonia.Input.Key.Q);
        foreach (var (x, y) in new[] { (0.25, 0.25), (0.75, 0.25), (0.75, 0.75), (0.25, 0.75) })
            QuadRig.ClickWall(window, canvas, x, y);
        await desk.FlushThrottle();

        // PLAY and back: the band leaves the wall and is back on return.
        desk.Main.Workspace = Workspace.Play;
        DeskHarness.Pump();
        desk.Link.ClearSent();
        desk.Main.Workspace = Workspace.Map;
        DeskHarness.Pump();
        await desk.FlushThrottle();
        Assert.True(map.IsDraftShown);
        Assert.Contains(desk.Link.SentLines, l => l.StartsWith("CURSOR op_1 ", StringComparison.Ordinal) && l.Contains(" TRAIL ", StringComparison.Ordinal));

        // V to the 3D view: the band goes, nobody sees the draft, and Enter, the arrows and Tab do nothing to it.
        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.V);
        await desk.FlushThrottle();
        Assert.False(map.IsProjectorView);
        Assert.False(map.IsDraftShown);
        Assert.Equal(["CURSOR OFF"], desk.Link.SentLines.Where(l => l.StartsWith("CURSOR", StringComparison.Ordinal)));
        var standing = map.Drafts.Of("pj_left")!.ToArray();
        QuadRig.Key(window, Avalonia.Input.Key.Enter);
        QuadRig.Key(window, Avalonia.Input.Key.Right);
        QuadRig.Key(window, Avalonia.Input.Key.Tab);
        await map.LastQuad;
        await desk.FlushThrottle();
        Assert.DoesNotContain(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal) || l.StartsWith("CURSOR op_1", StringComparison.Ordinal));
        Assert.Equal(standing, map.Drafts.Of("pj_left")!);

        // V back: the band is on the wall again, and Enter draws the quad.
        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.V);
        await desk.FlushThrottle();
        Assert.True(map.IsDraftShown);
        Assert.Contains(desk.Link.SentLines, l => l.StartsWith("CURSOR op_1 ", StringComparison.Ordinal) && l.Contains(" TRAIL ", StringComparison.Ordinal));
        QuadRig.Key(window, Avalonia.Input.Key.Enter);
        await map.LastQuad;
        DeskHarness.Pump();
        Assert.Single(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal));
        window.Close();
    }

    private static void Key(Window window, Avalonia.Input.Key key) => QuadRig.Key(window, key);

    [AvaloniaFact]
    public async Task DiscardAndEscDropTheDraftAndAGoneProjectorsDraftIsDroppedWithWords()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        QuadRig.Key(window, Avalonia.Input.Key.Q);
        QuadRig.ClickWall(window, canvas, 0.3, 0.3);
        desk.Link.ClearSent();
        // The chip's Discard.
        await map.DraftChips.Single().Discard.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Empty(map.DraftChips);
        Assert.False(map.Quad.IsDrawing);
        Assert.Contains("CURSOR OFF", desk.Link.SentLines);

        // Esc while the view has the keyboard and shows the draft.
        QuadRig.ClickWall(window, canvas, 0.3, 0.3);
        Assert.True(map.ViewHasKeyboard);
        QuadRig.Key(window, Avalonia.Input.Key.Escape);
        Assert.Empty(map.DraftChips);

        // A draft on pj_right, then pj_right removed: the draft goes, with words.
        map.Select("projector", "pj_right");
        DeskHarness.Pump();
        QuadRig.ClickWall(window, canvas, 0.4, 0.4);
        Assert.Single(map.Drafts.Of("pj_right")!);
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        Assert.True((await desk.Session.SendAsync("REMOVE pj_right", Ct)).Ok);
        DeskHarness.Pump();
        Assert.Null(map.Drafts.Of("pj_right"));
        Assert.Empty(map.DraftChips);
        Assert.Contains(desk.Session.Toasts, t => t.Text == "The unfinished quad on pj_right was dropped: the project has no such projector now");
        window.Close();
    }
}
