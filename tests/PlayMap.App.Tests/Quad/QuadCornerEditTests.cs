using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Tests.Quad;

/// <summary>A quad's inspector shows its four corners as x, y in the wall's pixels and none of the mesh fields a quad does not use; a
/// typed corner sends SURFACE CORNER &lt;sf&gt; &lt;c&gt; TO &lt;x&gt; &lt;y&gt; PX when Enter is pressed in it (never on each keystroke), and an
/// arrow sends BY 1 0 PX.</summary>
public class QuadCornerEditTests
{
    [AvaloniaFact]
    public async Task TheInspectorTypesTheCornersInWallPixelsAndTheArrowsNudgeThem()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var quad = await QuadRig.DrawQuad(desk, window, canvas);
        var inspector = Assert.IsType<SurfaceInspector>(map.Inspector.Current);
        Assert.True(inspector.IsQuad);
        Assert.Equal("x, y in wall pixels", inspector.CornerUnits);
        Assert.StartsWith("Quad on Left", inspector.Subtitle, StringComparison.Ordinal);
        Assert.Equal(["TL", "TR", "BR", "BL"], inspector.Corners.Select(c => c.Name));
        Assert.Equal(("480", "270"), (inspector.Corners[0].X, inspector.Corners[0].Y));
        Assert.Equal(("1440", "810"), (inspector.Corners[2].X, inspector.Corners[2].Y));
        // The mesh fields are not shown for a quad; its corners are.
        var view = window.GetVisualDescendants().OfType<Views.InspectorView>().First(v => v.IsEffectivelyVisible);
        Assert.True(view.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "QuadCorners").IsEffectivelyVisible);
        Assert.False(view.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "MeshFields").IsVisible);

        // TL's x typed: nothing while typing, one line at Enter.
        var box = view.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("corner") && t.DataContext == inspector.Corners[0]);
        Assert.True(box.Focus());
        box.SelectAll();
        desk.Link.ClearSent();
        window.KeyTextInput("500");
        DeskHarness.Pump();
        Assert.DoesNotContain(desk.Verbs(), l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal));
        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, "");
        DeskHarness.Pump();
        Assert.Equal([$"SURFACE CORNER {quad.Id} TL TO 500 270 PX"], desk.Verbs().Where(l => l.StartsWith("SURFACE CORNER", StringComparison.Ordinal)));
        Assert.Null(DeskVerbRuleTests.Fault($"SURFACE CORNER {quad.Id} TL TO 500 270 PX"));
        // The node put the corner where it was typed, in the wall's pixels (through op_1's warp).
        var wall = PlayMap.App.ViewModels.Map.QuadWall.Of(desk.Link.CurrentProject, "pj_left")!;
        var at = wall.Pixels(desk.Link.CurrentProject.FindSurface(quad.Id)!.Pixel!.TopLeft);
        Assert.Equal(500, at.X, 1);
        Assert.Equal(270, at.Y, 1);

        // The view has the keyboard again: an arrow nudges the selected corner by one wall pixel.
        Assert.True(canvas.Focus());
        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.Right);
        Assert.Equal([$"SURFACE CORNER {quad.Id} TL BY 1 0 PX"], desk.Verbs());
        window.Close();
    }
}
