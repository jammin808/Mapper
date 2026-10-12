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

    /// <summary>The desk's shared brushes for lights and model colours.</summary>
    public static class Brushes
    {
        private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static IBrush For(string? word)
        {
            var key = word ?? "grey";
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var b))
                    return b;
                var color = key switch
                {
                    "green" or "live" or "ready" or "good" => Color.Parse("#3FB950"),
                    "amber" or "stale" or "connecting" or "fetching" or "fair" => Color.Parse("#D29922"),
                    "red" or "down" or "missing" or "poor" or "unplayable" => Color.Parse("#E5484D"),
                    "accent" => Color.Parse("#3D9BFF"),
                    _ when ColorText.TryParse(key, out var rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb),
                    _ => Color.Parse("#6F7882"),
                };
                var brush = new SolidColorBrush(color).ToImmutable();
                Cache[key] = brush;
                return brush;
            }
        }
    }
}
