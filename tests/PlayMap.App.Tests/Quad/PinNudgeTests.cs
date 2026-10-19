using Avalonia.Headless.XUnit;
using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Tests.Quad;

/// <summary>A selected pin moves by one pixel: picked in its row (or with the Pin tool), each arrow sends the line its drag sends, SET
/// &lt;pin&gt; pixel, at its pixel ± 1 (Shift 10, Alt 0.1); its typed pixel sends the same line.</summary>
public class PinNudgeTests
{
    [AvaloniaFact]
    public async Task ASelectedPinsArrowsAndTypedPixelSendSetPinPixel()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, map) = QuadRig.OpenMap(desk);
        var inspector = Assert.IsType<ProjectorInspector>(map.Inspector.Current);
        var row = inspector.Pins[0];
        var pin = desk.Link.CurrentProject.FindProjector("pj_left")!.Pins.First(p => p.Id == row.Id);
        row.SelectCommand.Execute(null);
        DeskHarness.Pump();
        Assert.Equal(row.Id, map.SelectedPinId);
        Assert.True(row.IsSelected);
        Assert.True(canvas.Focus());

        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.Right);
        DeskHarness.Pump();
        var right = VerbText.SetJson(row.Id, "pixel", new Vec2d(Math.Round(pin.Pixel.X + 1, 3), Math.Round(pin.Pixel.Y, 3)));
        Assert.Equal([right], desk.Verbs());
        Assert.Null(DeskVerbRuleTests.Fault(right));
        var moved = desk.Link.CurrentProject.FindProjector("pj_left")!.Pins.First(p => p.Id == row.Id);
        Assert.Equal(pin.Pixel.X + 1, moved.Pixel.X, 3);

        desk.Link.ClearSent();
        QuadRig.Key(window, Avalonia.Input.Key.Up, Avalonia.Input.RawInputModifiers.Shift);
        DeskHarness.Pump();
        Assert.Equal([VerbText.SetJson(row.Id, "pixel", new Vec2d(Math.Round(moved.Pixel.X, 3), Math.Round(moved.Pixel.Y - 10, 3)))], desk.Verbs());

        // The pixel typed in its row.
        desk.Link.ClearSent();
        row.PixelX = "640.5";
        DeskHarness.Pump();
        var typed = desk.Link.CurrentProject.FindProjector("pj_left")!.Pins.First(p => p.Id == row.Id);
        Assert.Equal([VerbText.SetJson(row.Id, "pixel", new Vec2d(640.5, Math.Round(moved.Pixel.Y - 10, 3)))], desk.Verbs());
        Assert.Equal(640.5, typed.Pixel.X, 3);

        // Another tool lets the pin go: the arrows are the quad's again (none selected: nothing sent).
        QuadRig.Key(window, Avalonia.Input.Key.W);
        Assert.Null(map.SelectedPinId);
        window.Close();
    }
}
