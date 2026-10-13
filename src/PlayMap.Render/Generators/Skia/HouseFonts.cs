using SkiaSharp;

namespace PlayMap.Render.Generators.Skia;

/// <summary>The embedded Inter faces for pictures drawn outside the renderer (a timer view, a caption), so
/// their type is the same on every machine, stripped-down render nodes included. Shared, never disposed.</summary>
public static class HouseFonts
{
    public static SKTypeface Regular => Typefaces.Regular;

    public static SKTypeface SemiBold => Typefaces.SemiBold;
}
