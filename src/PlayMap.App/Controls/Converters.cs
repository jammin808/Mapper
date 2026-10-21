using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PlayMap.Core.Content;

namespace PlayMap.App.Controls;

/// <summary>An icon key ("play") → its geometry from the theme ("Icon.play"); null when unknown.</summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current is { } app && app.TryGetResource("Icon." + key, app.ActualThemeVariant, out var g) ? g : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>A light word ("green", "amber", "red", "grey") or a #RRGGBB colour → a brush.</summary>
public sealed class LightBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Brushes.For(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;

    /// <summary>The desk's shared brushes for lights and model colours. A status word's brush is its status token's, one instance per
    /// token, recoloured in place when the theme changes (<see cref="Recolour"/>), so every light already drawn follows the theme; a
    /// model colour (#RRGGBB) is its own.</summary>
    public static class Brushes
    {
        private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SolidColorBrush> Tokens = new(StringComparer.Ordinal);

        /// <summary>The blind draft's colour (the chip, the window's frame): distinct from every status hue and from armed red.</summary>
        public const uint Blind = 0x6E4FD8;

        public static IBrush For(string? word)
        {
            var key = word ?? "grey";
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var b))
                    return b;
                IBrush brush = key.ToLowerInvariant() switch
                {
                    "accent" => TokenBrush("DeskAccent"),
                    "blind" => new SolidColorBrush(Rgb(Blind)).ToImmutable(),
                    _ when Infrastructure.StatusShapes.StatusOf(key) != "off" => TokenBrush(Infrastructure.StatusShapes.Token(key)),
                    _ when ColorText.TryParse(key, out var rgb) => new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)).ToImmutable(),
                    _ => TokenBrush("LightGrey"),
                };
                Cache[key] = brush;
                return brush;
            }
        }

        /// <summary>Recolours every token brush handed out to the palette's colour (the theme moved).</summary>
        public static void Recolour(Infrastructure.DeskPalette palette)
        {
            ArgumentNullException.ThrowIfNull(palette);
            lock (Cache)
            {
                foreach (var (token, brush) in Tokens)
                {
                    if (palette.Tokens.TryGetValue(token, out var rgb))
                        brush.Color = Rgb(rgb);
                }
                Palette = palette;
            }
        }

        /// <summary>The palette the token brushes are drawn in now.</summary>
        public static Infrastructure.DeskPalette Palette { get; private set; } = Infrastructure.DeskPalette.Dark;

        public static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static SolidColorBrush TokenBrush(string token)
        {
            if (!Tokens.TryGetValue(token, out var brush))
            {
                brush = new SolidColorBrush(Rgb(Palette.Tokens[token]));
                Tokens[token] = brush;
            }
            return brush;
        }
    }
}

/// <summary>Small converters the views share.</summary>
public static class Conv
{
    public static readonly IValueConverter BoldIf = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter Percent = new FuncValueConverter<double, string>(v => string.Create(CultureInfo.InvariantCulture, $"{v * 100:0} %"));

    public static readonly IValueConverter Number2 = new FuncValueConverter<double, string>(v => v.ToString("0.00", CultureInfo.InvariantCulture));
}
