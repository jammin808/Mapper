using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Tests.Quad;

/// <summary>A selected pin moves by one pixel: picked by its row's button (which leaves the keyboard where it was), each arrow pressed on
/// the window sends the line its drag sends, SET &lt;pin&gt; pixel, at its pixel ± 1 (Shift 10, Alt 0.1); its pixel typed in its row and
/// Enter sends the same line. Arrows faster than the node's project push move on from the last pixel sent, so none is lost and none is
/// refused.</summary>
public class PinNudgeTests
{
    private static Button PickOf(Window window, PinRow row) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PinPick" && ReferenceEquals(b.DataContext, row) && b.IsEffectivelyVisible);

    [AvaloniaFact]
    public async Task APinPickedInItsRowMovesByTheArrowsAndItsTypedPixel()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var inspector = Assert.IsType<ProjectorInspector>(map.Inspector.Current);
        var row = inspector.Pins[0];
        var pin = desk.Link.CurrentProject.FindProjector("pj_left")!.Pins.First(p => p.Id == row.Id);

        // The row's button picks the pin and takes no keyboard: the arrows pressed on the window are the view's.
        var pick = PickOf(window, row);
        pick.BringIntoView();
        DeskHarness.Pump();
        DeskHarness.Click(window, pick);
        Assert.Equal(row.Id, map.SelectedPinId);
        Assert.True(row.IsSelected);
        Assert.False(pick.IsFocused);

        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.Right);
        await map.LastPinNudge;
        DeskHarness.Pump();
        var right = VerbText.SetJson(row.Id, "pixel", new Vec2d(Math.Round(pin.Pixel.X + 1, 3), Math.Round(pin.Pixel.Y, 3)));
        Assert.Equal([right], desk.Verbs());
        Assert.Null(DeskVerbRuleTests.Fault(right));
        var moved = desk.Link.CurrentProject.FindProjector("pj_left")!.Pins.First(p => p.Id == row.Id);
        Assert.Equal(pin.Pixel.X + 1, moved.Pixel.X, 3);

        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.Up, Avalonia.Input.RawInputModifiers.Shift);
        await map.LastPinNudge;
        DeskHarness.Pump();
        Assert.Equal([VerbText.SetJson(row.Id, "pixel", new Vec2d(Math.Round(moved.Pixel.X, 3), Math.Round(moved.Pixel.Y - 10, 3)))], desk.Verbs());

        // The pixel typed in its row: nothing while typing, the line at Enter.
        var box = window.GetVisualDescendants().OfType<TextBox>()
            .First(t => ReferenceEquals(t.DataContext, row) && t.GetVisualAncestors().OfType<Grid>().Any(g => g.Name == "PinPixel"));
        Assert.True(box.Focus());
        box.SelectAll();
        desk.Link.ClearSent();
        window.KeyTextInput("640.5");
        DeskHarness.Pump();
        Assert.Empty(desk.Verbs());
        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, "");
        DeskHarness.Pump();
        var typed = desk.Link.CurrentProject.FindProjector("pj_left")!.Pins.First(p => p.Id == row.Id);
        Assert.Equal([VerbText.SetJson(row.Id, "pixel", new Vec2d(640.5, Math.Round(moved.Pixel.Y - 10, 3)))], desk.Verbs());
        Assert.Equal(640.5, typed.Pixel.X, 3);

        // Another tool lets the pin go: the arrows are the quad's again (none selected: nothing sent).
        Assert.True(canvas.Focus());
        QuadRig.Key(window, Avalonia.Input.Key.W);
        Assert.Null(map.SelectedPinId);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ArrowsFasterThanTheProjectsPushMoveOnFromTheLastPixelSent()
    {
        await using var desk = new DeskHarness();
        var (window, _, map) = QuadRig.OpenMap(desk);
        var inspector = Assert.IsType<ProjectorInspector>(map.Inspector.Current);
        var row = inspector.Pins[0];
        var origin = desk.Link.CurrentProject.FindProjector("pj_left")!.Pins.First(p => p.Id == row.Id).Pixel;
        row.SelectCommand.Execute(null);
        DeskHarness.Pump();
        var refusals = desk.Session.Toasts.Count(t => t.IsRefusal);

        // The node applies each arrow at once but pushes its project late: three quick arrows are +1, +2, +3.
        desk.Link.HoldsProject = true;
        desk.Link.ClearSent();
        for (var i = 0; i < 3; i++)
        {
            QuadRig.Key(window, Avalonia.Input.Key.Right);
            await map.LastPinNudge;
            DeskHarness.Pump();
        }
        Assert.Equal([.. Enumerable.Range(1, 3).Select(k => VerbText.SetJson(row.Id, "pixel", new Vec2d(Math.Round(origin.X + k, 3), Math.Round(origin.Y, 3))))],
            desk.Verbs());
        Assert.Equal(refusals, desk.Session.Toasts.Count(t => t.IsRefusal));
        Assert.Equal(origin.X + 3, desk.Link.CurrentProject.FindProjector("pj_left")!.Pins.First(p => p.Id == row.Id).Pixel.X, 3);

        // The late push carries +3: the next arrow moves on from the mirror, +4.
        desk.Link.HoldsProject = false;
        desk.Link.PushProject();
        DeskHarness.Pump();
        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.Right);
        await map.LastPinNudge;
        DeskHarness.Pump();
        Assert.Equal([VerbText.SetJson(row.Id, "pixel", new Vec2d(Math.Round(origin.X + 4, 3), Math.Round(origin.Y, 3)))], desk.Verbs());

        // Another edit moves the pin elsewhere: the mirror wins, and the next arrow moves on from where the pin is now.
        Assert.True((await desk.Session.SendAsync(VerbText.SetJson(row.Id, "pixel", new Vec2d(100, 200)), Ct)).Ok);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.Down);
        await map.LastPinNudge;
        DeskHarness.Pump();
        Assert.Equal([VerbText.SetJson(row.Id, "pixel", new Vec2d(100, 201))], desk.Verbs());
        window.Close();
    }
}
