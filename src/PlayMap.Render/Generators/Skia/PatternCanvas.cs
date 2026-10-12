using PlayMap.Core.Content;
using PlayMap.Core.Model;
using SkiaSharp;

namespace PlayMap.Render.Generators.Skia;

/// <summary>What a pattern draws into and with: the canvas at the picture's own size, the kit, the generator's
/// kind and parameters (read through the catalogue's schema) and who it draws for.</summary>
internal readonly record struct PatternCanvas(SKCanvas Canvas, SkiaKit Kit, int W, int H, GeneratorKind Kind, ParamSet Params, GeneratorContext Context)
{
    public double Num(string name) => ParamValues.Number(Params, Kind, name);

    public int Int(string name) => ParamValues.Integer(Params, Kind, name);

    public bool Flag(string name) => ParamValues.Flag(Params, Kind, name);

    public string Choice(string name) => ParamValues.Choice(Params, Kind, name);

    public string Text(string name) => ParamValues.Text(Params, Kind, name);

    public SKColor Color(string name, SKColor fallback) => Px.Color(ParamValues.Text(Params, Kind, name), fallback);

    /// <summary>A line width that is at least one device pixel. A pattern renders at its consumer's own
    /// raster, so a canvas pixel is a device pixel and the width is what was asked.</summary>
    public static int Hairline(int px) => Math.Max(1, px);

    /// <summary>Whether a spacing still reads (at least <paramref name="minPx"/> apart): minor lines closer
    /// than that, and labels smaller than that, are left out.</summary>
    public static bool Resolves(float px, float minPx = 3f) => px >= minPx;
}

/// <summary>A test pattern drawn on the CPU: all of a picture, from its parameters, once per change.</summary>
internal interface ISkiaPattern
{
    void Draw(in PatternCanvas p);
}
