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
