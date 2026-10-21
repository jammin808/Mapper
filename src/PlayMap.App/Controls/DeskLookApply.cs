using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using PlayMap.App.Infrastructure;

namespace PlayMap.App.Controls;

/// <summary>Draws the desk in a palette: every token brush of the application (Theme/Tokens.axaml, each "&lt;token&gt;Brush", shared by
/// every view that names it) and every light's brush recoloured in place, and Fluent's own controls on the variant whose panels match.</summary>
public static class DeskLookApply
{
    public static void Apply(Application app, DeskPalette palette)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(palette);
        foreach (var (token, rgb) in palette.Tokens)
        {
            if (app.Resources.TryGetResource(token + "Brush", null, out var found) && found is SolidColorBrush brush)
                brush.Color = LightBrushConverter.Brushes.Rgb(rgb);
        }
        LightBrushConverter.Brushes.Recolour(palette);
        var variant = palette.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        if (app.RequestedThemeVariant != variant)
            app.RequestedThemeVariant = variant;
    }

    /// <summary>The colour a token's application brush is drawn in now (tests read the look back here).</summary>
    public static uint? Drawn(Application app, string token)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Resources.TryGetResource(token + "Brush", null, out var found) || found is not SolidColorBrush brush)
            return null;
        var c = brush.Color;
        return ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }
}
