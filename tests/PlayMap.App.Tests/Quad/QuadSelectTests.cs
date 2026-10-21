using Avalonia.Headless.XUnit;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Tests.Quad;

/// <summary>After Enter the tool is Select and the new quad selected; selecting the quad again restores the corner last selected on it
/// and tells the wall (HANDLE SELECT), selecting anything else tells it the quad's selection went (HANDLE SELECT NONE); picking a quad
/// in the outliner shows the quad's projector; Keep quad tool keeps the quad tool armed after Enter.</summary>
public class QuadSelectTests
{
    [AvaloniaFact]
    public async Task EnterLeavesSelectAndReselectRestoresTheCornerAndTellsTheWall()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var quad = await QuadRig.DrawQuad(desk, window, canvas);
        Assert.Equal(MapTool.Select, map.Tool);
        Assert.Equal(quad.Id, map.SelectedQuad!.Id);
        Assert.Contains($"HANDLE SELECT {quad.Id} TL", desk.Link.SentLines);

        // Tab twice: BR is the corner; the projector selected: the wall is told the quad's handle went.
        QuadRig.Key(window, Avalonia.Input.Key.Tab);
        QuadRig.Key(window, Avalonia.Input.Key.Tab);
        Assert.Equal(2, map.SelectedCorner);
        desk.Link.ClearSent();
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        Assert.Equal(["HANDLE SELECT NONE"], desk.Link.SentLines.Where(l => l.StartsWith("HANDLE", StringComparison.Ordinal)));

        // Reselected from the outliner: BR again, and the wall shows it.
        desk.Link.ClearSent();
        map.Outliner[0].Nodes.Single(n => n.Id == quad.Id).SelectCommand.Execute(null);
        DeskHarness.Pump();
        Assert.Equal(2, map.SelectedCorner);
        Assert.Equal([$"HANDLE SELECT {quad.Id} BR"], desk.Link.SentLines.Where(l => l.StartsWith("HANDLE", StringComparison.Ordinal)));
        // Deselected (Esc's selection rung): NONE once.
        desk.Link.ClearSent();
        map.Select("", null);
        map.Select("", null);
        DeskHarness.Pump();
        Assert.Equal(["HANDLE SELECT NONE"], desk.Link.SentLines.Where(l => l.StartsWith("HANDLE", StringComparison.Ordinal)));
        Assert.All(desk.Link.SentLines.Where(l => l.StartsWith("HANDLE", StringComparison.Ordinal)), l => Assert.Null(DeskVerbRuleTests.Fault(l)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ArmedTheWallIsToldNothingAndOnDisarmItIsToldTheSelectedHandle()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var quad = await QuadRig.DrawQuad(desk, window, canvas);
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        Assert.True((await desk.Session.SendAsync("SHOW ARM", Ct)).Ok);
        DeskHarness.Pump();
        Assert.NotNull(desk.Session.GateWords($"HANDLE SELECT {quad.Id} TL"));
        var refusals = desk.Session.Toasts.Count(t => t.IsRefusal);
        desk.Link.ClearSent();

        // Armed (HANDLE SELECT is setup): selecting and deselecting the quad tells the wall nothing and says nothing.
        var node = map.Outliner[0].Nodes.Single(n => n.Id == quad.Id);
        node.SelectCommand.Execute(null);
        DeskHarness.Pump();
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        node.SelectCommand.Execute(null);
        DeskHarness.Pump();
        Assert.DoesNotContain(desk.Link.SentLines, l => l.StartsWith("HANDLE", StringComparison.Ordinal));
        Assert.Equal(refusals, desk.Session.Toasts.Count(t => t.IsRefusal));

        // Disarmed: the wall is told the handle the desk shows now; reselecting tells it nothing more (it shows it already).
        Assert.True((await desk.Session.SendAsync("SHOW DISARM", Ct)).Ok);
        DeskHarness.Pump();
        Assert.Equal([$"HANDLE SELECT {quad.Id} TL"], desk.Link.SentLines.Where(l => l.StartsWith("HANDLE", StringComparison.Ordinal)));
        desk.Link.ClearSent();
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        node.SelectCommand.Execute(null);
        DeskHarness.Pump();
        Assert.Equal(["HANDLE SELECT NONE", $"HANDLE SELECT {quad.Id} TL"], desk.Link.SentLines.Where(l => l.StartsWith("HANDLE", StringComparison.Ordinal)));

        // Another client moves the wall's handle: reselecting the quad tells the wall again.
        Assert.True((await desk.Session.SendAsync($"HANDLE SELECT {quad.Id} BR", Ct)).Ok);
        DeskHarness.Pump();
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        desk.Link.ClearSent();
        node.SelectCommand.Execute(null);
        DeskHarness.Pump();
        Assert.Contains($"HANDLE SELECT {quad.Id} TL", desk.Link.SentLines);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ANewLinkIsToldTheSelectedQuadsHandleAgain()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var quad = await QuadRig.DrawQuad(desk, window, canvas);
        Assert.Equal(quad.Id, map.SelectedQuad!.Id);

        // The node again over a new link (it started again: its wall shows no handle): the selected quad's handle is told again.
        await using var again = new PlayMap.App.Link.FakeEngineLink(desk.Link.CurrentProject, desk.Clock);
        desk.Main.Attach(again, "Demo node");
        DeskHarness.Pump();
        Assert.Equal(quad.Id, map.SelectedQuad?.Id);
        Assert.Equal($"HANDLE SELECT {quad.Id} TL", Assert.Single(again.SentLines, l => l.StartsWith("HANDLE", StringComparison.Ordinal)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnOutlinerPickShowsTheQuadsProjectorAndKeepQuadToolDrawsSeveral()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk, "pj_right");
        map.KeepQuadTool = true;
        var first = await QuadRig.DrawQuad(desk, window, canvas);
        Assert.Equal(MapTool.Quad, map.Tool);
        var second = await QuadRig.DrawQuad(desk, window, canvas, inset: 0.35);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(MapTool.Quad, map.Tool);
        map.KeepQuadTool = false;
        QuadRig.Key(window, Avalonia.Input.Key.S);

        // The view on pj_left and the 3D view; a pick of pj_right's quad in the outliner shows pj_right's projector view.
        map.Select("projector", "pj_left");
        map.IsProjectorView = false;
        DeskHarness.Pump();
        map.Outliner[0].Nodes.Single(n => n.Id == first.Id).SelectCommand.Execute(null);
        DeskHarness.Pump();
        Assert.Equal("pj_right", map.ViewProjector!.Id);
        Assert.True(map.IsProjectorView);
        Assert.Equal(first.Id, map.SelectedQuad!.Id);
        window.Close();
    }
}
