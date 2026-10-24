using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PlayMap.App.Tests.Quad;

namespace PlayMap.App.Tests.Ux;

/// <summary>A field keeps the desk's Ctrl+Alt letters: Windows reports AltGr as Ctrl+Alt, so a person typing µ (AltGr+M), ż (AltGr+Z) or
/// đ (AltGr+D) in an inspector field types the character, and neither Map anything, the Oops history nor the look moves. Off a field
/// the same keys are the desk's.</summary>
public class TypingKeysTests
{
    [AvaloniaFact]
    public async Task CtrlAltLettersInAFieldStayTheFieldsAndOffItAreTheDesks()
    {
        await using var desk = new DeskHarness();
        var (window, canvas, _) = QuadRig.OpenMap(desk);
        var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled);
        Assert.True(box.Focus());
        DeskHarness.Pump();
        var theme = desk.Main.Prefs.Theme;

        foreach (var key in new[] { Key.M, Key.Z, Key.D })
            QuadRig.Key(window, key, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.False(desk.Session.MapAnything);
        Assert.False(desk.Main.Ux.Oops.IsOpen);
        Assert.Equal(theme, desk.Main.Prefs.Theme);

        // Off the field the keys are the desk's again.
        Assert.True(canvas.Focus());
        DeskHarness.Pump();
        QuadRig.Key(window, Key.Z, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.True(desk.Main.Ux.Oops.IsOpen);
        await desk.Main.Ux.Oops.Fetching;
        window.Close();
    }
}
